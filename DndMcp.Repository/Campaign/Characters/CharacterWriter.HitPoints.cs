using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// Damage, healing and temporary hit points on a sheet, out of combat (contract §7.1, §5.1-§5.8): R's rules
/// (<see cref="CombatRules.Damage"/>, <see cref="CombatRules.Heal"/>, <see cref="CombatRules.GrantTempHp"/>) on the sheet's
/// hit point state, so a sheet takes a hit exactly as the tracker's combatant would. The sheet always makes death saves (it
/// is a character); its adjustments are its <c>defenses</c> plus the effects persisted on it from a fight (an
/// <c>until_removed</c> effect with resistances) and Petrified.
///
/// <para>
/// <b>What follows a hit on the sheet</b>, where there is no turn order: dropping to 0 adds Unconscious (until removed:
/// any heal ends it, and so does a rest once the sheet is above 0 HP) and never Prone (F2, review UR03: nothing on a
/// sheet would ever end a stored Prone, so a character who stood up hours ago walked into the next fight prone, with
/// Disadvantage on every attack; a fight seeds the sheet's Unconscious at 0 HP as the tracker's down state, Prone until it
/// stands included, §6.2), damage at 0 adds death-save
/// failures (three: dead, with the D19 status proposal), concentration ends at 0 or death (§5.8) or needs a Constitution
/// save whose DC the result gives with the call that ends it on a failure, and 2024 Bloodied is noted. Healing the dead is
/// refused (three failed death saves, Exhaustion 6, or an effective maximum of 0), naming the cause only when the sheet
/// shows it: three failures are also how massive damage is stored, so they name none.
/// </para>
/// </summary>
public sealed partial class CharacterWriter
{
    /// <summary><c>damage {amount, damage_type}</c>: one damage instance on the sheet; during the fight, on the combatant.</summary>
    /// <exception cref="DndInputException">No amount or one out of range, an unknown damage type, no sheet, hit points not tracked.</exception>
    public CharacterWriteResult Damage(CampaignRow campaign, string? character, int? amount, string? damageType, WriteContext context)
    {
        var damage = Amount(amount, CharacterActions.Damage, "the damage taken");
        string? type = null;
        if (!string.IsNullOrWhiteSpace(damageType))
        {
            if (!DslValues.DamageTypes.Set.TryMatch(damageType, out type))
            {
                throw new DndInputException($"damage_type \"{WriteBatch.Echo(damageType)}\" is not a damage type; use {DslValues.DamageTypes.Set.List}, or leave it out for untyped damage.");
            }
        }

        var action = new CharacterAction { Kind = CharacterActions.Damage, Amount = damage, DamageType = type };
        return Run(campaign, character, CharacterActions.Damage, context, route: action, work: s =>
        {
            var sheet = s.RequireSheet();
            var state = HitPoints(sheet, s.Edition, s.Entity, s.Batch.Campaign);
            var outcome = CombatRules.Damage(state, new DamageRequest { Parts = [new DamageInstancePart(damage, type)] }, Adjustments(sheet));
            var notes = new List<string>();
            var more = new List<CharacterReminder>();
            if (outcome.NoEffect)
            {
                notes.Add($"No effect: {s.Entity.Name} is dead.");
                return s.Result(SheetDiff.Between(sheet, sheet), notes, []);
            }

            notes.Add(outcome.Arithmetic);
            var after = WithHitPoints(sheet, outcome.After);
            if (outcome.FellUnconscious)
            {
                after = WithConditions(after, [Unconscious], notes);
            }

            if (outcome.Died)
            {
                notes.Add($"{s.Entity.Name} died ({Cause(outcome.DeathCause)}).");
                more.Add(Died(s.Batch.Campaign, s.Entity));
            }
            else if (outcome.Dropped || outcome.DeathSaveFailures > 0 || outcome.StableLost)
            {
                if (outcome.DeathSaveFailures > 0)
                {
                    notes.Add($"Damage at 0 hit points: {N(outcome.DeathSaveFailures)} death save failure{(outcome.DeathSaveFailures == 1 ? string.Empty : "s")}.");
                }

                notes.Add($"{s.Entity.Name} is dying at 0 HP: {N(after.DeathSaves.Successes)} successes, {N(after.DeathSaves.Failures)} failures.");
            }

            if (outcome.BecameBloodied)
            {
                notes.Add("Bloodied.");
            }

            if (sheet.Concentration is { } held)
            {
                if (outcome.ConcentrationBroken || outcome.Died || outcome.After.Hp == 0)
                {
                    notes.Add($"Concentration on {held.Display} ended.");
                    after = after with { Concentration = null };
                }
                else if (outcome.ConcentrationDc is { } dc)
                {
                    more.Add(new CharacterReminder(CharacterReminderKinds.ConcentrationSave,
                        $"Concentration on {held.Display}: Constitution save DC {N(dc)}, {ConcentrationRoll(sheet, s.Edition)}. If it fails, end it:",
                        $"campaign_character {{\"action\": \"condition\", \"character\": \"{s.Entity.Handle}\", \"remove\": [\"concentration\"], \"campaign\": \"{s.Batch.Campaign.Slug}\"}}"));
                }
            }

            return s.Result(SheetDiff.Between(sheet, after), notes, [], more: more);
        });
    }

    /// <summary>
    /// <c>heal {amount}</c>: hit points regained on the sheet (never the dead); during the fight, on the combatant. A heal
    /// that regains hit points ends a stored Unconscious (F2, review CR06): from 0 it wakes the character, and above 0 a
    /// 2024 knock-out written back by a fight lasts until the creature regains any hit points (SRD 5.2.1, "Knocking Out a
    /// Creature"); any other stored Unconscious above 0 HP (a Sleep, an author's) ends on the sheet's heal too, a sheet
    /// convenience. A heal that regains nothing (the sheet at its maximum) wakes nobody (review F2R04). A stored Prone stays
    /// (an author may have set it), and the note says so.
    /// </summary>
    /// <exception cref="DndInputException">No amount or one out of range, no sheet, hit points not tracked, a dead character.</exception>
    public CharacterWriteResult Heal(CampaignRow campaign, string? character, int? amount, WriteContext context)
    {
        var healed = Amount(amount, CharacterActions.Heal, "the hit points regained");
        var action = new CharacterAction { Kind = CharacterActions.Heal, Amount = healed };
        return Run(campaign, character, CharacterActions.Heal, context, route: action, work: s =>
        {
            var sheet = s.RequireSheet();
            var state = HitPoints(sheet, s.Edition, s.Entity, s.Batch.Campaign);
            var outcome = CombatRules.Heal(state, healed);
            if (outcome.Refused)
            {
                throw new DndInputException($"{s.Entity.Name} is dead{DeadBecause(sheet, s.Edition)}: healing does not bring back the dead.");
            }

            var notes = new List<string> { $"HP {N(state.Hp)} → {N(outcome.After.Hp)} (+{N(outcome.Regained)})." };
            var after = WithHitPoints(sheet, outcome.After);
            if (outcome.Regained > 0)
            {
                after = WithoutUnconscious(after, notes);
            }

            return s.Result(SheetDiff.Between(sheet, after), notes, []);
        });
    }

    /// <summary><c>temp_hp {amount}</c>: temporary hit points (the higher of the two is kept); during the fight, on the combatant.</summary>
    /// <exception cref="DndInputException">No amount or one out of range, no sheet, hit points not tracked, a dead character.</exception>
    public CharacterWriteResult TempHp(CampaignRow campaign, string? character, int? amount, WriteContext context)
    {
        var temp = Amount(amount, CharacterActions.TempHp, "the temporary hit points gained");
        var action = new CharacterAction { Kind = CharacterActions.TempHp, Amount = temp };
        return Run(campaign, character, CharacterActions.TempHp, context, route: action, work: s =>
        {
            var sheet = s.RequireSheet();
            var state = HitPoints(sheet, s.Edition, s.Entity, s.Batch.Campaign);
            var outcome = CombatRules.GrantTempHp(state, temp);
            if (outcome.Refused)
            {
                throw new DndInputException($"{s.Entity.Name} is dead{DeadBecause(sheet, s.Edition)}: the dead gain no temporary hit points.");
            }

            var notes = new List<string>
            {
                outcome.After.TempHp == state.TempHp && state.TempHp > 0
                    ? $"Temporary HP stays {N(state.TempHp)}: the higher of {N(state.TempHp)} and {N(temp)} is kept."
                    : $"Temporary HP {N(state.TempHp)} → {N(outcome.After.TempHp)}.",
            };
            return s.Result(SheetDiff.Between(sheet, WithHitPoints(sheet, outcome.After)), notes, []);
        });
    }

    private const string Unconscious = "unconscious";
    private const string Prone = "prone";

    // An amount for damage, heal or temp_hp: required, 0 or more, at most R's guard.
    private static int Amount(int? amount, string action, string what)
    {
        if (amount is not { } value)
        {
            throw new DndInputException($"amount is required for {action}: {what}, e.g. {{\"action\": \"{action}\", \"amount\": 12}}.");
        }

        if (value < 0 || value > CombatRules.MaxDamagePart)
        {
            throw new DndInputException(
                $"amount is {N(value)}; give {what}, 0 to {CombatRules.MaxDamagePart.ToString("N0", CultureInfo.InvariantCulture)}" +
                (action == CharacterActions.Damage ? " (heal regains hit points)." : "."));
        }

        return value;
    }

    // The sheet's hit point state for R's rules; a sheet that does not track hit points cannot go through them (D17). The
    // fix call names the campaign last, as every printed call does: sent while another campaign is current, it would land
    // there.
    private static HitPointState HitPoints(CharacterSheet sheet, string edition, EntityRow entity, CampaignRow campaign)
    {
        if (sheet.MaxHp is not { } max || sheet.Hp is not { } hp)
        {
            throw new DndInputException(
                $"{entity.Name}'s sheet does not track hit points (no max_hp): give them with campaign_character {{\"action\": \"update\", " +
                $"\"character\": \"{entity.Handle}\", \"sheet\": {{\"max_hp\": …}}, \"campaign\": \"{campaign.Slug}\"}}.");
        }

        var tally = new DeathSaveTally(sheet.DeathSaves.Successes, sheet.DeathSaves.Failures, sheet.DeathSaves.Stable);
        return new HitPointState
        {
            Edition = edition,
            Hp = hp,
            MaxHp = max,
            MaxHpReduction = sheet.MaxHpReduction,
            TempHp = sheet.TempHp,
            Exhaustion = sheet.Exhaustion,
            MakesDeathSaves = true,
            DeathSaves = tally,
            Dead = sheet.IsDead(edition),
            Concentrating = sheet.Concentration is not null,
        };
    }

    // The sheet with R's resulting hit points, temporary hit points and death saves (unknown members of death_saves kept).
    private static CharacterSheet WithHitPoints(CharacterSheet sheet, HitPointState state) => sheet with
    {
        Hp = state.Hp,
        TempHp = state.TempHp,
        DeathSaves = sheet.DeathSaves with { Successes = state.DeathSaves.Successes, Failures = state.DeathSaves.Failures, Stable = state.DeathSaves.Stable },
    };

    /// <summary>
    /// The sheet without its stored Unconscious when it is above 0 HP (a heal, a rest: F2, review CR06), noting it; a stored
    /// Prone stays, as the note says. At 0 HP (or with none stored) the sheet as it is.
    /// </summary>
    internal static CharacterSheet WithoutUnconscious(CharacterSheet sheet, List<string> notes)
    {
        var unconscious = sheet.Conditions.Where(c => Is(c, Unconscious)).ToList();
        if (sheet.Hp is not > 0 || unconscious.Count == 0)
        {
            return sheet;
        }

        var after = sheet with { Conditions = sheet.Conditions.Except(unconscious).ToList() };
        notes.Add(after.Conditions.Any(c => Is(c, Prone)) ? "Unconscious ended (prone stays)." : "Unconscious ended.");
        return after;

        static bool Is(SheetCondition condition, string srd) => SheetValues.Conditions.Set.TryMatch(condition.Name, out var name) && name == srd;
    }

    // Adds SRD conditions the sheet does not have yet (until removed), noting each.
    private static CharacterSheet WithConditions(CharacterSheet sheet, IReadOnlyList<string> names, List<string> notes)
    {
        var conditions = sheet.Conditions.ToList();
        foreach (var name in names)
        {
            if (!conditions.Any(c => SheetValues.Conditions.Set.TryMatch(c.Name, out var have) && have == name))
            {
                conditions.Add(new SheetCondition(name, null, SheetValues.Durations.UntilRemoved, null, null));
                notes.Add($"Added {name} (until removed).");
            }
        }

        return sheet with { Conditions = conditions };
    }

    // The sheet's damage adjustments: its defenses, each persisted effect's resistances, and Petrified.
    private static DamageAdjustments Adjustments(CharacterSheet sheet)
    {
        var sets = new List<DamageAdjustments?>
        {
            DamageAdjustments.FromSheetDefenses(Types(sheet.Defenses.Resist), Types(sheet.Defenses.Immune), Types(sheet.Defenses.Vulnerable)),
        };
        foreach (var condition in sheet.Conditions)
        {
            if (SheetValues.Conditions.Set.TryMatch(condition.Name, out var srd) && srd == StatBlockValues.Conditions.Petrified)
            {
                sets.Add(new DamageAdjustments { Petrified = true });
            }

            if (condition.Effect is { } effect && Effect(condition.Name, effect) is { } adjustments)
            {
                sets.Add(adjustments);
            }
        }

        return DamageAdjustments.Combine(sets);
    }

    // A persisted effect object ({"resist":["all"],"except":["psychic"]}) as adjustments; null when it has none or is unreadable.
    private static DamageAdjustments? Effect(string name, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var resist = Types(List(root, "resist"));
            var immune = Types(List(root, "immune"));
            var vulnerable = Types(List(root, "vulnerable"));
            var except = List(root, "except").Where(t => DslValues.DamageTypes.Set.TryMatch(t, out _)).Select(t => Canonical(t)!).ToList();
            return resist.Count + immune.Count + vulnerable.Count == 0 ? null : DamageAdjustments.FromEffect(name, resist, immune, vulnerable, except);
        }
        catch (JsonException)
        {
            return null;
        }

        static IReadOnlyList<string> List(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];
    }

    // Canonical damage types and "all" (anything else a later version stored is ignored, never thrown on).
    private static IReadOnlyList<string> Types(IEnumerable<string> types) =>
        types.Select(t => t.Trim().Equals(DamageAdjustmentEntry.AllTypes, StringComparison.OrdinalIgnoreCase) ? DamageAdjustmentEntry.AllTypes : Canonical(t))
            .OfType<string>()
            .ToList();

    private static string? Canonical(string type) => DslValues.DamageTypes.Set.TryMatch(type, out var canonical) ? canonical : null;

    // Why a sheet is dead, for the refusal, when the sheet itself says (" (exhaustion 6)"); else nothing. Three failures are
    // only how a death is STORED (§4: massive damage and damage at 0 store them too), so they name no cause: "is dead
    // (three failed death saves)" for a character killed outright would say what did not happen.
    private static string DeadBecause(CharacterSheet sheet, string edition) =>
        sheet.Exhaustion >= SheetLimits.MaxExhaustion ? " (exhaustion 6)"
        : sheet.EffectiveMaxHp(edition) == 0 ? " (a hit point maximum of 0)"
        : string.Empty;

    private static string Cause(string? cause) => cause switch
    {
        DeathCauses.MassiveDamage => "massive damage",
        DeathCauses.DamageAtZero => "damage at 0 hit points of at least its maximum",
        DeathCauses.DeathSaveFailures => "three death save failures",
        DeathCauses.Exhaustion => "exhaustion 6",
        DeathCauses.MaxHpZero => "a hit point maximum of 0",
        _ => "0 hit points",
    };

    // The "died" reminder with the D19 status proposal: a ready campaign_write, sent as a dry run first, never applied here.
    private static CharacterReminder Died(CampaignRow campaign, EntityRow entity) =>
        new(SheetValues.ReminderKinds.Died,
            $"{entity.Name} died. If that stands, record it on the character (review its member_of relation and what it knows):",
            $"campaign_write {{\"ops\": [{{\"op\": \"{CampaignValues.OpKinds.Status}\", \"ref\": \"{entity.Handle}\", " +
            $"\"status\": \"{CampaignValues.Statuses.CharacterDead}\"}}], \"dry_run\": true, \"campaign\": \"{campaign.Slug}\"}}");

    private static string Signed(int value) => value >= 0 ? "+" + N(value) : N(value);

    /// <summary>
    /// How the sheet rolls its concentration save (contract §5.8), from R's rules so the sheet and the tracker agree: the
    /// Constitution save bonus (<see cref="CombatRules.SaveBonus(string, IReadOnlyDictionary{string, int}?, IEnumerable{string}?, IReadOnlyDictionary{string, int}?, int?)"/>),
    /// then <see cref="CombatRules.ConcentrationRoll"/>: − 2 × Exhaustion in 2024 (a save is a D20 Test), Disadvantage at
    /// 2014 Exhaustion 3 or more. "roll 1d20+3 (save bonus +7, −4 for Exhaustion 2)"; "roll 2d20kl1+5 (save bonus +5,
    /// Disadvantage for Exhaustion 3)". A plain save bonus there would tell a 2024 sheet at Exhaustion 2 it has +7 when its
    /// save is +3.
    /// </summary>
    internal static string ConcentrationRoll(CharacterSheet sheet, string edition)
    {
        var bonus = CombatRules.SaveBonus(DslValues.Abilities.Con, sheet.Abilities, sheet.Saves.Abilities, sheet.Saves.Bonus, sheet.ProficiencyBonus);
        var roll = CombatRules.ConcentrationRoll(bonus, sheet.Exhaustion, edition);
        var why = roll.Mode == Domain.Probability.D20Mode.Disadvantage ? $", Disadvantage for Exhaustion {N(sheet.Exhaustion)}"
            : roll.Modifier != bonus ? $", −{N(bonus - roll.Modifier)} for Exhaustion {N(sheet.Exhaustion)}"
            : string.Empty;
        return $"roll {roll.Expression} (save bonus {Signed(bonus)}{why})";
    }
}
