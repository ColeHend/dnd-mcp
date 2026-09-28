using DndMcp.Domain.Dpr;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using Xunit;
using DamageAdjustment = DndMcp.Domain.Simulation.DamageAdjustment;
using Q = DndMcp.Domain.Simulation.StatBlockValues.DamageQualifiers;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: a stat block target reads as that monster wherever the balance results name the target — the headline, the
/// target block (edition, CR, qualifiers kept whole, condition immunities, traits), the notes (where a qualified
/// resistance applied and why), the breakdown (a condition the target is immune to is "never attempted", not a bare 0%),
/// the signature effects and the sources — while a target given by numbers prints exactly as before.
///
/// <para>
/// These call the Domain and the formatters directly with hand-built stat blocks: the host's lookup of target.monster and
/// the monster normalizer are pinned elsewhere, and the text must not depend on either.
/// </para>
/// </summary>
public sealed class BalanceTargetMarkdownTests
{
    private const string Sword =
        """{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"PROPS] }""";

    private static string Fighter(string properties = "", string modifiers = "[]") =>
        $$"""{ "name": "Fighter", "edition": "2024", "level": 5, "abilities": {"str": 18}, "attacks": [{{Sword.Replace("PROPS", properties, StringComparison.Ordinal)}}], "modifiers": {{modifiers}} }""";

    private const string StunningStrike =
        """[{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15 }]""";

    [Fact]
    public void Target_StatBlock_NamesTheMonsterAndKeepsItsQualifiers()
    {
        var target = TargetResolver.Resolve(null, 5, Werewolf());

        var text = BalanceMarkdownText.Target(target, "## Target");

        Assert.Equal(
            "## Target\n\nWerewolf (2014 SRD stat block), CR 3. AC 11 (Werewolf stat block). Saves Str +2, Dex +1, Con +2, Int +0, " +
            "Wis +0, Cha +0 (Werewolf stat block). 58 hit points; immune to the charmed condition. Resists cold; bludgeoning, " +
            "piercing and slashing from nonmagical attacks that aren't silvered.",
            text);
    }

    [Fact]
    public void Target_LegendaryCaster_ListsItsTraitsFromTheStatBlock()
    {
        var target = TargetResolver.Resolve(null, 9, Caster());

        var text = BalanceMarkdownText.Target(target, "## Target");

        Assert.Contains("Archlich (2024 SRD stat block), CR 17.", text, StringComparison.Ordinal);
        Assert.Contains(
            "199 hit points; immune to fire; immune to the frightened and stunned conditions; Magic Resistance (Advantage on saves " +
            "against magical effects); 3 Legendary Resistances.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Target_GivenByNumbers_PrintsAsBefore()
    {
        var target = TargetResolver.Resolve(DslJson.Deserialize<TargetSpec>("""{ "ac": 15, "resistances": ["fire", "cold"] }""", "target"), 5);

        Assert.Equal(
            "## T\n\nAC 15 (given). Saves +2 on every save (the typical save bonus by CR from The Finished Book's \"Baseline Monster " +
            "Stats\" (tomedunn; an average of published monsters, not a DMG table) for CR 5). Resists cold, fire.",
            BalanceMarkdownText.Target(target, "## T"));
    }

    [Theory]
    [InlineData("", "Resistance to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered: applied to Longsword (neither magical nor silvered).")]
    [InlineData(", \"silvered\"", "Resistance to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered: not applied to Longsword (silvered).")]
    public void Format_Dpr_NamesTheMonsterAndSaysWhereItsQualifiedResistanceApplied(string properties, string note)
    {
        var report = DprAnalysis.Analyze(new DprRequest
        {
            Build = DslJson.Deserialize<BuildSpec>(Fighter(properties), "build"),
            Target = new TargetSpec { Monster = "werewolf" },
            TargetMonster = Werewolf(),
        });

        var text = BalanceDprMarkdown.Format(report);

        Assert.Contains("damage per round at level 5 against Werewolf (2014 SRD stat block), AC 11 — ", text, StringComparison.Ordinal);
        Assert.Contains("- " + note, text, StringComparison.Ordinal);
        Assert.Contains("the target's stat block: the 2014 SRD's Werewolf (rules_get ref \"2014/monster/werewolf\")", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BalanceMarkdownText.DmgRowSource, text, StringComparison.Ordinal);
        Assert.Contains("P(≥ 58, the target's hit points)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Dpr_StunImmuneTargetSaysNeverAttemptedAndNeverLands()
    {
        var modifiers =
            """[{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15 }, """ +
            """{ "kind": "save_effect", "name": "Hold Person", "ability": "wis", "dc": 15, "condition": "paralyzed", "action_cost": "none", "resource": {"uses": 1, "per": "long_rest"} }]""";
        var report = DprAnalysis.Analyze(new DprRequest
        {
            Build = DslJson.Deserialize<BuildSpec>(Fighter(modifiers: modifiers), "build"),
            TargetMonster = Horror(),
        });

        var text = BalanceDprMarkdown.Format(report);

        Assert.Contains("- Stunning Strike: never attempted, since the target is immune to the stunned condition.", text, StringComparison.Ordinal);
        Assert.Contains(" Paralyzed: never lands (the target is immune to it).", text, StringComparison.Ordinal);
        Assert.Contains("Stunning Strike: the Helmed Horror is immune to the stunned condition, so it is never attempted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the target is stunned for the rest of a turn", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Compare_StunningStrikeOnAStunImmuneMonster_MarksTheSignatureEffectImmune()
    {
        var report = DprComparison.Compare(new CompareRequest
        {
            Baseline = DslJson.Deserialize<BuildSpec>(Fighter(), "baseline"),
            Feature = DslJson.Deserialize<FeatureSpec>($$"""{ "name": "Stunning Strike", "modifiers": {{StunningStrike}} }""", "feature"),
            Target = new TargetSpec { Monster = "helmed horror" },
            TargetMonster = Horror(),
        });

        var text = BalanceCompareMarkdown.Format(report);

        Assert.Contains("| Stunning Strike | variant (new) | stunned | 0% (immune) | 0% (immune) |", text, StringComparison.Ordinal);
        Assert.Contains("against Helmed Horror (2014 SRD stat block), AC 20 — ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Dpr_EmpiricalProfile_EchoesTheProfileAndCitesTheTable()
    {
        var report = DprAnalysis.Analyze(new DprRequest
        {
            Build = DslJson.Deserialize<BuildSpec>(Fighter(), "build"),
            Target = DslJson.Deserialize<TargetSpec>("""{ "profile": "mm2014" }""", "target"),
            Levels = [4],
        });

        var text = BalanceDprMarkdown.Format(report);

        Assert.Contains(
            "## Target at level 4\n\nAC 12 (2014 SRD monster medians for CR 4 (11 monsters; CR = level 4)). Saves +1 on every save (the median " +
            "of the 2014 SRD's CR 4 monsters' mean save bonuses (0.67, rounded)).",
            text,
            StringComparison.Ordinal);
        Assert.Contains("- Target profile mm2014: AC 12 (median 12) and +1 on every save (median mean save bonus 0.67)", text, StringComparison.Ordinal);
        Assert.Contains(
            $"the warlock baseline {BalanceMarkdownText.Dpr(ReferenceCurves.At(4, "mm2014").WarlockBaseline)} (against the mm2014 target for CR 4).",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"profile mm2014: {MonsterStatsEmpirical.Source} (the 2014 rows of rules_get ref \"rules://tables/monster-stats-by-cr-empirical\";",
            text,
            StringComparison.Ordinal);
        Assert.Contains("(the warlock baseline here against the mm2014 target for CR = level; RPGBOT's target stays the DMG 2014 hit points ÷ 12)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BalanceMarkdownText.DmgRowSource, text, StringComparison.Ordinal);
        Assert.DoesNotContain("The Finished Book", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Compare_EmpiricalProfile_SaysWhichTargetsTheSlopesUse()
    {
        var report = DprComparison.Compare(new CompareRequest
        {
            Baseline = DslJson.Deserialize<BuildSpec>(Fighter(), "baseline"),
            Feature = DslJson.Deserialize<FeatureSpec>("""{ "name": "Savage Attacker", "modifiers": [{ "kind": "reroll_damage_take_best" }] }""", "feature"),
            Target = DslJson.Deserialize<TargetSpec>("""{ "profile": "mm2024" }""", "target"),
        });

        var text = BalanceCompareMarkdown.Format(report);

        Assert.Contains(
            "(the same horizon and target; under profile mm2024 the target at each level is the 2024 SRD medians for CR = that level):",
            text,
            StringComparison.Ordinal);
        Assert.Contains($"profile mm2024: {MonsterStatsEmpirical.Source}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Dpr_DefaultProfile_PrintsAsBefore()
    {
        var report = DprAnalysis.Analyze(new DprRequest { Build = DslJson.Deserialize<BuildSpec>(Fighter(), "build") });

        var text = BalanceDprMarkdown.Format(report);

        Assert.Contains("For scale at level 5: RPGBOT's target 12.08, the warlock baseline 17.80.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("profile", text, StringComparison.Ordinal);
    }

    // Werewolf-like (2014 numbers), with RESISTANCE to B/P/S from nonmagical attacks that aren't silvered and to cold, and
    // a charmed immunity to show the condition list.
    private static StatBlock Werewolf() => Create(
        "Werewolf", "2014", "3", 11, 58, new ResolvedAbilities(15, 13, 14, 10, 11, 10),
        resistances:
        [
            new DamageAdjustment("cold", null, "cold"),
            new DamageAdjustment("bludgeoning", Q.NonmagicalNotSilvered, "bludgeoning"),
            new DamageAdjustment("piercing", Q.NonmagicalNotSilvered, "piercing"),
            new DamageAdjustment("slashing", Q.NonmagicalNotSilvered, "slashing"),
        ],
        conditionImmunities: ["charmed"]);

    private static StatBlock Caster() => Create(
        "Archlich", "2024", "17", 17, 199, new ResolvedAbilities(11, 16, 16, 20, 14, 16),
        immunities: [new DamageAdjustment("fire", null, "fire")],
        conditionImmunities: ["frightened", "stunned"],
        traits: [new StatBlockTrait { Name = "Magic Resistance", Kind = StatBlockValues.TraitKinds.MagicResistance, Text = "Magic Resistance." }],
        legendaryResistance: 3);

    private static StatBlock Horror() => Create(
        "Helmed Horror", "2014", "4", 20, 60, new ResolvedAbilities(18, 13, 16, 10, 10, 10),
        conditionImmunities: ["paralyzed", "stunned"]);

    private static StatBlock Create(
        string name,
        string edition,
        string cr,
        int ac,
        int hp,
        ResolvedAbilities abilities,
        IReadOnlyList<DamageAdjustment>? resistances = null,
        IReadOnlyList<DamageAdjustment>? immunities = null,
        IReadOnlyList<string>? conditionImmunities = null,
        IReadOnlyList<StatBlockTrait>? traits = null,
        int legendaryResistance = 0)
    {
        var challenge = ChallengeRating.Parse(cr);
        return new StatBlock
        {
            Ref = $"{edition}/monster/{name.ToLowerInvariant().Replace(' ', '-')}",
            Name = name,
            Edition = edition,
            Size = "Medium",
            CreatureType = "monstrosity",
            ChallengeRating = challenge,
            Xp = ChallengeRatingTables.Xp(challenge),
            ProficiencyBonus = ChallengeRatingTables.ProficiencyBonus(challenge),
            ArmorClass = ac,
            HitPoints = hp,
            HitDice = DamageFormula.ParseDamage("9d8+18", "hit dice"),
            Abilities = abilities,
            SaveBonuses = DslValues.Abilities.All.ToDictionary(a => a, abilities.Modifier),
            InitiativeBonus = abilities.Modifier("dex"),
            Speeds = new Dictionary<string, int> { ["walk"] = 30 },
            Resistances = resistances ?? [],
            Immunities = immunities ?? [],
            Vulnerabilities = [],
            ConditionImmunities = conditionImmunities ?? [],
            Traits = traits ?? [],
            Actions = [],
            BonusActions = [],
            Reactions = [],
            Spells = [],
            SpellSlots = new Dictionary<int, int>(),
            Multiattacks = [],
            LegendaryResistance = legendaryResistance,
            Notes = [],
            Warnings = [],
        };
    }
}
