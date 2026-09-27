using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>encounter_difficulty</c> through the real client resolves each monster to the stat block (and edition)
/// it says it used, applies each edition's method to those numbers, and states every label with the numbers it was
/// judged on. Every caller mistake — a monster the SRD lacks, a malformed item, a wrong field — comes back as a message
/// that says what to send instead.
///
/// <para>
/// The arithmetic is pinned exactly in DndMcp.Tests; these pin what only the host decides: which stat block a name or
/// ref reaches in each edition, the corrected XP from srd.db, the argument checks inside <c>monsters</c>, and the text
/// the model reads.
/// </para>
/// </summary>
public sealed partial class EncounterToolTests : IClassFixture<McpServerHarness>
{
    private const string Prefix = "An error occurred invoking 'encounter_difficulty': ";

    private readonly McpServerHarness _server;

    public EncounterToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [GeneratedRegex(@"^## (?<edition>2014|2024) rules: (?<label>[A-Za-z ]+?)(?: \((?<effective>.+) at effective level (?<offset>[+−]\d+)\))?$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    private async Task<string> Success(string arguments) =>
        _server.SuccessText(await _server.CallToolJsonAsync("encounter_difficulty", arguments));

    private async Task<string> Error(string arguments) =>
        _server.ErrorText(await _server.CallToolJsonAsync("encounter_difficulty", arguments));

    private static Dictionary<string, string> Labels(string text) =>
        HeadingRegex().Matches(text).ToDictionary(m => m.Groups["edition"].Value, m => m.Groups["label"].Value);

    [Fact]
    public async Task CallTool_ThreeOgresBothEditions_Is2014MediumAnd2024Low()
    {
        // PLAN.md's manual check: "Is 3 ogres Deadly for four level-5s in 2014, and what is it in 2024?"
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"name":"Ogre","count":3}],"edition":"both"}""");

        Assert.Equal(new Dictionary<string, string> { ["2014"] = "Medium", ["2024"] = "Low" }, Labels(text));
        Assert.Contains("| Ogre (`2014/monster/ogre`, `2024/monster/ogre`) | 3 | CR 2, 450 XP | CR 2, 450 XP |", text, StringComparison.Ordinal);
        Assert.Contains("| 4 × level 5 | 1,000 | 2,000 | 3,000 | 4,400 |", text, StringComparison.Ordinal);
        Assert.Contains("- Monster XP 1,350 × 2 = **2,700 adjusted XP**: × 2 for 3 monsters (the 3–6 row, with 3–5 characters).", text, StringComparison.Ordinal);
        Assert.Contains("- That reaches Medium (2,000) but not Hard (3,000).", text, StringComparison.Ordinal);
        Assert.Contains("| 4 × level 5 | 2,000 | 3,000 | 4,400 |", text, StringComparison.Ordinal);
        Assert.Contains("- Monster XP **1,350** fits the Low budget (2,000), 67% of it.", text, StringComparison.Ordinal);
        Assert.Contains("## The editions compared", text, StringComparison.Ordinal);
        Assert.Contains("- At these levels the 2024 Low, Moderate and High budgets equal the 2014 Medium, Hard and Deadly thresholds.", text, StringComparison.Ordinal);
        Assert.Contains("- The labels are read in opposite directions: 2014 names a fight by the highest threshold", text, StringComparison.Ordinal);
        Assert.Contains("- 2014 judges 2,700 adjusted XP (× 2); 2024 judges the plain 1,350.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("XP differs between the editions", text, StringComparison.Ordinal);
        Assert.Contains("The party earns 1,350 XP (about 337 each)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EditionOmitted_Is2024Only()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"name":"Ogre","count":3}]}""");

        Assert.Equal(["2024"], Labels(text).Keys);
        Assert.Contains("`2024/monster/ogre`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2014", text.Split("*Sources:")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Dungeon Master's Guide", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2014_SourcesNameTheDmgNotTheSrd()
    {
        var text = await Success("""{"party":[5],"monsters":[{"name":"Ogre"}],"edition":"2014"}""");

        Assert.Contains("Dungeon Master's Guide (2014), pp. 82–84, also in the free 2014 Basic Rules (not SRD 5.1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SRD 5.2.1", text, StringComparison.Ordinal);
    }

    [Theory]
    // SRD 5.2.1's worked examples, through the tool: every monster by its SRD name.
    [InlineData("""{"party":[1,1,1,1],"monsters":[{"name":"Bugbear Warrior"}]}""", "Low", "200")]
    [InlineData("""{"party":[1,1,1,1],"monsters":[{"name":"Giant Rat","count":6}]}""", "Low", "150")]
    [InlineData("""{"party":[3,3,3,3,3],"monsters":[{"name":"Druid","count":2},{"name":"Stirge","count":9}]}""", "Moderate", "1,125")]
    [InlineData("""{"party":[3,3,3,3,3],"monsters":[{"name":"Wight"},{"name":"Warhorse Skeleton"},{"name":"Skeleton","count":6}]}""", "Moderate", "1,100")]
    [InlineData("""{"party":[15,15,15,15,15,15],"monsters":[{"name":"Adult Red Dragon","count":2},{"name":"Fire Giant","count":2}]}""", "High", "46,000")]
    public async Task CallTool_SrdWorkedExample_HasTheSrdsDifficulty(string arguments, string label, string xp)
    {
        var text = await Success(arguments);

        Assert.Equal(label, Labels(text)["2024"]);
        Assert.Contains($"Monster XP **{xp}**", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CorrectedMonster_UsesTheStatBlockXpFromTheIndex()
    {
        // Upstream gives the 2014 Dretch 25 XP; srd.db carries the correction to its stat block's 50.
        var text = await Success("""{"party":[1],"monsters":[{"ref":"2014/monster/dretch","count":2}],"edition":"2014"}""");

        Assert.Contains("| Dretch (`2014/monster/dretch`) | 1/4 | 50 | 2 | 100 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_PluralName_FindsTheMonster()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"name":"ogres","count":3}]}""");

        Assert.Contains("| Ogre (`2024/monster/ogre`) | 2 | 450 | 3 | 1,350 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RenamedMonsterBothEditions_UsesEachEditionsStatBlock()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"ref":"2014/monster/thug","count":2}],"edition":"both"}""");

        Assert.Contains("| Thug (2014) / Tough (2024) (`2014/monster/thug`, `2024/monster/tough`) | 2 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NameOnlyIn2024_Uses2024StatBlockFor2014WithANote()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"name":"Pirate Captain"}],"edition":"2014"}""");

        Assert.Contains("| Pirate Captain (`2024/monster/pirate-captain`) | 6 | 2,300 | 1 | 2,300 |", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Pirate Captain: the 2014 SRD has no counterpart of `2024/monster/pirate-captain`, so its 2024 stat block is used for the 2014 maths too.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_OtherEditionRefInOneEdition_IsUsedAsGivenWithANote()
    {
        var text = await Success("""{"party":[5],"monsters":[{"ref":"2014/monster/ogre"}]}""");

        Assert.Contains("| Ogre (`2014/monster/ogre`) |", text, StringComparison.Ordinal);
        Assert.Contains("- Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 encounter.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Lair_Uses2024InLairXpAndSays2014HasNone()
    {
        var text = await Success("""{"party":[17,17,17,17],"monsters":[{"name":"Adult Red Dragon","lair":true}],"edition":"both"}""");

        Assert.Contains("| CR 17, 18,000 XP | CR 17, 20,000 XP (in lair) |", text, StringComparison.Ordinal);
        Assert.Contains("- Adult Red Dragon (`2014/monster/adult-red-dragon`): its stat block gives no in-lair XP", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LairWithCr_Is2024NextCrXpAndSaysSo()
    {
        var text = await Success("""{"party":[17,17,17,17],"monsters":[{"cr":"17","name":"Dragon","lair":true}],"edition":"both"}""");

        Assert.Contains("| Dragon (by CR) | 1 | CR 17, 18,000 XP | CR 17, 20,000 XP (in lair) |", text, StringComparison.Ordinal);
        Assert.Contains("- Dragon: in its lair a CR 17 monster is worth the next CR's XP (20,000)", text, StringComparison.Ordinal);
        Assert.Contains("- Dragon: 2014 stat blocks give no in-lair XP, so lair changes nothing for 2014.", text, StringComparison.Ordinal);
        Assert.Contains("- The monsters' XP differs between the editions (18,000 in 2014, 20,000 in 2024): Dragon is worth 18,000 XP in 2014 and 20,000 (in lair) in 2024.", text, StringComparison.Ordinal);
        Assert.Contains("- 2014's multiplier is × 1 here, so each edition judges its own plain XP total.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LairWithCr30_HasNoRowAbove()
    {
        var text = await Success("""{"party":[20],"monsters":[{"cr":"30","name":"Tarrasque","lair":true}]}""");

        Assert.Contains("| Tarrasque (by CR) | 30 | 155,000 | 1 | 155,000 |", text, StringComparison.Ordinal);
        Assert.Contains("- Tarrasque: CR 30 has no row above it, so lair changes nothing; its normal XP is used.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TwoCrEntriesWithOneLabel_AreTwoStatBlocks()
    {
        // Keyed by label alone, the CR 8 Bandit merged into the CR 1 one and its warning vanished.
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"cr":"1","name":"Bandit"},{"cr":"8","name":"Bandit"}]}""");

        Assert.Contains("- Bandit is CR 8, above the party's level (5)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_OneLabelAtFourCrs_IsFourStatBlocks()
    {
        // Keyed by label alone, four "Cultist" items at different CRs counted as one stat block.
        var text = await Success(
            """{"party":[10,10],"monsters":[{"cr":"1","name":"Cultist"},{"cr":"2","name":"Cultist"},{"cr":"3","name":"Cultist"},{"cr":"4","name":"Cultist"}]}""");

        Assert.Contains("- 4 different stat blocks:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2024CrZeroStatBlock_SaysItIsZeroOrTen()
    {
        var text = await Success("""{"party":[5],"monsters":[{"name":"Frog","count":3}],"edition":"both"}""");

        Assert.Contains("| Frog (`2014/monster/frog`, `2024/monster/frog`) | 3 | CR 0, 0 XP | CR 0, 10 XP |", text, StringComparison.Ordinal);
        Assert.Contains("- Frog (`2024/monster/frog`): its SRD 5.2.1 stat block gives XP 0 or 10; 10 is used.", text, StringComparison.Ordinal);
        Assert.Contains("Frog is worth 0 XP in 2014 and 10 in 2024", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ExcludeIn2024Only_SaysItChangesNothing()
    {
        var text = await Success("""{"party":[5,5],"monsters":[{"cr":"1","exclude":true},{"cr":"3"}]}""");

        Assert.Contains("- exclude changes only the 2014 multiplier's count; the 2024 method counts every creature's XP.", text, StringComparison.Ordinal);
    }

    [Theory]
    // 175 of 200 is 87.5%: rounded down, never up to a share the total does not reach.
    [InlineData("""{"party":[1,1,1,1],"monsters":[{"cr":"1/8","count":7}]}""", "fits the Low budget (200), 87% of it.")]
    [InlineData("""{"party":[20,20,20,20],"monsters":[{"cr":"0"}]}""", "fits the Low budget (25,600), under 1% of it.")]
    public async Task CallTool_BudgetShare_IsRoundedDownAndNeverZeroForSomeXp(string arguments, string expected)
    {
        Assert.Contains(expected, await Success(arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Exclude_LeavesTheMultiplierCountAndSaysSo()
    {
        var text = await Success(
            """{"party":[10,10,10,10],"monsters":[{"name":"Young Red Dragon"},{"name":"Kobold","count":10,"exclude":true}],"edition":"2014"}""");

        Assert.Contains("× 1 for 1 counted monster, 10 excluded (the 1 row, with 3–5 characters)", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Left out of the multiplier's count: 10 × Kobold. Their XP still counts (the cautious reading of the DMG's rule).",
            text,
            StringComparison.Ordinal);
        Assert.Contains("· excluded from the 2014 multiplier count", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EffectiveLevelOffset_GivesBothLabels()
    {
        // Four level 5s against 2,400 XP in two monsters. 2014: × 1.5 = 3,600 adjusted, Hard at level 5 (3,000 / 4,400) and
        // at level 6 (3,600 / 5,600). 2024: over Low (2,000) at level 5, so Moderate; within Low (2,400) at level 6.
        var text = await Success(
            """{"party":[5,5,5,5],"monsters":[{"cr":"6","name":"Brute"},{"cr":"1/2","name":"Lackey"}],"edition":"both","effective_level_offset":1}""");

        var headings = HeadingRegex().Matches(text).ToDictionary(m => m.Groups["edition"].Value);
        Assert.Equal("the same", headings["2014"].Groups["effective"].Value);
        Assert.Equal(("Low", "+1"), (headings["2024"].Groups["effective"].Value, headings["2024"].Groups["offset"].Value));
        Assert.Contains("**Effective level:** +1 (4 characters at level 6).", text, StringComparison.Ordinal);
        Assert.Contains("| Effective level (+1) | 2,400 | 4,000 | 5,600 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ByCr_UsesTheXpTableAndTheLabel()
    {
        var text = await Success("""{"party":[3,3,3],"monsters":[{"cr":"1/2","name":"Bandit thug","count":3},{"cr":5}]}""");

        Assert.Contains("| Bandit thug (by CR) | 1/2 | 100 | 3 | 300 |", text, StringComparison.Ordinal);
        Assert.Contains("| CR 5 monster (by CR) | 5 | 1,800 | 1 | 1,800 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_CrZeroByCr_Uses10XpAndSaysSo()
    {
        var text = await Success("""{"party":[1],"monsters":[{"cr":"0","name":"Cat"}]}""");

        Assert.Contains("| Cat (by CR) | 0 | 10 | 1 | 10 |", text, StringComparison.Ordinal);
        Assert.Contains("- Cat: CR 0 is worth 0 or 10 XP by its stat block; 10 is used.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BeyondHighAndTrivial_AreNamedAsNotBookTerms()
    {
        var beyond = await Success("""{"party":[1],"monsters":[{"cr":"5"}],"edition":"both"}""");
        var trivial = await Success("""{"party":[20,20,20,20],"monsters":[{"cr":"1/8"}],"edition":"2014"}""");

        Assert.Equal(new Dictionary<string, string> { ["2014"] = "Deadly", ["2024"] = "Beyond High" }, Labels(beyond));
        Assert.Contains("The SRD's difficulties stop at High, so expect worse than High.", beyond, StringComparison.Ordinal);
        Assert.Contains("trivial, a label the DMG does not use", trivial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TroubleshootingApplies_ListsTheSrdAdvice()
    {
        var text = await Success("""{"party":[1,1],"monsters":[{"name":"Ogre"},{"name":"Goblin Warrior","count":4}]}""");

        Assert.Contains("**Troubleshooting (SRD 5.2.1):**", text, StringComparison.Ordinal);
        Assert.Contains("- 5 creatures against 2 characters is more than 2 per character", text, StringComparison.Ordinal);
        Assert.Contains("- Ogre is CR 2, above the party's level (1)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_MixedLevels2024_SaysEachBudgetIsSummed()
    {
        var text = await Success("""{"party":[5,4],"monsters":[{"name":"Ogre"}]}""");

        Assert.Contains("| **Party (2)** | **750** | **1,125** | **1,600** |", text, StringComparison.Ordinal);
        Assert.Contains("Mixed levels: each character's own budget is summed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SameArgumentsTwice_SameAnswer()
    {
        const string Arguments = """{"party":[5,5,5,5],"monsters":[{"name":"Ogre","count":3}],"edition":"both"}""";

        Assert.Equal(await Success(Arguments), await Success(Arguments));
    }

    [Fact]
    public async Task CallTool_LargestEncounter_StaysUnderTheOutputCeiling()
    {
        // The longest output the tool can make: 50 entries (the most accepted), each a CR 0 monster with a 60-character label
        // fought in its lair (three notes apiece), 20 distinct levels among 50 characters, both editions, an offset. Claude
        // Code warns above 10,000 tokens (about 40,000 characters).
        var monsters = string.Join(",", Enumerable.Range(0, 50).Select(i =>
            $$"""{"cr":"0","name":"{{i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}}{{new string('x', 58)}}","count":1000,"lair":true}"""));
        var party = string.Join(",", Enumerable.Range(0, 50).Select(i => (i % 20) + 1));
        var text = await Success($$"""{"party":[{{party}}],"monsters":[{{monsters}}],"edition":"both","effective_level_offset":-3}""");

        Assert.True(text.Length < 40_000, $"The result is {text.Length} characters.");
    }

    [Theory]
    [InlineData("Vampire", "Vampire, Vampire Form", "vampire-vampire", "13", "10,000")]
    [InlineData("Werewolf", "Werewolf, Human Form", "werewolf-human", "3", "700")]
    public async Task CallTool_NameTheDataSplitsIntoForms_UsesTheSharedStatBlock(string name, string form, string slug, string cr, string xp)
    {
        // Both SRDs print one "Vampire" stat block; the data splits it into forms with one CR and XP between them.
        var text = await Success($$"""{"party":[5,5,5,5],"monsters":[{"name":"{{name}}"}],"edition":"both"}""");

        Assert.Contains($"| {form} (`2014/monster/{slug}`, `2024/monster/{slug}`) | 1 | CR {cr}, {xp} XP | CR {cr}, {xp} XP |", text, StringComparison.Ordinal);
        Assert.Contains($"- {name}: the 2024 SRD data splits this stat block into forms (", text, StringComparison.Ordinal);
        Assert.Contains($"which share CR {cr} and {xp} XP, so {form} (`2024/monster/{slug}`) is used.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NameNotInEitherSrd_OffersCloseNamesThenTheCrRoute()
    {
        var text = await Error("""{"party":[5],"monsters":[{"name":"Orge"}]}""");

        Assert.Equal(
            Prefix + "monsters item 1: no monster in the 2014 or 2024 SRD is named \"Orge\". Close SRD names: Ogre " +
            "(`2024/monster/ogre`), Ogre (`2014/monster/ogre`); if one is the monster you mean, pass its ref. For a monster the SRD " +
            "does not have (it has only some of the Monster Manual), give the CR from its stat block, with name as a label: " +
            "{\"name\": \"Orge\", \"cr\": \"<its CR>\"}.",
            text);
    }

    [Fact]
    public async Task CallTool_NameCloseOnlyInTheOtherEdition_SuggestsIt()
    {
        // Resolution falls back to the other edition, so its close names are real answers.
        var text = await Error("""{"party":[5],"monsters":[{"name":"Svirfneblinn"}],"edition":"2024"}""");

        Assert.Contains("Close SRD names: Deep Gnome (Svirfneblin) (`2014/monster/deep-gnome-svirfneblin`)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RefMissingButInTheOtherEdition_OffersThatRef()
    {
        var text = await Error("""{"party":[5],"monsters":[{"ref":"2024/monster/orc"}]}""");

        Assert.Contains(
            "The 2014 SRD has Orc (`2014/monster/orc`): pass that ref, or name \"Orc\", which also finds its 2024 counterpart when " +
            "there is one.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NameNothingIsCloseTo_GoesStraightToTheCrRoute()
    {
        var text = await Error("""{"party":[5],"monsters":[{"name":"Beholder \"Xanathar\""}]}""");

        Assert.Equal(
            Prefix + "monsters item 1: no monster in the 2014 or 2024 SRD is named \"Beholder \"Xanathar\"\". For a monster the SRD " +
            "does not have (it has only some of the Monster Manual), give the CR from its stat block, with name as a label: " +
            "{\"name\": \"Beholder \\\"Xanathar\\\"\", \"cr\": \"<its CR>\"}.",
            text);
    }

    [Theory]
    [InlineData("""{"ref":"2024/monster/orge"}""", "monsters item 1: no 2024 monster has the slug \"orge\" (`2024/monster/orge`). Did you mean Ogre (`2024/monster/ogre`)? For a monster not in the SRD, give its cr instead.")]
    [InlineData("""{"ref":"2024/spell/fireball"}""", "monsters item 1: ref `2024/spell/fireball` is a spell, not a monster. Give a monster's ref (e.g. \"2024/monster/ogre\"), its name, or a cr.")]
    [InlineData("""{"ref":"2024/monster/ogre","name":"Ogre"}""", "monsters item 1: has both ref and name; give one: ref or name for an SRD monster, or cr (with name as its label) for any other.")]
    [InlineData("""{"ref":"2024/monster/ogre","cr":"2"}""", "monsters item 1: has both ref and cr; give one: ref or name for an SRD monster, or cr (with name as its label) for any other.")]
    [InlineData("""{"count":2}""", "monsters item 1: names no monster; give ref or name for an SRD monster, or cr for any other, e.g. {\"name\": \"Ogre\", \"count\": 2} or {\"cr\": \"3\", \"name\": \"Bandit boss\"}.")]
    [InlineData("""{"cr":"1e1"}""", "monsters item 1: \"1e1\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    [InlineData("""{"cr":1e1}""", "monsters item 1: \"1e1\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    [InlineData("""{"cr":1e-400}""", "monsters item 1: \"1e-400\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    [InlineData("""{"cr":"1/3"}""", "monsters item 1: \"1/3\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    [InlineData("""{"cr":0.3}""", "monsters item 1: \"0.3\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a whole number from 1 to 30.")]
    [InlineData("""{"cr":true}""", "monsters item 1: cr must be a string such as \"1/2\" or \"5\", or a number, but was the boolean true.")]
    [InlineData("""{"name":"Ogre","count":0}""", "monsters item 1: count is 0; it is 1 to 1,000.")]
    [InlineData("""{"name":"Ogre","count":1001}""", "monsters item 1: count is 1001; it is 1 to 1,000.")]
    public async Task CallTool_BadMonsterItem_SaysWhatToSend(string item, string message)
    {
        Assert.Equal(Prefix + message, await Error($$"""{"party":[5],"monsters":[{{item}}]}"""));
    }

    [Fact]
    public async Task CallTool_LabelTooLong_IsRefused()
    {
        var text = await Error($$"""{"party":[5],"monsters":[{"cr":"1","name":"{{new string('x', 61)}}"}]}""");

        Assert.StartsWith(Prefix + "monsters item 1: with cr, name is only a label, one line of at most 60 characters", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"party":[],"monsters":[{"name":"Ogre"}]}""", "party needs at least one character's level, e.g. [5, 5, 5, 5] for four level 5 characters.")]
    [InlineData("""{"party":[5,21],"monsters":[{"name":"Ogre"}]}""", "party: character 2's level is 21; character levels are 1 to 20. Give one level per character, e.g. [5, 5, 4].")]
    [InlineData("""{"party":[5],"monsters":[]}""", "monsters needs at least one item. Example: {\"party\": [5, 5, 5, 5], \"monsters\": [{\"name\": \"Ogre\", \"count\": 3}], \"edition\": \"both\"}.")]
    [InlineData("""{"party":[5],"monsters":[{"name":"Ogre"}],"edition":"2025"}""", "edition must be \"2014\", \"2024\" or \"both\" (got \"2025\").")]
    [InlineData("""{"party":[5],"monsters":[{"name":"Ogre"}],"effective_level_offset":11}""", "effective_level_offset is 11; it is a whole number from -10 to +10, e.g. 1 for a party that fights about a level above its own.")]
    public async Task CallTool_BadPartyEditionOrOffset_SaysWhatIsAccepted(string arguments, string message)
    {
        Assert.Equal(Prefix + message, await Error(arguments));
    }

    [Fact]
    public async Task CallTool_TooManyEntries_IsRefused()
    {
        var entries = string.Join(",", Enumerable.Repeat("""{"cr":"1"}""", 51));

        Assert.Equal(
            Prefix + "monsters has 51 items; at most 50 are accepted. Put identical creatures on one item with count.",
            await Error($$"""{"party":[5],"monsters":[{{entries}}]}"""));
    }

    private const string Accepts =
        " encounter_difficulty accepts: party (array of integer, required), monsters (array of object, required), edition (string, " +
        "optional), effective_level_offset (integer, optional).";

    // ToolArgumentGuard inside monsters: every one of these fails in the SDK's binder or binds wrongly without it.
    [Theory]
    [InlineData("""[{"monster":"Ogre","qty":3}]""",
        "argument 'monsters' item 1 has unknown field 'monster' (fields: ref, name, cr, count, exclude, lair); argument 'monsters' item 1 has unknown field 'qty' (fields: ref, name, cr, count, exclude, lair)")]
    [InlineData("""[{"name":"Ogre","count":"three"}]""", "argument 'monsters' item 1 field 'count' should be integer but was the string \"three\"")]
    [InlineData("""[{"name":"Ogre","count":2.5}]""", "argument 'monsters' item 1 field 'count' should be integer but was the number 2.5")]
    [InlineData("""[{"name":"Ogre","count":null}]""", "argument 'monsters' item 1 field 'count' should be integer but was null")]
    [InlineData("""[{"name":"Ogre","count":99999999999}]""", "argument 'monsters' item 1 field 'count' was the number 99999999999, which is too large to be valid")]
    [InlineData("""[{"name":"Ogre","exclude":"yes"}]""", "argument 'monsters' item 1 field 'exclude' should be boolean but was the string \"yes\"")]
    [InlineData("""[{"name":7}]""", "argument 'monsters' item 1 field 'name' should be string but was the number 7")]
    [InlineData("""[{"name":"Ogre"},"Goblin"]""", "argument 'monsters' item 2 should be object but was the string \"Goblin\"")]
    [InlineData("\"3 ogres\"", "argument 'monsters' should be array but was the string \"3 ogres\"")]
    public async Task CallTool_MalformedMonsters_TheGuardNamesTheField(string monsters, string problem)
    {
        var text = await Error($$"""{"party":[5],"monsters":{{monsters}}}""");

        Assert.Equal(Prefix + "Invalid arguments: " + problem + "." + Accepts, text);
    }

    [Theory]
    [InlineData("""[5,"five"]""", "argument 'party' item 2 should be integer but was the string \"five\"")]
    [InlineData("""[5,99999999999]""", "argument 'party' item 2 was the number 99999999999, which is too large to be valid")]
    [InlineData("5", "argument 'party' should be array but was the number 5")]
    public async Task CallTool_MalformedParty_TheGuardNamesTheItem(string party, string problem)
    {
        var text = await Error($$"""{"party":{{party}},"monsters":[{"name":"Ogre"}]}""");

        Assert.Equal(Prefix + "Invalid arguments: " + problem + "." + Accepts, text);
    }

    [Fact]
    public async Task CallTool_ManyBadFields_AreCappedWithACount()
    {
        var monsters = string.Join(",", Enumerable.Repeat("""{"name":"Ogre","count":"x"}""", 7));
        var text = await Error($$"""{"party":[5],"monsters":[{{monsters}}]}""");

        Assert.Contains("argument 'monsters' item 5 field 'count'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("item 6 field", text, StringComparison.Ordinal);
        Assert.Contains("argument 'monsters' has 2 more problem(s)", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"3\"")]
    [InlineData("\"+3\"")]
    public async Task CallTool_NumericStringCount_BindsAsTheBinderReadsIt(string count)
    {
        // The guard must not refuse what binds: the SDK's options read numbers from strings inside objects too.
        var text = await Success($$"""{"party":[5],"monsters":[{"name":"Ogre","count":{{count}}}]}""");

        Assert.Contains("| Ogre (`2024/monster/ogre`) | 2 | 450 | 3 | 1,350 |", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.5", "1/2")]
    [InlineData("\"0.5\"", "1/2")]
    [InlineData("\"½\"", "1/2")]
    [InlineData("13", "13")]
    public async Task CallTool_CrAsNumberOrString_ReadsTheSameRow(string cr, string shown)
    {
        var text = await Success($$"""{"party":[5],"monsters":[{"cr":{{cr}},"name":"X"}]}""");

        Assert.Contains($"| X (by CR) | {shown} |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DeadlyAndBeyondHigh_SayHowFarPastTheLastBand()
    {
        var text = await Success("""{"party":[1],"monsters":[{"cr":"5"}],"edition":"both"}""");

        Assert.Contains("- That reaches Deadly (100), at 27.0× the threshold.", text, StringComparison.Ordinal);
        Assert.Contains("- Monster XP **1,800** is over the High budget (100) by 1,700 (18.0× the budget).", text, StringComparison.Ordinal);
        Assert.Contains("- No multiplier for groups in 2024. The party earns 1,800 XP.\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"party":[3,3,3,3,3],"monsters":[{"name":"Druid","count":2},{"name":"Stirge","count":9}]}""", "- Monster XP **1,125** is over the Low budget (750) and fits Moderate (1,125).")]
    [InlineData("""{"party":[15,15,15,15,15,15],"monsters":[{"name":"Adult Red Dragon","count":2},{"name":"Fire Giant","count":2}]}""", "- Monster XP **46,000** is over the Moderate budget (32,400) and fits High (46,800).")]
    [InlineData("""{"party":[1,1,1,1],"monsters":[{"cr":"1/8","name":"Rat","count":5}]}""", "- Monster XP **125** fits the Low budget (200), 62% of it.")]
    public async Task CallTool_2024Band_NamesTheBudgetsEitherSide(string arguments, string line)
    {
        Assert.Contains(line, await Success(arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BandsPartWaysAtLevel6_ListsEachPairAndTheSameXp()
    {
        var text = await Success("""{"party":[6,6,6,6],"monsters":[{"name":"Ogre"}],"edition":"both"}""");

        Assert.Contains("For this party: Low 2,400 vs Medium 2,400; Moderate 4,000 vs Hard 3,600; High 5,600 vs Deadly 5,600.", text, StringComparison.Ordinal);
        Assert.Contains("- 2014's multiplier is × 1 here, so both editions judge the same XP total.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EditionsXpDiffers_SaysSo()
    {
        var text = await Success("""{"party":[17,17,17,17],"monsters":[{"name":"Adult Red Dragon","lair":true}],"edition":"both"}""");

        Assert.Contains("- The monsters' XP differs between the editions (18,000 in 2014, 20,000 in 2024): Adult Red Dragon is worth 18,000 XP in 2014 and 20,000 (in lair) in 2024.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"party":[1,1,1,1,1,1],"monsters":[{"cr":"1/8","name":"Rat"}],"edition":"2014"}""", "- Monster XP 25 × 0.5 = **12.5 adjusted XP**: × 0.5 for 1 monster (the 1 row's × 1, one step down for a party of 6 or more).")]
    [InlineData("""{"party":[3,3],"monsters":[{"cr":"1","name":"Guard","count":3}],"edition":"2014"}""", "- Monster XP 600 × 2.5 = **1,500 adjusted XP**: × 2.5 for 3 monsters (the 3–6 row's × 2, one step up for a party of fewer than 3).")]
    public async Task CallTool_PartySizeShift_SaysWhichWayFromWhichRow(string arguments, string line)
    {
        Assert.Contains(line, await Success(arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NegativeOffset_UsesAMinusSign()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"cr":"6","name":"Brute"}],"effective_level_offset":-1}""");

        Assert.Contains("**Effective level:** −1 (4 characters at level 4).", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SingleLevel2024_HasNoMixedLevelNoteOrPartyRowAndNamesTheSrd()
    {
        var text = await Success("""{"party":[1,1,1,1],"monsters":[{"cr":"1/8","name":"Rat","count":5}]}""");

        Assert.DoesNotContain("Mixed levels", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**Party (", text, StringComparison.Ordinal);
        Assert.Contains("- 2024 classifies a fight as the lowest difficulty whose budget its XP fits", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "*Sources: 2024 budget and troubleshooting: SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty; CR and XP: the SRD " +
            "stat blocks, or the XP by CR table for a monster given by CR. The tables: rules_get ref \"rules://tables\".*\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ExcludedBothEditionsMixedLevels_MarksTheRowAndOrdersLevelsDown()
    {
        var text = await Success(
            """{"party":[5,4],"monsters":[{"name":"Young Red Dragon"},{"cr":"1/8","name":"Rat","count":10,"exclude":true}],"edition":"both"}""");

        Assert.Contains("| Rat (by CR) · excluded from the 2014 multiplier count | 10 | CR 1/8, 25 XP | CR 1/8, 25 XP |", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("| 1 × level 5 |", StringComparison.Ordinal) < text.IndexOf("| 1 × level 4 |", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallTool_MixedCrsNoExclude_OffersExclude()
    {
        var text = await Success("""{"party":[10,10,10,10],"monsters":[{"name":"Young Red Dragon"},{"name":"Kobold","count":10}],"edition":"2014"}""");

        Assert.Contains("mark such a monster exclude: true to apply it.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_OffsetPastTwenty_SaysHowManyWereHeldAndShowsInLairXp()
    {
        var text = await Success("""{"party":[20,18],"monsters":[{"name":"Adult Red Dragon","lair":true}],"effective_level_offset":2}""");

        Assert.Contains("**Effective level:** +2 (2 characters at level 20); 1 character held to the 1–20 range.", text, StringComparison.Ordinal);
        Assert.Contains("| Adult Red Dragon (`2024/monster/adult-red-dragon`) | 17 | 20,000 (in lair) | 1 | 20,000 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EffectiveLevelOffset_Has2014EffectiveLineAndTroubleshootsAtPrintedLevels()
    {
        var text = await Success(
            """{"party":[5,5,5,5],"monsters":[{"cr":"6","name":"Brute"},{"cr":"1/2","name":"Lackey"}],"edition":"both","effective_level_offset":1}""");

        Assert.Contains("- At effective level +1 it reaches Hard (3,600) but not Deadly (5,600).", text, StringComparison.Ordinal);
        Assert.Contains("- Brute is CR 6, above the party's level (5)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ThreeOgres_AdventuringDayAndJudgedXp()
    {
        var text = await Success("""{"party":[5,5,5,5],"monsters":[{"name":"Ogre","count":3}],"edition":"both"}""");

        Assert.Contains("- Adventuring day: this party can handle about 14,000 adjusted XP before a long rest, so this fight is 19% of a day.", text, StringComparison.Ordinal);
        Assert.Contains("- 2014 judges 2,700 adjusted XP (× 2); 2024 judges the plain 1,350.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"party":[5],"monsters":[{"ref":"ogre"}]}""", "| Ogre (`2024/monster/ogre`) | 2 | 450 | 1 | 450 |")]
    [InlineData("""{"party":[5],"monsters":[{"ref":"monster/ogre"}],"edition":"2014"}""", "| Ogre (`2014/monster/ogre`) | 2 | 450 | 1 | 450 |")]
    public async Task CallTool_RefWithoutEditionOrSlash_UsesTheAskedEdition(string arguments, string row)
    {
        var text = await Success(arguments);

        Assert.Contains(row, text, StringComparison.Ordinal);
        Assert.DoesNotContain("used as given", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LabelOfExactlySixty_IsAccepted()
    {
        var text = await Success($$"""{"party":[5],"monsters":[{"cr":"1","name":"{{new string('x', 60)}}"}]}""");

        Assert.Contains($"| {new string('x', 60)} (by CR) |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LabelWithANewline_IsRefused()
    {
        var text = await Error("""{"party":[5],"monsters":[{"cr":"1","name":"Bandit\nboss"}]}""");

        Assert.StartsWith(Prefix + "monsters item 1: with cr, name is only a label, one line", text, StringComparison.Ordinal);
        Assert.EndsWith("(got \"Bandit\\nboss\").", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_FourCrOnlyMonsters_AreFourStatBlocks()
    {
        var text = await Success("""{"party":[5,5],"monsters":[{"cr":"1","name":"A"},{"cr":"1","name":"B"},{"cr":"1","name":"C"},{"cr":"1","name":"D"}]}""");

        Assert.Contains("- 4 different stat blocks:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ExactlyFiveBadFields_HasNoMoreCount()
    {
        var monsters = string.Join(",", Enumerable.Repeat("""{"name":"Ogre","count":"x"}""", 5));
        var text = await Error($$"""{"party":[5],"monsters":[{{monsters}}]}""");

        Assert.Contains("item 5 field 'count'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("more problem", text, StringComparison.Ordinal);
    }
}
