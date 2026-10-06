using DndMcp.Domain.Campaign;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;

namespace DndMcp.Domain.Combat;

/// <summary>
/// One encounter as the tracker reads and changes it: the <c>encounter</c> row's fight state and every combatant. Every
/// combat step is a pure function from one of these (and the step's input and rolled dice) to the next
/// (<see cref="CombatTracker"/>); the Repository reads it inside the step's transaction and writes back what changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not every member is a column.</b> <see cref="PlayerCampaign"/> is the campaign's role (contract D17: unknown HP by
/// default for an srd combatant in a player campaign, the "rolled here" note on enemy initiative), filled by the loader.
/// </para>
/// <para>
/// <b>Round convention (D15).</b> Round 0 is "not started"; the first <c>initiative</c> sets round 1 and the turn to the
/// top of the order. The turn pointer is a combatant id (<see cref="TurnCombatantId"/>), never a position, so adding or
/// leaving combatants never hands the turn to someone else (the PWA's bugs).
/// </para>
/// </remarks>
public sealed record EncounterState
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>"2014" or "2024": the fight's rules.</summary>
    public required string Ruleset { get; init; }

    /// <summary><see cref="CampaignValues.EncounterStatuses"/>.</summary>
    public string Status { get; init; } = CampaignValues.EncounterStatuses.Active;

    /// <summary>0 before the first initiative.</summary>
    public int Round { get; init; }

    public string? TurnCombatantId { get; init; }

    /// <summary>Fought in a lair: in-lair legendary counts and XP (2024 data), and the 2014 initiative-20 lair reminder.</summary>
    public bool Lair { get; init; }

    /// <summary>Every combatant, left ones included, in <c>order_key</c> order.</summary>
    public IReadOnlyList<CombatantState> Combatants { get; init; } = [];

    /// <summary>Not a column: the campaign is a player campaign (D17).</summary>
    public bool PlayerCampaign { get; init; }

    /// <summary>The combatant with this id, or null.</summary>
    public CombatantState? Find(string? id) => id is null ? null : Combatants.FirstOrDefault(c => c.Id == id);

    /// <summary>The combatant whose turn it is, or null (round 0).</summary>
    public CombatantState? TurnHolder => Find(TurnCombatantId);

    /// <summary>
    /// The turn order (contract D14, <see cref="CombatRules.OrderInitiative"/>): every combatant with an initiative, left
    /// and dead ones INCLUDED (their places still fire the expiries they anchor, §6.2), highest total first.
    /// </summary>
    public IReadOnlyList<CombatantState> Order => CombatOrder.Of(Combatants);

    /// <summary>
    /// The ids of the combatants whose stored state differs between <paramref name="before"/> and <paramref name="after"/>,
    /// added ones included, in <paramref name="after"/>'s order: the rows a step writes.
    /// </summary>
    public static IReadOnlyList<string> ChangedCombatants(EncounterState before, EncounterState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changed = new List<string>();
        foreach (var combatant in after.Combatants)
        {
            var old = before.Find(combatant.Id);
            if (old is null || !CombatJson.SameState(old, combatant))
            {
                changed.Add(combatant.Id);
            }
        }

        return changed;
    }
}

/// <summary>
/// One <c>combatant</c> row with its JSON parsed (contract §3, §4; <see cref="CombatJson"/> reads and writes the
/// columns). Immutable: the tracker returns new states.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hit points.</b> <see cref="Hp"/> and <see cref="MaxHp"/> are both null for a combatant whose hit points are
/// unknown (D17): it accumulates <see cref="DamageTaken"/> instead and is never defeated automatically. The effective
/// maximum is always <see cref="HitPointMath.EffectiveMaxHp"/>.
/// </para>
/// <para>
/// <b>Not a column:</b> <see cref="EntityHandle"/> (the linked entity's <c>kind:slug</c>, for addressing a combatant by
/// handle or slug and for the calls reminders print) and <see cref="EntitySubtype"/>, filled by the loader.
/// </para>
/// </remarks>
public sealed record CombatantState
{
    public required string Id { get; init; }

    public string? EntityId { get; init; }

    /// <summary>The tracker name (author-facing): the entity's name, the snapshot's monster name (copies numbered), or a typed name.</summary>
    public required string Name { get; init; }

    /// <summary><see cref="CampaignValues.CombatSides"/>.</summary>
    public required string Side { get; init; }

    /// <summary>The init group (identical monsters of one <c>add</c> entry: one initiative value, never a tie among them), or null.</summary>
    public string? InitGroup { get; init; }

    public string? SrdRef { get; init; }

    /// <summary>The whole normalized stat block at add (in-lair counts not applied), or null.</summary>
    public StatBlock? StatBlock { get; init; }

    /// <summary>The initiative total (decimals reorder ties), or null: not in the order yet.</summary>
    public double? Initiative { get; init; }

    public int InitBonus { get; init; }

    /// <summary>Base AC (a sheet's, a stat block's, or given); effects add to it in <see cref="DisplayedAc"/>.</summary>
    public int? Ac { get; init; }

    public int? MaxHp { get; init; }

    public int MaxHpReduction { get; init; }

    public int? Hp { get; init; }

    public int TempHp { get; init; }

    /// <summary>Damage taken by a combatant with unknown hit points (D17); healing lowers it, never below 0.</summary>
    public int DamageTaken { get; init; }

    public IReadOnlyList<CombatCondition> Conditions { get; init; } = [];

    public CombatConcentration? Concentration { get; init; }

    public DeathSaveTally DeathSaves { get; init; } = DeathSaveTally.Zero;

    /// <summary>It falls unconscious and makes death saves at 0 HP (a PC, sheet-seeded, or added with <c>death_saves: true</c>).</summary>
    public bool MakesDeathSaves { get; init; }

    public int Exhaustion { get; init; }

    public LegendaryState? Legendary { get; init; }

    /// <summary>The resources by key (<see cref="CombatValues.ResourceKeys"/>), in stored order.</summary>
    public IReadOnlyDictionary<string, CombatResource> Resources { get; init; } = new OrderedDictionary<string, CombatResource>(StringComparer.Ordinal);

    /// <summary>The sheet's state at add; non-null exactly for a sheet-seeded combatant (D19).</summary>
    public SheetSnapshot? SheetSnapshot { get; init; }

    public bool ReactionUsed { get; init; }

    /// <summary>Surprised: 2024 Disadvantage on its initiative roll (cleared once it has an initiative); 2014 it loses its first turn (cleared at that turn's end).</summary>
    public bool Surprised { get; init; }

    /// <summary>Hidden from the party's views (an ambusher); <c>set {hidden: false}</c> reveals it.</summary>
    public bool Hidden { get; init; }

    /// <summary>Enemy or neutral side only: dead, at 0 HP, knocked out, or held at 0 by a trait (D16).</summary>
    public bool Defeated { get; init; }

    public bool Dead { get; init; }

    /// <summary>It left the fight (<c>leave</c>): out of the turn order, its place still fires the expiries it anchors.</summary>
    public bool Removed { get; init; }

    public double OrderKey { get; init; }

    public string? CreatedAt { get; init; }

    public string? UpdatedAt { get; init; }

    /// <summary>Not a column: the linked entity's handle (<c>character:torch</c>), filled by the loader.</summary>
    public string? EntityHandle { get; init; }

    /// <summary>Not a column: the linked character entity's subtype ("pc", "npc"), filled by the loader.</summary>
    public string? EntitySubtype { get; init; }

    /// <summary>Sheet-seeded (D19): added as a character with a sheet, written back at <c>end</c>.</summary>
    public bool IsSheetSeeded => SheetSnapshot is not null;

    /// <summary>A combatant seeded from a sheet (<see cref="CombatantFactory.FromSheet"/>).</summary>
    public static CombatantState FromSheet(Characters.CharacterSheet sheet, string id, string name, string edition, int round = 0, double orderKey = 1) =>
        CombatantFactory.FromSheet(sheet, id, name, edition, round, orderKey);

    /// <summary>A combatant from a stat block (<see cref="CombatantFactory.FromStatBlock"/>).</summary>
    public static CombatantState FromStatBlock(StatBlock block, HpChoice hp, bool lair, string id, string name, int? rolledHp = null, double orderKey = 1) =>
        CombatantFactory.FromStatBlock(block, hp, lair, id, name, rolledHp, orderKey);

    /// <summary>Its hit points are known (not D17's unknown).</summary>
    public bool HpKnown => Hp is not null && MaxHp is not null;

    /// <summary>The linked entity's slug ("torch" of "character:torch"), or null.</summary>
    public string? EntitySlug => EntityHandle is { } handle && handle.IndexOf(':') is var i and >= 0 ? handle[(i + 1)..] : null;

    /// <summary>
    /// How calls address it: the entity slug for a character-linked combatant that is not played from a stat block
    /// ("belmakor"), else its tracker name as a slug ("mummy-2"); both resolve through <see cref="CombatAddressing"/>. The
    /// tracker name's slug is never cut: an entity slug stops at <see cref="CampaignSlugs.MaxLength"/> (60) characters, but
    /// a tracker name may have <see cref="CombatTracker.MaxNameLength"/> (80), and a cut one ("aaaa…" for "Aaaa… 2") matches
    /// every copy by prefix, so the call a reminder prints would be refused as ambiguous.
    /// </summary>
    public string Address => StatBlock is null && EntitySlug is { Length: > 0 } slug ? slug : TrackerAddress(Name);

    private static string TrackerAddress(string name)
    {
        var slug = CampaignSlugs.From(name, name);
        var whole = CampaignText.Key(name).Replace(' ', '-');
        return whole.Length > slug.Length && whole.StartsWith(slug, StringComparison.Ordinal) ? whole : slug;
    }

    /// <summary>The effective maximum (<see cref="HitPointMath.EffectiveMaxHp"/>), or null with unknown hit points.</summary>
    public int? EffectiveMaxHp(string edition) => MaxHp is { } max ? HitPointMath.EffectiveMaxHp(max, MaxHpReduction, Exhaustion, edition) : null;

    /// <summary>Whether it has a condition of this name (canonical names compared ignoring case).</summary>
    public bool Has(string name) => Conditions.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A 2024 knock-out: an Unconscious marked <see cref="CombatCondition.KnockOut"/>.</summary>
    public bool KnockedOut => Conditions.Any(c => c.KnockOut && string.Equals(c.Name, C.Unconscious, StringComparison.Ordinal));

    /// <summary>It is or includes Incapacitated (<see cref="CombatRules.IsIncapacitating"/>).</summary>
    public bool Incapacitated => Conditions.Any(c => CombatRules.IsIncapacitating(c.Name));

    /// <summary>At 0 HP, making death saves, not stable, not dead (hit points known).</summary>
    public bool Dying => MakesDeathSaves && !Dead && Hp == 0 && MaxHp is not null && !DeathSaves.Stable;

    /// <summary>Base AC plus every active effect's <c>ac</c>, or null without an AC.</summary>
    public int? DisplayedAc => Ac is { } ac ? ac + Conditions.Sum(c => c.Effect?.Ac ?? 0) : null;

    /// <summary>Left, dead, or defeated and unconscious without death saves: <c>next</c> passes over it (D16).</summary>
    public bool Skipped => Removed || Dead || (Defeated && !MakesDeathSaves && Has(C.Unconscious));

    /// <summary>
    /// The hit-point state the shared rules read (contract §5); null with unknown hit points. <c>KnockedOut</c> is read
    /// from the knock-out Unconscious, <c>Concentrating</c> from <see cref="Concentration"/>.
    /// </summary>
    public HitPointState? HitPoints(string edition) => Hp is { } hp && MaxHp is { } max
        ? new HitPointState
        {
            Edition = edition,
            Hp = hp,
            MaxHp = max,
            MaxHpReduction = MaxHpReduction,
            TempHp = TempHp,
            Exhaustion = Exhaustion,
            MakesDeathSaves = MakesDeathSaves,
            DeathSaves = DeathSaves,
            Dead = Dead,
            KnockedOut = KnockedOut,
            Concentrating = Concentration is not null,
        }
        : null;
}
