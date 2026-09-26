using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the documented quirks of the vendored monster and spell data as facts, with exact counts and, where it
/// matters, exact identities.
///
/// <para>
/// The Phase 5 normalizer and the 2024 spell overlay are written against these quirks. If a re-vendor fixes one
/// (upstream relabels the five half-damage breaths) or adds more (another monster with a descriptive multiattack
/// count), the corresponding override would silently become wrong or incomplete. Failing here turns that into a
/// deliberate, reviewed diff.
/// </para>
/// <para>
/// The count table reads the raw JSON, not the typed models, so it pins the DATA independently of how the models
/// surface it. The identity tests below it use the typed models, because that is how Phase 5 will see the data.
/// Every row cites the dnd5eapi research; where the data disagrees with the research, the data's number is pinned
/// and the difference is noted.
/// </para>
/// </summary>
public sealed partial class SrdDataQuirkTests
{
    private static readonly string[] ActionLists = ["actions", "bonus_actions", "legendary_actions", "reactions", "special_abilities"];

    private static readonly Dictionary<string, Func<string, int>> Counters = new(StringComparer.Ordinal)
    {
        // research §7.1: 41 (2024). All 41 are typed multiattack_type "actions".
        ["multiattack-with-actions-and-action-options"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("actions", out _) && a.Action.TryGetProperty("action_options", out _)),
        // research §7.1: 47 (2024).
        ["multiattack-any-combination"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("action_options", out var o) && Str(o, "desc") == "Any combination"),
        // research §7.1: 2014 6; 2024 "21 references to spells". The data has 21 unresolved references in 2024, but only
        // 20 name a spell. The 21st is erinyes "Entangling Rope" vs the action "Entangling Rope (Requires Magic Rope)".
        ["multiattack-references-matching-no-action"] = e => MultiattackReferences(e).Count(r => !r.MatchesAction),
        ["multiattack-references-naming-a-spell"] = e => MultiattackReferences(e).Count(r => !r.MatchesAction && r.MatchesSpell),
        // research §6 and the task brief said action_options counts are ints. In v7.0.0 every count is a string.
        ["multiattack-counts-as-json-numbers"] = e => MultiattackCountTokens(e).Count(t => t.ValueKind == JsonValueKind.Number),
        ["multiattack-counts-as-json-strings"] = e => MultiattackCountTokens(e).Count(t => t.ValueKind == JsonValueKind.String),
        // research §7.1 says 2014 action_options use option types action, multiple, breath and damage. In the data
        // action_options hold only action and multiple. Breath options (40) live in the separate `options` Choice, and
        // damage options only inside damage[] Choices (counted above and below).
        ["multiattack-options-of-type-action"] = e => MultiattackOptions(e).Count(o => Str(o, "option_type") == "action"),
        ["multiattack-options-of-type-multiple"] = e => MultiattackOptions(e).Count(o => Str(o, "option_type") == "multiple"),
        ["multiattack-options-of-other-types"] = e =>
            MultiattackOptions(e).Count(o => Str(o, "option_type") is not ("action" or "multiple")),
        ["breath-options"] = e =>
            Actions(e).Sum(a => a.Action.TryGetProperty("options", out var o)
                ? o.GetProperty("from").GetProperty("options").EnumerateArray().Count(x => Str(x, "option_type") == "breath")
                : 0),
        // research §7.2: 16 (2014), 0 (2024).
        ["damage-choice-entries"] = e => Actions(e).Sum(a => DamageEntries(a.Action).Count(d => d.TryGetProperty("choose", out _))),
        // research §7.2: 64 (2014), 148 (2024). 2014 counts only plain rolls: 67 attacks have >1 entries if Choices count.
        ["attacks-with-several-plain-damage-rolls"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("attack_bonus", out _) &&
                                  DamageEntries(a.Action).Count(d => !d.TryGetProperty("choose", out _)) > 1),
        // research §7.2: 2 (2014; assassin-style riders with their own save).
        ["damage-rolls-with-own-dc"] = e => Actions(e).Sum(a => DamageEntries(a.Action).Count(d => d.TryGetProperty("dc", out _))),
        // research §7.3: 19 flat "1" entries (2014); 2024 dropped flat damage entirely.
        ["flat-damage-dice"] = e =>
            Actions(e).Sum(a => DamageEntries(a.Action).Count(d => Str(d, "damage_dice") is { } dice && dice.All(char.IsAsciiDigit))),
        // research §7.3: "18 attacks have attack_bonus and an EMPTY damage[]" (2024). In the data the damage key is
        // ABSENT on those 18, never an empty array. 2014 also has 18 (non-damaging attacks such as Web).
        ["attack-bonus-without-damage-key"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("attack_bonus", out _) && !a.Action.TryGetProperty("damage", out _)),
        ["attack-bonus-with-empty-damage-array"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("attack_bonus", out _) &&
                                  a.Action.TryGetProperty("damage", out var d) && d.GetArrayLength() == 0),
        // research §7.4: 1 (2014, aboleth rider), 48 (2024), of which 28 are grapple escape DCs.
        ["attack-bonus-with-dc"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("attack_bonus", out _) && a.Action.TryGetProperty("dc", out _)),
        ["grapple-escape-dcs"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("attack_bonus", out _) && a.Action.TryGetProperty("dc", out _) &&
                                  Str(a.Action, "desc")!.Contains("escape", StringComparison.OrdinalIgnoreCase)),
        // research §7.4: 5 (2014), 0 (2024).
        ["save-labelled-none-but-prose-says-half"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("dc", out var dc) && Str(dc, "success_type") == SaveSuccessTypes.None &&
                                  Str(a.Action, "desc")!.Contains("half as much", StringComparison.Ordinal)),
        // Not in the research: 2024 zombie / ogre zombie Undead Fortitude ("DC 5 plus the damage taken").
        ["dc-without-value"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("dc", out var dc) && !dc.TryGetProperty("dc_value", out _)),
        // research §7.5: 65 (2014); 2024 "72 on actions plus 15 on bonus actions". The data adds 1 on a reaction: 88.
        ["recharge-on-roll"] = e =>
            Actions(e).Count(a => a.Action.TryGetProperty("usage", out var u) && Str(u, "type") == UsageTypes.RechargeOnRoll),
        // research §7.6: 35 × "(Costs 2 Actions)", 6 × "(Costs 3 Actions)" (2014 names only).
        ["legendary-cost-2-in-name"] = e => Actions(e).Count(a => a.List == "legendary_actions" && Str(a.Action, "name")!.Contains("(Costs 2 Actions)")),
        ["legendary-cost-3-in-name"] = e => Actions(e).Count(a => a.List == "legendary_actions" && Str(a.Action, "name")!.Contains("(Costs 3 Actions)")),
        // research §7.6: 44 (2024). The prose uses a typographic apostrophe (can’t), so match either.
        ["legendary-once-per-turn-in-prose"] = e =>
            Actions(e).Count(a => a.List == "legendary_actions" && OncePerTurn().IsMatch(Str(a.Action, "desc")!)),
        // research §7.6: 29 (2024).
        ["monsters-with-xp-in-lair"] = e => Monsters(e).Count(m => m.TryGetProperty("xp_in_lair", out _)),
        // research §7.7: 36 (2014, all traits), 90 (2024: 60 actions, 17 bonus, 7 legendary, 3 reactions, 3 traits).
        ["spellcasting-blocks"] = e => Actions(e).Count(a => a.Action.TryGetProperty("spellcasting", out _)),
        // research §7.9: 7 (2014).
        ["monsters-with-several-ac-entries"] = e => Monsters(e).Count(m => m.GetProperty("armor_class").GetArrayLength() > 1),
        // research §2 / §3: 0 of 339 2024 spells have dc, area_of_effect or heal_at_slot_level.
        ["spells-with-dc"] = e => Spells(e).Count(s => s.TryGetProperty("dc", out _)),
        ["spells-with-area-of-effect"] = e => Spells(e).Count(s => s.TryGetProperty("area_of_effect", out _)),
        ["spells-with-heal-at-slot-level"] = e => Spells(e).Count(s => s.TryGetProperty("heal_at_slot_level", out _)),
    };

    public static TheoryData<string, string, int> PinnedQuirkCounts => new()
    {
        { "multiattack-with-actions-and-action-options", SrdEdition.Edition2014, 0 },
        { "multiattack-with-actions-and-action-options", SrdEdition.Edition2024, 41 },
        { "multiattack-any-combination", SrdEdition.Edition2014, 0 },
        { "multiattack-any-combination", SrdEdition.Edition2024, 47 },
        { "multiattack-references-matching-no-action", SrdEdition.Edition2014, 6 },
        { "multiattack-references-matching-no-action", SrdEdition.Edition2024, 21 },
        { "multiattack-references-naming-a-spell", SrdEdition.Edition2014, 0 },
        { "multiattack-references-naming-a-spell", SrdEdition.Edition2024, 20 },
        { "multiattack-counts-as-json-numbers", SrdEdition.Edition2014, 0 },
        { "multiattack-counts-as-json-numbers", SrdEdition.Edition2024, 0 },
        { "multiattack-counts-as-json-strings", SrdEdition.Edition2014, 369 },
        { "multiattack-counts-as-json-strings", SrdEdition.Edition2024, 379 },
        { "multiattack-options-of-type-action", SrdEdition.Edition2014, 39 },
        { "multiattack-options-of-type-action", SrdEdition.Edition2024, 208 },
        { "multiattack-options-of-type-multiple", SrdEdition.Edition2014, 53 },
        { "multiattack-options-of-type-multiple", SrdEdition.Edition2024, 2 },
        { "multiattack-options-of-other-types", SrdEdition.Edition2014, 0 },
        { "multiattack-options-of-other-types", SrdEdition.Edition2024, 0 },
        { "breath-options", SrdEdition.Edition2014, 40 },
        { "breath-options", SrdEdition.Edition2024, 0 },
        { "damage-choice-entries", SrdEdition.Edition2014, 16 },
        { "damage-choice-entries", SrdEdition.Edition2024, 0 },
        { "attacks-with-several-plain-damage-rolls", SrdEdition.Edition2014, 64 },
        { "attacks-with-several-plain-damage-rolls", SrdEdition.Edition2024, 148 },
        { "damage-rolls-with-own-dc", SrdEdition.Edition2014, 2 },
        { "damage-rolls-with-own-dc", SrdEdition.Edition2024, 0 },
        { "flat-damage-dice", SrdEdition.Edition2014, 19 },
        { "flat-damage-dice", SrdEdition.Edition2024, 0 },
        { "attack-bonus-without-damage-key", SrdEdition.Edition2014, 18 },
        { "attack-bonus-without-damage-key", SrdEdition.Edition2024, 18 },
        { "attack-bonus-with-empty-damage-array", SrdEdition.Edition2014, 0 },
        { "attack-bonus-with-empty-damage-array", SrdEdition.Edition2024, 0 },
        { "attack-bonus-with-dc", SrdEdition.Edition2014, 1 },
        { "attack-bonus-with-dc", SrdEdition.Edition2024, 48 },
        { "grapple-escape-dcs", SrdEdition.Edition2014, 0 },
        { "grapple-escape-dcs", SrdEdition.Edition2024, 28 },
        { "save-labelled-none-but-prose-says-half", SrdEdition.Edition2014, 5 },
        { "save-labelled-none-but-prose-says-half", SrdEdition.Edition2024, 0 },
        { "dc-without-value", SrdEdition.Edition2014, 0 },
        { "dc-without-value", SrdEdition.Edition2024, 2 },
        { "recharge-on-roll", SrdEdition.Edition2014, 65 },
        { "recharge-on-roll", SrdEdition.Edition2024, 88 },
        { "legendary-cost-2-in-name", SrdEdition.Edition2014, 35 },
        { "legendary-cost-2-in-name", SrdEdition.Edition2024, 0 },
        { "legendary-cost-3-in-name", SrdEdition.Edition2014, 6 },
        { "legendary-cost-3-in-name", SrdEdition.Edition2024, 0 },
        { "legendary-once-per-turn-in-prose", SrdEdition.Edition2014, 0 },
        { "legendary-once-per-turn-in-prose", SrdEdition.Edition2024, 44 },
        { "monsters-with-xp-in-lair", SrdEdition.Edition2014, 0 },
        { "monsters-with-xp-in-lair", SrdEdition.Edition2024, 29 },
        { "spellcasting-blocks", SrdEdition.Edition2014, 36 },
        { "spellcasting-blocks", SrdEdition.Edition2024, 90 },
        { "monsters-with-several-ac-entries", SrdEdition.Edition2014, 7 },
        { "monsters-with-several-ac-entries", SrdEdition.Edition2024, 0 },
        { "spells-with-dc", SrdEdition.Edition2014, 92 },
        { "spells-with-dc", SrdEdition.Edition2024, 0 },
        { "spells-with-area-of-effect", SrdEdition.Edition2014, 88 },
        { "spells-with-area-of-effect", SrdEdition.Edition2024, 0 },
        { "spells-with-heal-at-slot-level", SrdEdition.Edition2014, 10 },
        { "spells-with-heal-at-slot-level", SrdEdition.Edition2024, 0 },
    };

    [Theory]
    [MemberData(nameof(PinnedQuirkCounts))]
    public void QuirkCount_VendoredData_MatchesPinnedValue(string quirk, string edition, int expected)
    {
        Assert.Equal(expected, Counters[quirk](edition));
    }

    [Fact]
    public void PinnedQuirkCounts_CoverEveryCounterInBothEditions()
    {
        var pinned = PinnedQuirkCounts.Select(row => $"{row[0]}/{row[1]}").Order(StringComparer.Ordinal).ToList();
        var expected = Counters.Keys.SelectMany(q => SrdEdition.All.Select(e => $"{q}/{e}")).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, pinned);
    }

    // ---- Identities, through the typed models ---------------------------------------------------------------------

    [Theory]
    [InlineData(SrdEdition.Edition2014, "hydra: Number of Heads|violet-fungus: 1d4")]
    [InlineData(SrdEdition.Edition2024, "hydra: Number of Heads")]
    public void MultiattackCount_NonNumeric_OnlyTheKnownMonstersHaveOne(string edition, string expected)
    {
        var counts = edition == SrdEdition.Edition2014
            ? SrdTestContent.Monsters2014.SelectMany(m => Counts(m.Index, m.Actions?.Select(a => (a.Actions, a.ActionOptions))))
            : SrdTestContent.Monsters2024.SelectMany(m => Counts(m.Index, m.Actions?.Select(a => (a.Actions, a.ActionOptions))));

        var descriptive = counts.Where(c => !c.Count.IsFixed).Select(c => $"{c.Monster}: {c.Count.Text}").Order(StringComparer.Ordinal);

        Assert.Equal(expected, string.Join("|", descriptive));
    }

    [Fact]
    public void MultiattackCount_Hydra_HasTextAndNoFixedValueInBothEditions()
    {
        var bite2014 = Assert.Single(Multiattack(SrdTestContent.Monsters2014.Single(m => m.Index == "hydra").Actions!).Actions!);
        var bite2024 = Assert.Single(SrdTestContent.Monsters2024.Single(m => m.Index == "hydra").Actions!
            .Single(a => a.MultiattackType is not null).Actions!);

        Assert.Equal("Number of Heads", bite2014.Count.Text);
        Assert.Null(bite2014.Count.Value);
        Assert.Equal("Number of Heads", bite2024.Count.Text);
        Assert.Null(bite2024.Count.Value);
    }

    [Fact]
    public void Multiattack2024_WithBothLists_AreThe41TypedActions()
    {
        var both = All2024().Where(a => a.Action.Actions is not null && a.Action.ActionOptions is not null).ToList();

        Assert.Equal(41, both.Count);
        Assert.All(both, a => Assert.Equal(MultiattackTypes.Actions, a.Action.MultiattackType));
    }

    // The five 2014 half-damage saves upstream labels "none". Phase 5 overrides exactly these. If upstream fixes one,
    // or mislabels another, the override list is wrong and this fails.
    [Fact]
    public void MislabeledHalfSaves2014_AreExactlyTheFiveKnownActions()
    {
        var mislabeled = All2014()
            .Where(a => a.Action.Dc?.SuccessType == SaveSuccessTypes.None &&
                        a.Action.Desc.Contains("half as much damage", StringComparison.Ordinal))
            .Select(a => $"{a.Monster} / {a.Action.Name} / {a.Action.Dc!.DcType.Index} {a.Action.Dc.DcValue}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "adult-red-dragon / Fire Breath / dex 21",
                "ancient-white-dragon / Cold Breath / con 22",
                "green-dragon-wyrmling / Poison Breath / con 11",
                "lich / Disrupt Life (Costs 3 Actions) / con 18",
                "winter-wolf / Cold Breath / dex 12",
            ],
            mislabeled);
    }

    [Fact]
    public void MislabeledHalfSaves2024_NoneRemain()
    {
        Assert.DoesNotContain(All2024(), a =>
            a.Action.Dc?.SuccessType == SaveSuccessTypes.None &&
            a.Action.Desc.Contains("half as much damage", StringComparison.Ordinal));
    }

    [Fact]
    public void AttacksWithoutDamage2024_AreAbsentNotEmpty_AndAllButRoperDealFlatProseDamage()
    {
        var withoutDamage = All2024().Where(a => a.Action.AttackBonus is not null && a.Action.Damage is null).ToList();

        Assert.Equal(18, withoutDamage.Count);
        Assert.DoesNotContain(All2024(), a => a.Action.Damage is { Count: 0 });

        var noFlatDamage = withoutDamage.Where(a => !FlatProseHit().IsMatch(a.Action.Desc)).Select(a => $"{a.Monster} / {a.Action.Name}");
        Assert.Equal(["roper / Tentacle"], noFlatDamage);
    }

    [Fact]
    public void DcWithoutValue2024_IsOnlyUndeadFortitude()
    {
        var withoutValue = All2024()
            .Where(a => a.Action.Dc is { DcValue: null })
            .Select(a => $"{a.Monster} / {a.Action.Name}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(["ogre-zombie / Undead Fortitude", "zombie / Undead Fortitude"], withoutValue);
    }

    // ---- Raw JSON helpers ---------------------------------------------------------------------------------------

    private static IEnumerable<JsonElement> Monsters(string edition) =>
        SrdTestContent.Raw(edition, SrdFileNames.Monsters).EnumerateArray();

    private static IEnumerable<JsonElement> Spells(string edition) =>
        SrdTestContent.Raw(edition, SrdFileNames.Spells).EnumerateArray();

    private static IEnumerable<(string Monster, string List, JsonElement Action)> Actions(string edition) =>
        from monster in Monsters(edition)
        from list in ActionLists
        where monster.TryGetProperty(list, out _)
        from action in monster.GetProperty(list).EnumerateArray()
        select (monster.GetProperty("index").GetString()!, list, action);

    private static IEnumerable<JsonElement> DamageEntries(JsonElement action) =>
        action.TryGetProperty("damage", out var damage) ? damage.EnumerateArray() : [];

    private static IEnumerable<JsonElement> MultiattackOptions(string edition) =>
        Actions(edition).SelectMany(a => MultiattackOptions(a.Action));

    private static IEnumerable<JsonElement> MultiattackOptions(JsonElement action) =>
        action.TryGetProperty("action_options", out var options)
            ? options.GetProperty("from").GetProperty("options").EnumerateArray()
            : [];

    private static IEnumerable<JsonElement> MultiattackCountTokens(string edition)
    {
        foreach (var (_, _, action) in Actions(edition))
        {
            if (action.TryGetProperty("actions", out var routine))
            {
                foreach (var step in routine.EnumerateArray())
                {
                    yield return step.GetProperty("count");
                }
            }

            foreach (var option in MultiattackOptions(action))
            {
                if (option.TryGetProperty("count", out var count))
                {
                    yield return count;
                }

                if (option.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        yield return item.GetProperty("count");
                    }
                }
            }
        }
    }

    private static IEnumerable<(bool MatchesAction, bool MatchesSpell)> MultiattackReferences(string edition)
    {
        foreach (var monster in Monsters(edition))
        {
            var lists = ActionLists.Where(l => monster.TryGetProperty(l, out _)).SelectMany(l => monster.GetProperty(l).EnumerateArray()).ToList();
            var actionNames = lists.Select(a => Str(a, "name")!).ToHashSet(StringComparer.Ordinal);
            var spellNames = lists
                .Where(a => a.TryGetProperty("spellcasting", out _))
                .SelectMany(a => a.GetProperty("spellcasting").GetProperty("spells").EnumerateArray())
                .Select(s => Str(s, "name")!)
                .ToHashSet(StringComparer.Ordinal);

            if (!monster.TryGetProperty("actions", out var actions))
            {
                continue;
            }

            foreach (var action in actions.EnumerateArray())
            {
                var names = new List<string>();
                if (action.TryGetProperty("actions", out var routine))
                {
                    names.AddRange(routine.EnumerateArray().Select(s => Str(s, "action_name")!));
                }

                foreach (var option in MultiattackOptions(action))
                {
                    if (Str(option, "action_name") is { } name)
                    {
                        names.Add(name);
                    }

                    if (option.TryGetProperty("items", out var items))
                    {
                        names.AddRange(items.EnumerateArray().Select(i => Str(i, "action_name")!));
                    }
                }

                foreach (var name in names)
                {
                    yield return (actionNames.Contains(name), spellNames.Contains(name));
                }
            }
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("can[’']t take this action again until the start of its next turn")]
    private static partial Regex OncePerTurn();

    [GeneratedRegex(@"Hit: \d+ \w+ damage")]
    private static partial Regex FlatProseHit();

    // ---- Typed helpers ------------------------------------------------------------------------------------------

    private static IEnumerable<(string Monster, MonsterAction2014 Action)> All2014() =>
        from monster in SrdTestContent.Monsters2014
        from list in new[] { monster.SpecialAbilities, monster.Actions, monster.LegendaryActions, monster.Reactions }
        where list is not null
        from action in list
        select (monster.Index, action);

    private static IEnumerable<(string Monster, MonsterAction2024 Action)> All2024() =>
        from monster in SrdTestContent.Monsters2024
        from list in new[] { monster.SpecialAbilities, monster.Actions, monster.BonusActions, monster.LegendaryActions, monster.Reactions }
        where list is not null
        from action in list
        select (monster.Index, action);

    private static MonsterAction2014 Multiattack(IReadOnlyList<MonsterAction2014> actions) =>
        actions.Single(a => a.MultiattackType is not null);

    private static IEnumerable<(string Monster, MultiattackCount Count)> Counts(
        string monster,
        IEnumerable<(IReadOnlyList<MultiattackAction>? Routine, Choice<MultiattackOption>? Options)>? multiattacks)
    {
        foreach (var (routine, options) in multiattacks ?? [])
        {
            foreach (var step in routine ?? [])
            {
                yield return (monster, step.Count);
            }

            foreach (var option in options?.From.Options ?? [])
            {
                if (option.Count is not null)
                {
                    yield return (monster, option.Count);
                }

                foreach (var item in option.Items ?? [])
                {
                    yield return (monster, item.Count!);
                }
            }
        }
    }
}
