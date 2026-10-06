using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: when a call leaves the rules edition (or encounter_difficulty's level offset) out, the rules, encounter and
/// balance tools use the active campaign's ruleset (2014 or 2024; a mixed campaign gives none) and its
/// <c>effective_level_offset</c>, say so in one line naming the campaign, and never let the campaign override a value the
/// call gave. With no campaign active, a mixed one, or every value given, each answer is byte-identical to the answer of
/// a server that has never seen a campaign.
///
/// <para>
/// Why it fails silently: the default is invisible in the call. A 2014 campaign whose rules_get quietly answered with the
/// 2024 Fireball, or whose balance_dpr computed a build with 2024 Great Weapon Fighting against a 2014 ogre, reads as a
/// correct answer; so does the reverse, a campaign default leaking into rules_get's ref-conflict check and refusing
/// <c>{"ref": "2014/spell/fireball"}</c> in a 2024 campaign, or a note appearing (and a result changing) for users who never
/// made a campaign. Every test here compares against a campaign-free server or pins the exact note.
/// </para>
/// <para>
/// Each test builds its own servers: the harness gives each its own data directory, so one test's active campaign can
/// never reach another's (or the campaign-free tool tests running in parallel).
/// </para>
/// </summary>
public sealed class CampaignDefaultsToolTests
{
    private const string Note2014 = "2014 rules: the active campaign's (belmakor) ruleset.";

    private const string Note2024 = "2024 rules: the active campaign's (belmakor) ruleset.";

    private const string Fighter =
        """{"name": "Fighter 5", "level": 5, "abilities": {"str": 18}, "fighting_style": "gwf", "attacks": [{"name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"]}]}""";

    private const string Party =
        """[{"archetype": "fighter", "level": 5}, {"archetype": "cleric", "level": 5}]""";

    /// <summary>A server whose active campaign is "belmakor" with <paramref name="ruleset"/> (none when null).</summary>
    private static async Task<McpServerHarness> StartAsync(string? ruleset, int? offset = null)
    {
        var server = new McpServerHarness();
        await server.InitializeAsync();
        if (ruleset is not null)
        {
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            var settings = offset is null
                ? null
                : new Dictionary<string, JsonElement> { ["effective_level_offset"] = JsonSerializer.SerializeToElement(offset.Value) };
            var created = campaigns.Store.Create("Belmakor", "dm", ruleset, settings: settings);
            campaigns.Use(created.Campaign.Slug);
        }

        return server;
    }

    /// <summary>The same call on a campaign-free server and on one whose campaign has <paramref name="ruleset"/>.</summary>
    private static async Task<(string Plain, string WithCampaign)> BothAsync(string tool, string arguments, string ruleset, int? offset = null)
    {
        var plain = await StartAsync(null);
        var campaign = await StartAsync(ruleset, offset);
        try
        {
            return (plain.SuccessText(await plain.CallToolJsonAsync(tool, arguments)),
                    campaign.SuccessText(await campaign.CallToolJsonAsync(tool, arguments)));
        }
        finally
        {
            await plain.DisposeAsync();
            await campaign.DisposeAsync();
        }
    }

    private static async Task<string> CallAsync(string? ruleset, string tool, string arguments, int? offset = null)
    {
        var server = await StartAsync(ruleset, offset);
        try
        {
            return server.SuccessText(await server.CallToolJsonAsync(tool, arguments));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // Every call below either leaves the edition to the campaign or gives it; a seed keeps balance_simulate comparable.
    public static TheoryData<string, string> EditionDefaultingCalls => new()
    {
        { "rules_search", """{"query": "fireball"}""" },
        { "rules_get", """{"name": "Fireball"}""" },
        { "rules_get", """{"ref": "spell/fireball"}""" },
        { "encounter_difficulty", """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}]}""" },
        { "balance_dpr", $$$"""{"build": {{{Fighter}}}, "target": {"monster": "ogre"}}""" },
        { "balance_compare", $$$"""{"baseline": {{{Fighter}}}, "feature": {"name": "Savage Attacker", "modifiers": [{"kind": "reroll_damage_take_best"}]}}""" },
        { "balance_simulate", $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre"}], "iterations": 200, "seed": 7}""" },
    };

    [Theory]
    [MemberData(nameof(EditionDefaultingCalls))]
    public async Task CallTool_MixedCampaign_IsByteIdenticalToNoCampaign(string tool, string arguments)
    {
        // A mixed campaign names no edition: the tools fall back to 2024 exactly as with no campaign, and say nothing.
        var (plain, mixed) = await BothAsync(tool, arguments, "mixed");

        Assert.Equal(plain, mixed);
        Assert.DoesNotContain("active campaign", mixed, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EditionDefaultingCalls))]
    public async Task CallTool_2024Campaign_GivesTheNoCampaignAnswerPlusOneNote(string tool, string arguments)
    {
        // 2024 is also the built-in default: the numbers and text are the campaign-free ones, and the only difference is
        // the line saying the campaign decided (a note is never silently added or dropped).
        var (plain, campaign) = await BothAsync(tool, arguments, "2024");

        Assert.Contains(Note2024, campaign, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(campaign, "active campaign"));
        Assert.Equal(Collapse(plain), Without(campaign, Note2024));
    }

    [Theory]
    [MemberData(nameof(EditionDefaultingCalls))]
    public async Task CallTool_2014Campaign_Uses2014AndSaysSo(string tool, string arguments)
    {
        var (plain, campaign) = await BothAsync(tool, arguments, "2014");

        Assert.Contains(Note2014, campaign, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(campaign, "active campaign"));
        Assert.Contains("2014", campaign.Replace(Note2014, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.NotEqual(plain, campaign);
    }

    // The same calls with the edition given: the campaign (2014) never overrides it, and nothing is noted.
    public static TheoryData<string, string> ExplicitEditionCalls => new()
    {
        { "rules_search", """{"query": "fireball", "edition": "2024"}""" },
        { "rules_search", """{"query": "fireball", "edition": "both"}""" },
        { "rules_get", """{"name": "Fireball", "edition": "2024"}""" },
        { "rules_get", """{"name": "Fireball", "edition": "both"}""" },
        { "rules_get", """{"ref": "2024/spell/fireball"}""" },
        { "encounter_difficulty", """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "edition": "2024", "effective_level_offset": 0}""" },
        { "balance_dpr", $$$"""{"build": {{{Fighter.Replace("\"level\": 5", "\"edition\": \"2024\", \"level\": 5", StringComparison.Ordinal)}}}, "target": {"monster": "ogre"}}""" },
        { "balance_simulate", $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre"}], "edition": "2024", "iterations": 200, "seed": 7}""" },
    };

    [Theory]
    [MemberData(nameof(ExplicitEditionCalls))]
    public async Task CallTool_ExplicitEditionIn2014Campaign_IsByteIdenticalToNoCampaign(string tool, string arguments)
    {
        var (plain, campaign) = await BothAsync(tool, arguments, "2014", offset: 2);

        Assert.Equal(plain, campaign);
    }

    [Fact]
    public async Task RulesGet_RefWithItsOwnEditionIn2024Campaign_UsesTheRefsEditionWithoutAConflict()
    {
        // The ref-conflict check sees only the caller's edition: a campaign default there would refuse this ref.
        var text = await CallAsync("2024", "rules_get", """{"ref": "2014/spell/fireball"}""");

        Assert.StartsWith("# Fireball\n*spell · 2014 · SRD 5.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("active campaign", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_ExplicitEditionAgainstTheRef_StillRefusesInACampaign()
    {
        var server = await StartAsync("2014");
        try
        {
            var text = server.ErrorText(await server.CallToolJsonAsync("rules_get", """{"ref": "2024/spell/fireball", "edition": "2014"}"""));

            Assert.Contains("ref `2024/spell/fireball` is a 2024 entry but edition is \"2014\". Leave edition out to use the ref's own", text, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("""{"name": "Fireball"}""")]
    [InlineData("""{"ref": "spell/fireball"}""")]
    public async Task RulesGet_EditionFromThe2014Campaign_IsThe2014EntryWithTheNoteLast(string arguments)
    {
        var text = await CallAsync("2014", "rules_get", arguments);

        Assert.StartsWith("# Fireball\n*spell · 2014 · SRD 5.1", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_NameWithKindIn2014Campaign_ReadsTheKindInThe2014Srd()
    {
        // "race" is a 2014 kind: read in 2024 (the explicit-or-default edition) it would be refused as species-only.
        var text = await CallAsync("2014", "rules_get", """{"name": "Elf", "kind": "race"}""");

        Assert.StartsWith("# Elf\n*race · 2014", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_NameOtherEntriesShareIn2014Campaign_ListsThemThenTheNoteLast()
    {
        // Trailer order: the entry, "Also named …" (about this lookup), then the campaign's note (about every lookup).
        var plain = await CallAsync(null, "rules_get", """{"name": "Shield", "edition": "2014"}""");
        var text = await CallAsync("2014", "rules_get", """{"name": "Shield"}""");

        Assert.Contains("to get it instead.", plain, StringComparison.Ordinal);
        Assert.Equal(plain + "\n\n" + Note2014, text);
    }

    // Lookups that fail in the edition the campaign chose; the same call with that edition given fails the same way.
    public static TheoryData<string, string> CampaignEditionErrors => new()
    {
        { "rules_get", """{"name": "Weapon Mastery"}""" },
        { "rules_get", """{"name": "Serpent Venom", "kind": "poison"}""" },
        { "rules_get", """{"ref": "spell/no-such-spell"}""" },
        { "rules_search", """{"query": "venom", "kinds": ["poison"]}""" },
    };

    [Theory]
    [MemberData(nameof(CampaignEditionErrors))]
    public async Task RulesError_InTheCampaignsEdition_EndsWithTheNoteNamingTheCampaign(string tool, string arguments)
    {
        // "Nothing in the 2014 SRD …" answers a call that sent no edition: the note says where 2014 came from.
        var explicitEdition = arguments.TrimEnd('}') + """, "edition": "2014"}""";
        var plain = await StartAsync(null);
        var campaign = await StartAsync("2014");
        try
        {
            var given = plain.ErrorText(await plain.CallToolJsonAsync(tool, explicitEdition));
            var defaulted = campaign.ErrorText(await campaign.CallToolJsonAsync(tool, arguments));
            var explicitInCampaign = campaign.ErrorText(await campaign.CallToolJsonAsync(tool, explicitEdition));

            Assert.Contains("2014", given, StringComparison.Ordinal);
            Assert.Equal(given + " " + Note2014, defaulted);
            Assert.Equal(given, explicitInCampaign);
        }
        finally
        {
            await plain.DisposeAsync();
            await campaign.DisposeAsync();
        }
    }

    [Fact]
    public async Task RulesSearch_EditionFromThe2014Campaign_SearchesThe2014SrdAndEndsWithTheNote()
    {
        var text = await CallAsync("2014", "rules_search", """{"query": "fireball"}""");

        Assert.Contains("\" in the 2014 SRD", text.Split('\n')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("· 2024 ·", text, StringComparison.Ordinal);
        Assert.EndsWith("Pass a ref to rules_get for the whole entry.\n\n" + Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesSearch_KindOnlyThe2014SrdHas_IsAcceptedIn2014Campaign()
    {
        // The kinds filter is read in the campaign's edition: "race" is a 2014 kind.
        var text = await CallAsync("2014", "rules_search", """{"query": "elf", "kinds": ["race"]}""");

        Assert.Contains("race · 2014 · `2014/race/elf`", text, StringComparison.Ordinal);
        Assert.EndsWith(Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncounterDifficulty_EditionAndOffsetFromTheCampaign_UseBothAndNoteBothFirst()
    {
        var text = await CallAsync("2014", "encounter_difficulty", """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}]}""", offset: 1);

        Assert.Contains("\n## 2014 rules: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## 2024 rules", text, StringComparison.Ordinal);
        Assert.Contains("**Effective level:** +1 (4 characters at level 6).", text, StringComparison.Ordinal);
        Assert.Contains(
            "**Notes:**\n- " + Note2014 + "\n- Effective level +1: the active campaign's (belmakor) effective_level_offset; pass " +
            "effective_level_offset 0 for the book levels alone.\n",
            text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task EncounterDifficulty_ExplicitOffset_BeatsTheCampaignsAndIsNotNoted(int offset)
    {
        var arguments = $$"""{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "edition": "2024", "effective_level_offset": {{offset}}}""";
        var (plain, campaign) = await BothAsync("encounter_difficulty", arguments, "2014", offset: 3);

        Assert.Equal(plain, campaign);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task EncounterDifficulty_ExplicitOffsetWithTheCampaignsEdition_KeepsTheCallsOffset(int offset)
    {
        // The edition is left to the campaign (so its defaults are read) but the offset is given: 0 must stay "book levels
        // only", never the campaign's +3, and only the edition is noted.
        var party = """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "effective_level_offset": """ + offset;
        var plain = await CallAsync(null, "encounter_difficulty", party + """, "edition": "2014"}""");
        var campaign = await CallAsync("2014", "encounter_difficulty", party + "}", offset: 3);

        Assert.Equal(Collapse(plain), Without(campaign, Note2014));
        Assert.DoesNotContain("effective_level_offset;", campaign, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncounterDifficulty_OffsetOnlyFromTheCampaign_KeepsTheExplicitEditionAndNotesTheOffset()
    {
        var arguments = """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "edition": "2024"}""";
        var (plain, campaign) = await BothAsync("encounter_difficulty", arguments, "2014", offset: -1);

        Assert.DoesNotContain("Effective level", plain, StringComparison.Ordinal);
        Assert.Contains("**Effective level:** −1 (4 characters at level 4).", campaign, StringComparison.Ordinal);
        Assert.Contains("- Effective level −1: the active campaign's (belmakor) effective_level_offset;", campaign, StringComparison.Ordinal);
        Assert.DoesNotContain("ruleset", campaign, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncounterDifficulty_CampaignNotes_ComeBeforeTheMonstersNotes()
    {
        // They say which rules and levels every number above was computed for, so they lead the list.
        var text = await CallAsync("2014", "encounter_difficulty", """{"party": [3], "monsters": [{"cr": "0", "name": "Rat"}]}""", offset: 1);

        Assert.Contains(
            "**Notes:**\n- " + Note2014 + "\n- Effective level +1: the active campaign's (belmakor) effective_level_offset; pass " +
            "effective_level_offset 0 for the book levels alone.\n- Rat: CR 0 is worth 0 or 10 XP by its stat block;",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncounterDifficulty_CampaignOffsetZero_IsNoOffsetAndNoNote()
    {
        var arguments = """{"party": [5], "monsters": [{"name": "Ogre"}], "edition": "2024"}""";
        var (plain, campaign) = await BothAsync("encounter_difficulty", arguments, "2014", offset: 0);

        Assert.Equal(plain, campaign);
    }

    [Fact]
    public async Task EncounterDifficulty_CampaignNamingTheActiveOne_IsByteIdenticalToLeavingItOut()
    {
        // Naming the campaign the defaults already come from changes nothing: the same rules, offset and notes, word for word.
        var server = await StartAsync("2014", offset: 1);
        try
        {
            var arguments = """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}]""";
            var named = server.SuccessText(await server.CallToolJsonAsync("encounter_difficulty", arguments + """, "campaign": "belmakor"}"""));

            Assert.Equal(server.SuccessText(await server.CallToolJsonAsync("encounter_difficulty", arguments + "}")), named);
            Assert.Contains("- " + Note2014 + "\n", named, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task EncounterDifficulty_CampaignNamingAnotherOne_TakesItsRulesetAndOffsetAndNamesIt()
    {
        // belmakor (2014) is active; the call names deep (2024, +2): deep's rules and offset, in notes that name deep and
        // never call it the active campaign.
        var server = await StartAsync("2014");
        try
        {
            var settings = new Dictionary<string, JsonElement> { ["effective_level_offset"] = JsonSerializer.SerializeToElement(2) };
            server.Services.GetRequiredService<CampaignService>().Store.Create("Deep", "dm", "2024", slug: "deep", settings: settings);

            var text = server.SuccessText(await server.CallToolJsonAsync("encounter_difficulty",
                """{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "campaign": "deep"}"""));

            Assert.Contains("\n## 2024 rules: ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("## 2014 rules", text, StringComparison.Ordinal);
            Assert.Contains("**Effective level:** +2 (4 characters at level 7).", text, StringComparison.Ordinal);
            Assert.Contains(
                "**Notes:**\n- 2024 rules: the deep campaign's ruleset.\n- Effective level +2: the deep campaign's effective_level_offset; pass " +
                "effective_level_offset 0 for the book levels alone.\n",
                text, StringComparison.Ordinal);
            Assert.DoesNotContain("active campaign", text, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceSimulate_CampaignNamingTheActiveOne_IsByteIdenticalToLeavingItOut()
    {
        // The chosen campaign is the active one: the same rules and the same note, word for word.
        var server = await StartAsync("2014");
        try
        {
            var arguments = $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre"}], "iterations": 200, "seed": 7""";
            var named = server.SuccessText(await server.CallToolJsonAsync("balance_simulate", arguments + """, "campaign": "belmakor"}"""));

            Assert.Equal(server.SuccessText(await server.CallToolJsonAsync("balance_simulate", arguments + "}")), named);
            Assert.Contains("- " + Note2014 + "\n", named, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceSimulate_CampaignNamingAnotherOne_TakesItsRulesetAndNamesIt()
    {
        // belmakor (2014) is active; the call names deep (2024): deep's rules for the archetypes and the ogre, in a note that
        // names deep and never calls it the active campaign (CampaignEditionFill's chosen-campaign form).
        var server = await StartAsync("2014");
        try
        {
            server.Services.GetRequiredService<CampaignService>().Store.Create("Deep", "dm", "2024", slug: "deep");

            var text = server.SuccessText(await server.CallToolJsonAsync("balance_simulate",
                $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre"}], "iterations": 200, "seed": 7, "campaign": "deep"}"""));

            Assert.Contains("| archetype fighter (level 5, 2024) |", text, StringComparison.Ordinal);
            Assert.Contains("| monster 2024/monster/ogre |", text, StringComparison.Ordinal);
            Assert.Contains("### Notes\n\n- 2024 rules: the deep campaign's ruleset.\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("active campaign", text, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceSimulate_NoCampaignNamedNoEncounterNoCharacter_NeverResolvesOne()
    {
        // With nothing in the call naming a campaign's part, the ambient defaults stand as before: with no campaigns at all
        // the call answers as it always did and creates no campaigns.db.
        var server = await StartAsync(null);
        try
        {
            server.SuccessText(await server.CallToolJsonAsync("balance_simulate", $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre"}], "iterations": 100, "seed": 7}"""));

            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ToolSchema_BalanceSimulateEncounterArguments_PublishDefaultNullAndNothingIsRequired()
    {
        var server = await StartAsync(null);
        try
        {
            var schema = (await server.Client.ListToolsAsync()).Single(t => t.Name == "balance_simulate").JsonSchema;

            foreach (var argument in new[] { "party", "enemies", "encounter", "from_state", "campaign" })
            {
                Assert.Equal(JsonValueKind.Null, schema.GetProperty("properties").GetProperty(argument).GetProperty("default").ValueKind);
            }

            Assert.Contains("Default: the current campaign.", schema.GetProperty("properties").GetProperty("campaign").GetProperty("description").GetString(),
                StringComparison.Ordinal);
            Assert.False(schema.TryGetProperty("required", out _), "balance_simulate requires an argument");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ToolSchema_EncounterCampaign_PublishesDefaultNullAndIsOptional()
    {
        var server = await StartAsync(null);
        try
        {
            var schema = (await server.Client.ListToolsAsync()).Single(t => t.Name == "encounter_difficulty").JsonSchema;
            var property = schema.GetProperty("properties").GetProperty("campaign");

            Assert.Equal(JsonValueKind.Null, property.GetProperty("default").ValueKind);
            Assert.Contains("Default: the current campaign.", property.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Equal(["party", "monsters"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!));
            Assert.False(schema.GetProperty("properties").GetProperty("party").TryGetProperty("type", out _), "party is published typed");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceDpr_BuildWithoutEdition_Follows2014AndFightsThe2014Ogre()
    {
        var text = await CallAsync("2014", "balance_dpr", $$$"""{"build": {{{Fighter}}}, "target": {"monster": "ogre"}}""");

        Assert.Contains("## The build as read: level 5, 2014 rules", text, StringComparison.Ordinal);
        Assert.Contains("2014/monster/ogre", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2024/monster/ogre", text, StringComparison.Ordinal);
        Assert.Contains("**Notes:**\n- " + Note2014 + "\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceCompare_BaselineAndVariantWithoutEdition_BothFollowTheCampaignSoNoEditionChangeIsReported()
    {
        var variant = Fighter.Replace("\"gwf\"", "\"dueling\"", StringComparison.Ordinal);
        var text = await CallAsync("2014", "balance_compare", $$"""{"baseline": {{Fighter}}, "variant": {{variant}}}""");

        Assert.Contains(Note2014, text, StringComparison.Ordinal);
        Assert.DoesNotContain("includes the edition change", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2024 rules", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceCompare_ExplicitVariantEdition_WinsOverTheCampaign()
    {
        // The variant names 2024, the baseline names nothing: the baseline follows the 2014 campaign, the variant keeps its
        // own, and the Domain reports the edition change as it would for any two editions.
        var variant = Fighter.Replace("\"level\": 5", "\"edition\": \"2024\", \"level\": 5", StringComparison.Ordinal);
        var text = await CallAsync("2014", "balance_compare", $$"""{"baseline": {{Fighter}}, "variant": {{variant}}}""");

        Assert.Contains(Note2014, text, StringComparison.Ordinal);
        Assert.Contains("The baseline follows the 2014 rules and the variant the 2024 rules", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014", "2024")]
    [InlineData("2024", "2014")]
    public async Task BalanceCompare_BaselineNamesItsEditionVariantDoesNot_VariantFollowsTheBaseline(string ruleset, string baselineEdition)
    {
        // The campaign's ruleset must not put an edition change into Δ that the call never asked for: the variant reads as
        // the baseline's edition, exactly as if the call had given it, and the campaign decided nothing (no note).
        var baseline = Fighter.Replace("\"level\": 5", $"\"edition\": \"{baselineEdition}\", \"level\": 5", StringComparison.Ordinal);
        var variant = Fighter.Replace("\"gwf\"", "\"dueling\"", StringComparison.Ordinal);
        var variantWithEdition = variant.Replace("\"level\": 5", $"\"edition\": \"{baselineEdition}\", \"level\": 5", StringComparison.Ordinal);

        var plain = await CallAsync(null, "balance_compare", $$"""{"baseline": {{baseline}}, "variant": {{variantWithEdition}}}""");
        var campaign = await CallAsync(ruleset, "balance_compare", $$"""{"baseline": {{baseline}}, "variant": {{variant}}}""");

        Assert.Equal(plain, campaign);
        Assert.DoesNotContain("includes the edition change", campaign, StringComparison.Ordinal);
        Assert.DoesNotContain("active campaign", campaign, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014", "2014")]
    [InlineData("2024", "2014")]
    [InlineData("2014", "2024")]
    public async Task BalanceSimulate_ExplicitFightEditionInACampaign_BuildsNamingNoneFollowIt(string ruleset, string fightEdition)
    {
        // Without a campaign a build naming no edition compiles as 2024 whatever the fight's edition (the Domain's build
        // default). In a campaign it takes the call's fight edition before the campaign's ruleset, so saying "2014" in a
        // 2014 campaign never gives a worse answer than leaving it out. The call decided, so nothing is noted.
        var party = $$"""[{"build": {{Fighter}}, "hp": 44, "ac": 18}]""";
        var partyWithEdition = party.Replace("\"level\": 5", $"\"edition\": \"{fightEdition}\", \"level\": 5", StringComparison.Ordinal);
        const string Rest = "\"enemies\": [{\"monster\": \"ogre\"}], \"iterations\": 100, \"seed\": 3";

        var plain = await CallAsync(null, "balance_simulate", $$"""{"party": {{partyWithEdition}}, "edition": "{{fightEdition}}", {{Rest}}}""");
        var campaign = await CallAsync(ruleset, "balance_simulate", $$"""{"party": {{party}}, "edition": "{{fightEdition}}", {{Rest}}}""");

        Assert.Contains($"| build \"Fighter 5\" (level 5, {fightEdition}) |", campaign, StringComparison.Ordinal);
        Assert.Equal(plain, campaign);
    }

    /// <summary>
    /// Kills G07 (FH10, M02): a build that names its own edition keeps it while a campaign is active, whatever the fight's
    /// and the campaign's editions say; the campaign fills only what an entry leaves out. Overwritten, a 2024 build was
    /// simulated with 2014 rules, and the answer differed from the same call with no campaign.
    /// </summary>
    [Fact]
    public async Task BalanceSimulate_BuildNamingItsOwnEditionInACampaign_KeepsIt()
    {
        var arguments = $$"""{"party": {{SimulateToolTests.FighterParty("2024", 1)}}, "enemies": [{"monster": "ogre"}], "edition": "2014", "seed": 42, "iterations": 200}""";

        var (plain, campaign) = await BothAsync("balance_simulate", arguments, "2014");

        Assert.Contains("build \"Fighter\" (level 5, 2024)", campaign, StringComparison.Ordinal);
        Assert.Equal(plain, campaign);
    }

    /// <summary>
    /// Kills G05 (FH10, M24): a balance result's notes start with what the campaign decided (the rules every number above
    /// was computed under), before the monster lookup's own notes, as encounter_difficulty orders them.
    /// </summary>
    [Fact]
    public async Task BalanceDpr_CampaignEditionAndALookupNote_TheCampaignNoteComesFirst()
    {
        const string Build = """{"name": "Fighter", "level": 5, "abilities": {"str": 16}, "attacks": [{"name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing"}]}""";

        var text = await CallAsync("2014", "balance_dpr", "{\"build\": " + Build + ", \"target\": {\"monster\": \"werewolf\"}}");

        var campaign = text.IndexOf("- " + Note2014, StringComparison.Ordinal);
        var lookup = text.IndexOf("- werewolf: the 2014 SRD data splits this stat block into forms (", StringComparison.Ordinal);
        Assert.True(campaign >= 0 && lookup >= 0, text);
        Assert.True(campaign < lookup, text);
    }

    /// <summary>
    /// Kills E02 (FH10, M01): the defaults come from this process's current campaign before the persisted active one, which
    /// another Claude session may have changed since: two sessions on two campaigns each keep their own rules.
    /// </summary>
    [Fact]
    public async Task RulesGet_ProcessCurrentCampaignIsNotThePersistedActiveOne_UsesTheCurrentOnesRuleset()
    {
        var server = await StartAsync("2014");
        try
        {
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            var other = campaigns.Store.Create("New Rules", "dm", "2024", slug: "new");
            campaigns.Store.SetActive(other.Campaign.Id); // what another process's campaign use does

            var text = server.SuccessText(await server.CallToolJsonAsync("rules_get", """{"name": "Fireball"}"""));

            Assert.EndsWith(Note2014, text.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Kills K10 (FH10, M01): when this process's current campaign no longer exists (its create undone), the defaults come
    /// from the persisted active campaign, as every call without campaign then resolves (§3.7: an id naming a removed
    /// campaign is ignored), not from the built-in 2024.
    /// </summary>
    [Fact]
    public async Task RulesGet_CurrentCampaignRemoved_TakesTheActiveCampaignsRuleset()
    {
        var server = await StartAsync(null);
        try
        {
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            server.SuccessText(await server.CallToolJsonAsync("campaign", """{"action": "create", "name": "Old Table", "role": "dm", "ruleset": "2014", "slug": "oldt"}"""));
            var second = server.SuccessText(await server.CallToolJsonAsync("campaign",
                """{"action": "create", "name": "New Table", "role": "dm", "ruleset": "2024", "slug": "newt"}"""));
            campaigns.Store.SetActive(campaigns.Store.TryGet("oldt")!.Id); // another process's campaign use
            var batch = Regex.Match(second, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
            server.SuccessText(await server.CallToolJsonAsync("campaign_history", $$"""{"action": "undo", "batch_id": "{{batch}}", "campaign": "newt"}"""));

            var text = server.SuccessText(await server.CallToolJsonAsync("rules_get", """{"name": "Fireball"}"""));

            Assert.EndsWith("2014 rules: the active campaign's (oldt) ruleset.", text.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceSimulate_FirstPartyEntryNamesItsEdition_OthersFollowItNotTheCampaign()
    {
        // The fight follows the first party entry's rules; an entry naming none follows them too, as it does with no
        // campaign, rather than mixing in the campaign's ruleset.
        var party = """[{"archetype": "fighter", "level": 5, "edition": "2024"}, {"archetype": "cleric", "level": 5}]""";
        var (plain, campaign) = await BothAsync("balance_simulate", $$"""{"party": {{party}}, "enemies": [{"archetype": "rogue", "level": 3}], "iterations": 100, "seed": 3}""", "2014");

        Assert.Contains("| archetype cleric (level 5, 2024) |", campaign, StringComparison.Ordinal);
        Assert.Contains("| archetype rogue (level 3, 2024) |", campaign, StringComparison.Ordinal);
        Assert.Equal(plain, campaign);
    }

    public static TheoryData<string, string, string> NoCampaignEditionCalls => new()
    {
        {
            "balance_compare",
            $$$"""{"baseline": {{{Fighter.Replace("\"level\": 5", "\"edition\": \"2014\", \"level\": 5", StringComparison.Ordinal)}}}, "variant": {{{Fighter}}}}""",
            "The baseline follows the 2014 rules and the variant the 2024 rules"
        },
        {
            "balance_simulate",
            $$$"""{"party": [{"build": {{{Fighter}}}, "hp": 44, "ac": 18}], "edition": "2014", "enemies": [{"monster": "ogre"}], "iterations": 100, "seed": 3}""",
            "| build \"Fighter 5\" (level 5, 2024) |"
        },
        {
            "balance_simulate",
            """{"party": [{"archetype": "fighter", "level": 5, "edition": "2014"}, {"archetype": "cleric", "level": 5}], "enemies": [{"monster": "ogre"}], "iterations": 100, "seed": 3}""",
            "| archetype cleric (level 5, 2024) |"
        },
    };

    [Theory]
    [MemberData(nameof(NoCampaignEditionCalls))]
    public async Task CallTool_NoCampaign_EditionsTheCallNamedAreNotSpreadToOtherSpecs(string tool, string arguments, string expected)
    {
        // Following the call's own editions is a campaign-only rule: with no campaign every answer stays what it was before
        // campaigns existed, quirks included (a variant or build naming no edition is 2024 whatever the call said elsewhere).
        var server = await StartAsync(null);
        try
        {
            var text = server.SuccessText(await server.CallToolJsonAsync(tool, arguments));

            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")), "A balance call created campaigns.db.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task BalanceSimulate_ArchetypesAndMonstersWithoutEdition_Follow2014AndSaySo()
    {
        var text = await CallAsync("2014", "balance_simulate", $$"""{"party": {{Party}}, "enemies": [{"monster": "ogre", "count": 2}], "iterations": 200, "seed": 7}""");

        Assert.Contains("| archetype fighter (level 5, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("| monster 2014/monster/ogre |", text, StringComparison.Ordinal);
        Assert.Contains("*2014 rules · ", text, StringComparison.Ordinal);
        Assert.Contains("### Notes\n\n- " + Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceSimulate_EnemyArchetypesAndBuildsWithoutEdition_FollowTheCampaignToo()
    {
        // enemies is published untyped and parsed by the host: its entries are filled like the party's.
        var enemies = """[{"archetype": "rogue", "level": 3}, {"build": {"name": "Bandit captain", "level": 4, "attacks": [{"name": "Scimitar", "damage": "1d6", "damage_type": "slashing", "properties": ["melee"]}]}, "hp": 40, "ac": 15}]""";
        var text = await CallAsync("2014", "balance_simulate", $$"""{"party": {{Party}}, "enemies": {{enemies}}, "iterations": 100, "seed": 3}""");

        Assert.Contains("| archetype rogue (level 3, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("| build \"Bandit captain\" (level 4, 2014) |", text, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(text, Note2014));
    }

    [Fact]
    public async Task BalanceSimulate_MonsterFirstInTheParty_IsLookedUpInTheCampaignsEdition()
    {
        // No party build or archetype names an edition: the monster lookup's last fallback is the campaign's ruleset.
        var text = await CallAsync("2014", "balance_simulate", """{"party": [{"monster": "Knight"}], "enemies": [{"monster": "Ogre"}], "iterations": 100, "seed": 3}""");

        Assert.Contains("2014/monster/knight", text, StringComparison.Ordinal);
        Assert.Contains("2014/monster/ogre", text, StringComparison.Ordinal);
        Assert.Contains(Note2014, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BalanceSimulate_EntriesThatNameTheirEdition_KeepItAndNothingIsNoted()
    {
        var party = """[{"archetype": "fighter", "level": 5, "edition": "2024"}, {"build": {"name": "Brute", "edition": "2024", "level": 5, "attacks": [{"name": "Club", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"]}]}, "hp": 40, "ac": 14}]""";
        var arguments = $$"""{"party": {{party}}, "enemies": [{"monster": "ogre", "edition": "2024"}], "iterations": 100, "seed": 3}""";
        var (plain, campaign) = await BothAsync("balance_simulate", arguments, "2014");

        Assert.Equal(plain, campaign);
    }

    [Fact]
    public async Task Defaults_NoCampaignsDatabase_AreNeverCreatedByARulesCall()
    {
        var server = await StartAsync(null);
        try
        {
            server.SuccessText(await server.CallToolJsonAsync("rules_search", """{"query": "fireball"}"""));
            server.SuccessText(await server.CallToolJsonAsync("encounter_difficulty", """{"party": [5], "monsters": [{"cr": "1"}]}"""));

            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")), "A rules call created campaigns.db.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// FH2 (R09): campaigns.db exists but cannot be read (here it is not a database at all). Every call that left its
    /// edition (and offset) to the campaign answers as with no campaign, plus one line saying the campaign's settings could
    /// not be read, naming the file and what was used instead; the log says why. Before, the answer was byte-identical to
    /// no campaign: a 2014 campaign's rules lookup quietly answered with 2024 rules, and nothing said so.
    /// </summary>
    [Theory]
    [MemberData(nameof(EditionDefaultingCalls))]
    public async Task CallTool_UnreadableCampaignsDatabase_AnswersAsWithNoCampaignAndSaysTheSettingsCouldNotBeRead(string tool, string arguments)
    {
        var plain = await CallAsync(null, tool, arguments);
        var server = await StartAsync(null);
        try
        {
            Directory.CreateDirectory(server.DataDirectory);
            var path = Path.Combine(server.DataDirectory, "campaigns.db");
            await File.WriteAllTextAsync(path, "not a database at all, just some text");

            var text = server.SuccessText(await server.CallToolJsonAsync(tool, arguments));

            var note = $"Could not read the active campaign's settings (campaigns.db at {path}); using " +
                       (tool == "encounter_difficulty" ? "2024 and no effective_level_offset." : "2024.");
            Assert.Equal(1, Occurrences(text, note));
            Assert.Equal(Collapse(plain), Without(text, note));
            Assert.Contains(server.ServerLog.Entries, e => e.Category == typeof(CampaignService).FullName && e.Message.Contains("defaults", StringComparison.Ordinal));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// The note says only what the campaign would have decided: with the edition given, an unreadable campaigns.db leaves
    /// encounter_difficulty without the campaign's level offset alone; with every value given nothing reads campaigns.db,
    /// and the answer is byte-identical to no campaign.
    /// </summary>
    [Theory]
    [InlineData("""{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "edition": "2024"}""", "no effective_level_offset")]
    [InlineData("""{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "edition": "2024", "effective_level_offset": 0}""", null)]
    [InlineData("""{"party": [5, 5, 5, 5], "monsters": [{"name": "Ogre", "count": 3}], "effective_level_offset": 1}""", "2024")]
    public async Task EncounterDifficulty_UnreadableCampaignsDatabase_NotesOnlyWhatTheCampaignWouldHaveDecided(string arguments, string? used)
    {
        var plain = await CallAsync(null, "encounter_difficulty", arguments);
        var server = await StartAsync(null);
        try
        {
            Directory.CreateDirectory(server.DataDirectory);
            var path = Path.Combine(server.DataDirectory, "campaigns.db");
            await File.WriteAllTextAsync(path, "not a database at all, just some text");

            var text = server.SuccessText(await server.CallToolJsonAsync("encounter_difficulty", arguments));

            if (used is null)
            {
                Assert.Equal(plain, text);
                return;
            }

            var note = $"Could not read the active campaign's settings (campaigns.db at {path}); using {used}.";
            Assert.Equal(1, Occurrences(text, note));
            Assert.Equal(Collapse(plain), Without(text, note));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("rules_search", "edition")]
    [InlineData("rules_get", "edition")]
    [InlineData("encounter_difficulty", "edition")]
    [InlineData("encounter_difficulty", "effective_level_offset")]
    [InlineData("balance_simulate", "edition")]
    public async Task ToolSchema_CampaignDefaultedArgument_PublishesDefaultNullAndIsOptional(string tool, string argument)
    {
        // A published "default": "2024" would contradict the campaign's ruleset and go stale when the campaign changes.
        var server = await StartAsync(null);
        try
        {
            var schema = (await server.Client.ListToolsAsync()).Single(t => t.Name == tool).JsonSchema;
            var property = schema.GetProperty("properties").GetProperty(argument);

            Assert.Equal(JsonValueKind.Null, property.GetProperty("default").ValueKind);
            Assert.Contains("the active campaign's", property.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(argument, schema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(r => r.GetString()) : [], StringComparer.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("rules_search")]
    [InlineData("rules_get")]
    [InlineData("encounter_difficulty")]
    [InlineData("balance_dpr")]
    [InlineData("balance_simulate")]
    public async Task ToolDescription_EditionDefault_NamesTheCampaignAndStaysWithinTheLimit(string tool)
    {
        // balance_dpr and balance_simulate sat within 31 characters of the 2,048 limit before the campaign wording.
        var server = await StartAsync(null);
        try
        {
            var description = (await server.Client.ListToolsAsync()).Single(t => t.Name == tool).Description!;

            Assert.Contains("campaign's", description, StringComparison.Ordinal);
            Assert.DoesNotContain("\"2024\" (default)", description, StringComparison.Ordinal);
            Assert.DoesNotContain("\"2024\" default", description, StringComparison.Ordinal);
            Assert.InRange(description.Length, 1, 2_048);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("balance_dpr", "build")]
    [InlineData("balance_compare", "baseline")]
    public async Task BuildSchema_SpecIsARecord_PublishesExactlyTheBuildFields(string tool, string argument)
    {
        // BuildSpec became a record so the host can copy it with the campaign's edition; a record must add nothing to the
        // schema the model reads (its EqualityContract is private).
        var server = await StartAsync(null);
        try
        {
            var schema = (await server.Client.ListToolsAsync()).Single(t => t.Name == tool).JsonSchema;
            var build = schema.GetProperty("properties").GetProperty(argument);

            Assert.Equal(
                ["abilities", "attacks", "edition", "fighting_style", "level", "modifiers", "name", "preset", "proficiency_bonus"],
                build.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(build.TryGetProperty("required", out _), "A build field became required.");

            // The property the model reads beside the tool description must not name a different default ("2024").
            var edition = build.GetProperty("properties").GetProperty("edition").GetProperty("description").GetString();
            Assert.Contains("Default: the active campaign's ruleset, else 2024.", edition, StringComparison.Ordinal);
            Assert.DoesNotContain("(default)", edition, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task PartySchema_CombatantSpecIsARecord_PublishesExactlyTheEntryFields()
    {
        var server = await StartAsync(null);
        try
        {
            var schema = (await server.Client.ListToolsAsync()).Single(t => t.Name == "balance_simulate").JsonSchema;
            var entry = schema.GetProperty("properties").GetProperty("party").GetProperty("items");

            Assert.Equal(
                ["ac", "archetype", "build", "character", "count", "death_saves", "edition", "hp", "initiative_bonus", "level", "monster",
                 "name", "position", "save_proficiencies", "saves"],
                entry.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(entry.TryGetProperty("required", out _), "An entry field became required.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // The text with the note's own line (a paragraph or a bullet) removed, and a notes heading it leaves empty with it.
    private static string Without(string text, string note)
    {
        var lines = text.Split('\n').Where(l => l != note && l != "- " + note).ToList();
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var next = lines.Skip(i + 1).FirstOrDefault(l => l.Length > 0);
            if (lines[i] is "**Notes:**" or "### Notes" && next?.StartsWith("- ", StringComparison.Ordinal) != true)
            {
                continue;
            }

            kept.Add(lines[i]);
        }

        return Collapse(string.Join('\n', kept));
    }

    private static string Collapse(string text) => Regex.Replace(text, "\n{3,}", "\n\n").TrimEnd();
}
