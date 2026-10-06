using System.Reflection;
using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Features;
using Xunit;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: every closed vocabulary in <see cref="CampaignValues"/> is exactly the CHECK list campaigns.db enforces for
/// that column (read from EVERY migration embedded in the Repository assembly, so the two cannot drift and a later
/// migration's lists are pinned as 0001's are), every CHECK list in the migrations is claimed by some vocabulary here (a
/// new one fails this test until it is), and the per-kind tables and named constants are consistent with the sets they
/// belong to.
/// </summary>
public sealed partial class CampaignValuesTests
{
    private const string MigrationPrefix = "DndMcp.Repository.Campaign.Migrations.";

    /// <summary>(table.column) → the vocabulary the CHECK list must equal.</summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> Expected = new()
    {
        ["campaign.role"] = CV.Roles.Set.Values,
        ["campaign.ruleset"] = CV.Rulesets.Set.Values,
        ["campaign.status"] = CV.CampaignStatuses.Set.Values,
        ["entity.kind"] = CV.Kinds.Set.Values,
        ["entity.visibility"] = CV.Visibilities.Set.Values,
        ["entity.canon_status"] = CV.CanonStatuses.Set.Values,
        ["entity.confidence"] = CV.Confidences.Set.Values,
        ["entity_alias.visibility"] = CV.Visibilities.Set.Values,
        ["relation.visibility"] = CV.Visibilities.RowSet.Values,
        ["relation.status"] = CV.RelationStatuses.Set.Values,
        ["fact.fact_type"] = CV.FactTypes.Set.Values,
        ["fact.truth"] = CV.Truths.Set.Values,
        ["fact.canon_status"] = CV.CanonStatuses.Set.Values,
        ["fact.confidence"] = CV.Confidences.Set.Values,
        ["fact.visibility"] = CV.Visibilities.Set.Values,
        ["fact_link.role"] = CV.FactLinkRoles.Set.Values,
        ["knowledge.knower_kind"] = CV.KnowerKinds.Set.Values,
        ["knowledge.state"] = CV.KnowledgeStates.Set.Values,
        ["session.status"] = CV.SessionStatuses.Set.Values,
        ["session.played_on_precision"] = CV.DatePrecisions.Set.Values,
        ["objective.status"] = CV.ObjectiveStatuses.Set.Values,
        ["objective.visibility"] = CV.Visibilities.RowSet.Values,
        ["clock.unit"] = CV.ClockUnits.Set.Values,
        ["beat_edge.mode"] = CV.BeatEdgeModes.Set.Values,
        ["change_log.op"] = [CV.ChangeOps.Create, CV.ChangeOps.Update, CV.ChangeOps.Delete],
        ["character_sheet.ruleset"] = CV.Rulesets.Editions.Values,
        ["award.kind"] = CV.AwardKinds.Set.Values,
        ["encounter.ruleset"] = CV.Rulesets.Editions.Values,
        ["encounter.status"] = CV.EncounterStatuses.Set.Values,
        ["combatant.side"] = CV.CombatSides.Set.Values,
        ["combat_log.kind"] = CV.CombatLogKinds.Set.Values,
    };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> CheckLists = new(ReadCheckLists);

    public static TheoryData<string> Columns() => new(Expected.Keys);

    [Theory]
    [MemberData(nameof(Columns))]
    public void CheckList_InTheMigration_EqualsTheVocabulary(string column)
    {
        Assert.True(CheckLists.Value.TryGetValue(column, out var values), $"{column} has no CHECK (… IN (…)) list in the migrations");

        Assert.Equal(Expected[column].Order(StringComparer.Ordinal), values.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CheckLists_EveryListInTheMigration_IsClaimedByAVocabulary()
    {
        Assert.Equal(Expected.Keys.Order(StringComparer.Ordinal), CheckLists.Value.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The lists come from every embedded migration, not only 0001: a reader of one file would leave every later CHECK
    /// list unpinned (the vocabulary and the schema could drift with every test green).
    /// </summary>
    [Fact]
    public void CheckLists_AreReadFromEveryEmbeddedMigration()
    {
        var resources = typeof(DndMcp.Repository.Sqlite.Fts5Query).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            DndMcp.Repository.Campaign.CampaignDbMigrator.Embedded.Select(m => MigrationPrefix + m.Name + ".sql"),
            resources);
        Assert.Contains(resources, r => r.EndsWith("0002_characters_combat.sql", StringComparison.Ordinal));
        Assert.Contains("combat_log.kind", CheckLists.Value.Keys);
    }

    /// <summary>
    /// A sheet or a fight runs one edition's rules: the edition set is the ruleset set without <c>mixed</c>, in the same
    /// spelling, so a campaign's ruleset passes straight through when it is not mixed.
    /// </summary>
    [Fact]
    public void Editions_AreTheRulesetsWithoutMixed()
    {
        Assert.Equal(CV.Rulesets.Set.Values.Where(v => v != CV.Rulesets.Mixed), CV.Rulesets.Editions.Values);
        Assert.False(CV.Rulesets.Editions.TryMatch(CV.Rulesets.Mixed, out _));
    }

    public static TheoryData<string, DslValueSet> Phase7SetNames() => new()
    {
        { "edition", CV.Rulesets.Editions },
        { "award kind", CV.AwardKinds.Set },
        { "encounter status", CV.EncounterStatuses.Set },
        { "side", CV.CombatSides.Set },
        { "combat log kind", CV.CombatLogKinds.Set },
    };

    /// <summary>
    /// What a value of each Phase 7 set is called in every refusal that lists the set ("… is not an edition: 2014, 2024"):
    /// contract §8.2 names the edition set "edition", and a sheet's or an encounter's ruleset refused as "not a ruleset"
    /// would point the caller back at the campaign vocabulary, which also allows mixed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Phase7SetNames))]
    public void Phase7Sets_AreNamedForTheirMessages(string what, DslValueSet set) => Assert.Equal(what, set.What);

    /// <summary>The 18 combat log kinds of contract §3, exactly (the tracker, the board and Phase 8's import share them).</summary>
    [Fact]
    public void CombatLogKinds_AreTheEighteenKindsOfTheContract()
    {
        Assert.Equal(
            new[]
            {
                "start", "add", "remove", "initiative", "turn", "damage", "heal", "temp_hp", "condition", "concentration", "save",
                "death_save", "legendary", "resource", "defeat", "note", "end", "import",
            },
            CV.CombatLogKinds.Set.Values);
    }

    public static TheoryData<string, DslValueSet> AllSets()
    {
        var data = new TheoryData<string, DslValueSet>();
        foreach (var type in typeof(CampaignValues).GetNestedTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(DslValueSet)))
            {
                data.Add($"{type.Name}.{field.Name}", (DslValueSet)field.GetValue(null)!);
            }
        }

        foreach (var (kind, set) in CV.Subtypes.ByKind)
        {
            data.Add($"Subtypes.{kind}", set);
        }

        foreach (var (kind, set) in CV.Statuses.ByKind)
        {
            data.Add($"Statuses.{kind}", set);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSets))]
    public void EverySet_ValuesAreCanonicalLowerCaseAndMatchThemselves(string name, DslValueSet set)
    {
        Assert.NotEmpty(set.Values);
        foreach (var value in set.Values)
        {
            Assert.True(set.TryMatch(value, out var canonical), $"{name}: {value}");
            Assert.Equal(value, canonical);
            Assert.Equal(value.Trim().ToLowerInvariant(), value);
        }
    }

    [Fact]
    public void EveryConstant_OfAClassWithASet_IsInThatSet()
    {
        foreach (var type in typeof(CampaignValues).GetNestedTypes())
        {
            var sets = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(DslValueSet))
                .Select(f => (DslValueSet)f.GetValue(null)!)
                .ToList();
            if (sets.Count == 0)
            {
                continue;
            }

            var constants = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name is not ("CharacterPrefix" or "List"))
                .Select(f => (string)f.GetRawConstantValue()!);
            Assert.All(constants, c => Assert.Contains(c, sets.SelectMany(s => s.Values)));
        }
    }

    [Fact]
    public void AwareStates_AreEveryStateButUnawareAndForgot()
    {
        var expected = CV.KnowledgeStates.Set.Values.Except([CV.KnowledgeStates.Unaware, CV.KnowledgeStates.Forgot]).Order();

        Assert.Equal(expected, CV.KnowledgeStates.AwareStates.Order());
    }

    [Fact]
    public void PerKindTables_NameRealKindsAndTheirOwnValues()
    {
        Assert.All(CV.Subtypes.ByKind.Keys.Concat(CV.Statuses.ByKind.Keys).Concat(CV.Statuses.Defaults.Keys), k => Assert.True(CV.Kinds.Set.Contains(k), k));
        Assert.All(CV.Statuses.Defaults, d => Assert.True(CV.Statuses.ByKind[d.Key].Contains(d.Value), $"{d.Key}: {d.Value}"));
        Assert.False(CV.Statuses.ByKind.ContainsKey(CV.Kinds.Session));
        Assert.True(CV.Subtypes.ByKind[CV.Kinds.Character].Contains(CV.Subtypes.Pc));
        Assert.True(CV.Subtypes.ByKind[CV.Kinds.Faction].Contains(CV.Subtypes.PartyFaction));
        Assert.True(CV.Subtypes.ByKind[CV.Kinds.Rule].Contains(CV.Subtypes.RevealRule));
    }

    [Theory]
    [InlineData("beat", CV.Statuses.BeatMet)]
    [InlineData("beat", CV.Statuses.BeatCut)]
    [InlineData("question", CV.Statuses.QuestionWithheld)]
    [InlineData("question", CV.Statuses.QuestionOpen)]
    [InlineData("question", CV.Statuses.QuestionAnswered)]
    [InlineData("secret", CV.Statuses.SecretHidden)]
    [InlineData("secret", CV.Statuses.SecretSeeded)]
    [InlineData("secret", CV.Statuses.SecretPartial)]
    [InlineData("secret", CV.Statuses.SecretRevealed)]
    [InlineData("clock", CV.Statuses.ClockDone)]
    public void NamedStatus_IsAStatusOfItsKind(string kind, string status)
    {
        Assert.True(CV.Statuses.ByKind[kind].Contains(status));
    }

    [Theory]
    [InlineData("All Of", "all_of")]
    [InlineData("anyOf", "any_of")]
    [InlineData("CLUE-FOR", "clue_for")]
    [InlineData("Unrecognized", "unrecognized")]
    [InlineData("Temp HP", "temp_hp")]
    [InlineData("death-save", "death_save")]
    [InlineData("ENEMY", "enemy")]
    [InlineData("Milestone", "milestone")]
    [InlineData(" Paused ", "paused")]
    public void Sets_MatchForgivingly(string text, string canonical)
    {
        var sets = new[]
        {
            CV.BeatEdgeModes.Set, CV.FactLinkRoles.Set, CV.KnowledgeStates.Set, CV.CombatLogKinds.Set, CV.CombatSides.Set, CV.AwardKinds.Set,
            CV.EncounterStatuses.Set,
        };

        Assert.Contains(sets, s => s.TryMatch(text, out var c) && c == canonical);
    }

    // Every embedded migration's text (the resources themselves, not the migrator's parsed copy, so a migration the
    // migrator failed to discover is still read), in file order: a later migration's list for the same column replaces
    // the earlier one, as a later CREATE TABLE in a rebuild would.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadCheckLists()
    {
        var assembly = typeof(DndMcp.Repository.Sqlite.Fts5Query).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (resources.Count == 0)
        {
            throw new InvalidOperationException($"No {MigrationPrefix}*.sql is embedded in {assembly.GetName().Name}.");
        }

        var lists = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            foreach (Match table in TablePattern().Matches(sql))
            {
                foreach (Match check in InListPattern().Matches(table.Groups["body"].Value))
                {
                    var values = QuotedPattern().Matches(check.Groups["list"].Value).Select(m => m.Groups[1].Value).ToList();
                    if (values.Count > 0)
                    {
                        lists[$"{table.Groups["name"].Value}.{check.Groups["column"].Value}"] = values;
                    }
                }
            }
        }

        return lists;
    }

    [GeneratedRegex(@"CREATE TABLE (?<name>\w+) \((?<body>.*?)\) STRICT", RegexOptions.Singleline)]
    private static partial Regex TablePattern();

    [GeneratedRegex(@"(?<column>\w+)\s+IN\s*\((?<list>[^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex InListPattern();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex QuotedPattern();
}
