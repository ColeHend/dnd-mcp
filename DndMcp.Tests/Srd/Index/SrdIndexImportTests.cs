using System.Globalization;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// What the builder imports: every vendored record of every kind in both editions plus the 174 glossary entries,
/// once each, with the JSON unchanged, and the columns the queries depend on filled the way they assume.
///
/// <para>
/// The per-(edition, kind) counts are PLAN.md's "import counts match" exit check, written as literal numbers equal to
/// the vendored array lengths (<see cref="VendoredFileTests"/> pins the same numbers per file; the glossary's 174 is
/// pinned by <see cref="RulesGlossaryTests"/>). A builder that skipped records, double-imported a file or dropped a kind
/// changes a number here. A re-vendor changes them too, deliberately.
/// </para>
/// </summary>
public sealed class SrdIndexImportTests : IClassFixture<SrdIndexFixture>
{
    private const int Total2014 = 2415;
    private const int Total2024 = 2187;

    private readonly SrdIndexFixture _fixture;

    public SrdIndexImportTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string, int> PinnedCounts => new()
    {
        { "2014", "ability-score", 6 }, { "2014", "alignment", 9 }, { "2014", "background", 1 }, { "2014", "class", 12 },
        { "2014", "condition", 15 }, { "2014", "damage-type", 13 }, { "2014", "equipment", 237 },
        { "2014", "equipment-category", 39 }, { "2014", "feat", 1 }, { "2014", "feature", 407 }, { "2014", "language", 16 },
        { "2014", "level", 290 }, { "2014", "magic-item", 362 }, { "2014", "magic-school", 8 }, { "2014", "monster", 334 },
        { "2014", "proficiency", 117 }, { "2014", "race", 9 }, { "2014", "rule", 137 }, { "2014", "skill", 18 },
        { "2014", "spell", 319 }, { "2014", "subclass", 12 }, { "2014", "subrace", 4 }, { "2014", "trait", 38 },
        { "2014", "weapon-property", 11 },

        { "2024", "ability-score", 6 }, { "2024", "alignment", 10 }, { "2024", "background", 4 }, { "2024", "class", 12 },
        { "2024", "condition", 15 }, { "2024", "damage-type", 13 }, { "2024", "equipment", 182 },
        { "2024", "equipment-category", 30 }, { "2024", "feat", 17 }, { "2024", "feature", 232 }, { "2024", "language", 19 },
        { "2024", "level", 287 }, { "2024", "magic-item", 262 }, { "2024", "magic-school", 8 }, { "2024", "monster", 341 },
        { "2024", "poison", 14 }, { "2024", "proficiency", 74 }, { "2024", "rule", 174 }, { "2024", "skill", 18 },
        { "2024", "species", 9 }, { "2024", "spell", 339 }, { "2024", "subclass", 12 }, { "2024", "subspecies", 24 },
        { "2024", "trait", 67 }, { "2024", "weapon-mastery", 8 }, { "2024", "weapon-property", 10 },
    };

    [Theory]
    [MemberData(nameof(PinnedCounts))]
    public void Counts_EachEditionAndKind_MatchesTheVendoredArrayLength(string edition, string kind, int expected)
    {
        var count = Assert.Single(_fixture.Index.Counts(), c => c.Edition == edition && c.Kind == kind);

        Assert.Equal(expected, count.Count);
    }

    // Without this a kind the builder invents, or one it drops entirely, would never meet a pinned row.
    [Fact]
    public void Counts_AllEditionsAndKinds_AreExactlyThePinnedOnes()
    {
        var pinned = PinnedCounts.Select(row => $"{row[0]}/{row[1]}").Order(StringComparer.Ordinal);
        var actual = _fixture.Index.Counts().Select(c => $"{c.Edition}/{c.Kind}").Order(StringComparer.Ordinal);

        Assert.Equal(pinned, actual);
    }

    [Fact]
    public void Counts_Totals_AreTheSumOfBothEditions()
    {
        var counts = _fixture.Index.Counts();

        Assert.Equal(Total2014, counts.Where(c => c.Edition == "2014").Sum(c => c.Count));
        Assert.Equal(Total2024, counts.Where(c => c.Edition == "2024").Sum(c => c.Count));
        Assert.Equal(Total2014 + Total2024, _fixture.Index.Info.DocumentCount);
        Assert.Equal(Total2014 + Total2024, _fixture.Summary.DocumentCount);
    }

    // The meta counts are what the build reports (and what srd-build prints); they must agree with the rows.
    [Fact]
    public void Meta_PerKindCounts_AgreeWithTheRows()
    {
        var meta = _fixture.Column("SELECT key || '=' || value FROM meta WHERE key LIKE 'count:%';").Order(StringComparer.Ordinal);
        var rows = _fixture.Index.Counts()
            .Select(c => $"count:{c.Edition}/{c.Kind}={c.Count.ToString(CultureInfo.InvariantCulture)}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(rows, meta);
        Assert.Equal(rows, _fixture.Summary.Counts.Select(c => $"count:{c.Edition}/{c.Kind}={c.Count}").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every document's JSON is the vendored record, value for value, or the record with its curated correction applied
    /// (content/srd-corrections.json) and nothing else: formatters read <see cref="SrdDocument.Root"/>, so anything the
    /// importer reshaped would reach the model as if it were SRD text.
    /// </summary>
    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Json_EveryVendoredRecord_IsStoredUnchangedOrAsCorrected(string edition)
    {
        var corrections = SrdCorrections.Load(SrdIndexFixture.ContentRoot);
        var compared = 0;
        foreach (var kind in SrdKinds.All.Where(k => k.FileFor(edition) is not null))
        {
            foreach (var record in SrdTestContent.Raw(edition, kind.FileFor(edition)!).EnumerateArray())
            {
                var doc = _fixture.Index.Get(edition, kind.Name, record.GetProperty("index").GetString()!);
                Assert.NotNull(doc);
                Assert.True(JsonElement.DeepEquals(Expected(corrections, doc.Ref, record), doc.Root), $"{doc.Ref} differs from the vendored record.");
                compared++;
            }
        }

        Assert.Equal(edition == "2014" ? Total2014 : Total2024 - 174, compared);
    }

    [Fact]
    public void Json_EveryGlossaryRecord_IsStoredUnchangedOrAsCorrectedUnderItsNameSlug()
    {
        var corrections = SrdCorrections.Load(SrdIndexFixture.ContentRoot);
        using var glossary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SrdIndexFixture.ContentRoot, SrdKinds.RulesGlossary2024FileName)));
        foreach (var record in glossary.RootElement.EnumerateArray())
        {
            var name = record.GetProperty("name").GetString()!;
            var doc = _fixture.Index.Get("2024", "rule", SrdSlug.FromName(name));

            Assert.NotNull(doc);
            Assert.True(JsonElement.DeepEquals(Expected(corrections, doc.Ref, record), doc.Root), $"{doc.Ref} differs from the glossary record.");
        }
    }

    // One reason per applied correction, and none on any other document: the column is what labels corrected text.
    [Fact]
    public void Corrections_Column_HoldsTheReasonOfExactlyTheCorrectedDocuments()
    {
        var corrections = SrdCorrections.Load(SrdIndexFixture.ContentRoot);
        var expected = corrections.Entries
            .Where(c => _fixture.Index.Get(c.Target) is not null)
            .Select(c => $"{c.Ref}={c.Reason}")
            .Order(StringComparer.Ordinal);

        var actual = _fixture.Column("SELECT edition || '/' || kind || '/' || slug || '=' || corrections FROM doc WHERE corrections <> '';")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    private static JsonElement Expected(SrdCorrections corrections, SrdRef reference, JsonElement record) =>
        corrections.Apply(reference, record) is { } corrected ? JsonDocument.Parse(corrected).RootElement.Clone() : record;

    [Theory]
    [InlineData("2014/level/fighter-5", "Fighter 5")]       // 2014 levels have no name: class name + level
    [InlineData("2014/level/champion-3", "Champion 3")]     // 2014 subclass level: the SUBCLASS name
    [InlineData("2014/level/wizard-20", "Wizard 20")]
    [InlineData("2024/level/fighter-5", "Fighter 5")]       // 2024 levels carry their own name
    [InlineData("2024/level/berserker-3", "Berserker 3")]
    [InlineData("2024/rule/finesse-weapon-property", "Finesse (Weapon Property)")]
    [InlineData("2014/monster/adult-red-dragon", "Adult Red Dragon")]
    public void Name_ForRecord_IsTheDisplayName(string reference, string expected)
    {
        var parts = reference.Split('/');

        Assert.Equal(expected, _fixture.Index.Get(parts[0], parts[1], parts[2])!.Name);
    }

    [Fact]
    public void Rows_EveryDocument_HasANameValidJsonAndAUniqueSlug()
    {
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE trim(name) = '' OR trim(slug) = '' OR json_valid(json) = 0;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM (SELECT 1 FROM doc GROUP BY edition, kind, slug HAVING COUNT(*) > 1);"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE name_key = '';"));
    }

    // ClassLevels / SubclassLevels read the class and subclass columns; a level without them would vanish from a class
    // table. A feature's level orders graded features (the d6 Bardic Inspiration at level 1 before the d10 at level 10).
    [Fact]
    public void Rows_LevelColumns_AreFilledForLevelsAndFeatureLevelsOnly()
    {
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE kind = 'level' AND (class_slug IS NULL OR level IS NULL);"));
        Assert.Equal(0, _fixture.Scalar(
            "SELECT COUNT(*) FROM doc WHERE kind <> 'level' AND (class_slug IS NOT NULL OR subclass_slug IS NOT NULL);"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE kind NOT IN ('level', 'feature') AND level IS NOT NULL;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE kind = 'feature' AND level IS NULL;"));
        Assert.Equal(10, _fixture.Scalar("SELECT level FROM doc WHERE edition = '2014' AND kind = 'feature' AND slug = 'bardic-inspiration-d10';"));
        Assert.Equal(9, _fixture.Scalar("SELECT level FROM doc WHERE edition = '2024' AND kind = 'feature' AND slug = 'barbarian-brutal-strike';"));
        Assert.Equal(50, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE kind = 'level' AND edition = '2014' AND subclass_slug IS NOT NULL;"));
        Assert.Equal(47, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE kind = 'level' AND edition = '2024' AND subclass_slug IS NOT NULL;"));
    }

    [Theory]
    [InlineData("2014", "bardic-inspiration-d10", 10)]
    [InlineData("2014", "wild-shape-cr-1-4-or-below-no-flying-or-swim-speed", 2)]
    [InlineData("2024", "barbarian-brutal-strike", 9)]
    [InlineData("2024", "berserker-frenzy", 3)]
    public void Get_Feature_CarriesItsClassLevel(string edition, string slug, int level)
    {
        Assert.Equal(level, _fixture.Index.Get(edition, "feature", slug)!.Level);
    }

    [Fact]
    public void Rows_Searchable_IsZeroExactlyForLevels()
    {
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE (kind = 'level') <> (searchable = 0);"));
        Assert.Equal(577, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE searchable = 0;"));
    }

    // The FTS index itself (MATCH reads the index, not doc) holds no level rows, while it does hold classes.
    [Fact]
    public void Fts_LevelRows_AreNotIndexed()
    {
        Assert.Equal(0, _fixture.Scalar(
            "SELECT COUNT(*) FROM doc_fts JOIN doc ON doc.id = doc_fts.rowid WHERE doc_fts MATCH 'fighter' AND doc.kind = 'level';"));
        Assert.True(_fixture.Scalar(
            "SELECT COUNT(*) FROM doc_fts JOIN doc ON doc.id = doc_fts.rowid WHERE doc_fts MATCH 'fighter' AND doc.kind = 'class';") > 0);
    }

    [Theory]
    [InlineData("2014", "fighter", 20)]
    [InlineData("2024", "fighter", 20)]
    [InlineData("2014", "wizard", 20)]
    public void ClassLevels_ForClass_AreItsOwnLevelsInOrder(string edition, string classSlug, int expected)
    {
        var levels = _fixture.Index.ClassLevels(edition, classSlug);

        Assert.Equal(expected, levels.Count);
        Assert.Equal(Enumerable.Range(1, expected), levels.Select(l => l.Root.GetProperty("level").GetInt32()));
        Assert.All(levels, l => Assert.False(l.Root.TryGetProperty("subclass", out _), $"{l.Ref} is a subclass level."));
    }

    // 2024 subclass levels keep the short name in their index (berserker-3) but point at path-of-the-berserker.
    [Theory]
    [InlineData("2014", "berserker", "2014/level/berserker-3,2014/level/berserker-6,2014/level/berserker-10,2014/level/berserker-14")]
    [InlineData("2024", "path-of-the-berserker", "2024/level/berserker-3,2024/level/berserker-6,2024/level/berserker-10,2024/level/berserker-14")]
    [InlineData("2024", "berserker", "")]
    public void SubclassLevels_ForSubclass_AreOrderedByLevel(string edition, string subclassSlug, string expected)
    {
        var levels = _fixture.Index.SubclassLevels(edition, subclassSlug);

        Assert.Equal(expected, string.Join(',', levels.Select(l => l.Ref.ToString())));
    }

    [Fact]
    public void Info_FreshBuild_DescribesTheContentAndSchema()
    {
        var info = _fixture.Index.Info;
        var content = SrdIndexContent.Load(SrdIndexFixture.ContentRoot);

        Assert.Equal(content.StalenessKey, info.StalenessKey);
        Assert.Equal(SrdIndexSchema.Version, info.SchemaVersion);
        Assert.Equal("5e-database-v7.0.0", info.ContentTag);
        Assert.Equal(content.ContentFingerprint, info.ContentFingerprint);
        Assert.Equal(RulesGlossaryTests.PinnedSha256, info.GlossarySha256);
        Assert.InRange(info.BuiltAtUtc, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    // Readers open srd.db read-only; WAL would need -wal/-shm files beside it that a rename does not move.
    [Fact]
    public void File_FreshBuild_IsDeleteJournalStrictAndVersioned()
    {
        Assert.Equal(["delete"], _fixture.Column("PRAGMA journal_mode;"));
        Assert.Equal(SrdIndexSchema.Version, _fixture.Scalar("PRAGMA user_version;"));
        Assert.All(
            _fixture.Column("SELECT sql FROM sqlite_schema WHERE type = 'table' AND name IN ('meta', 'doc', 'alias', 'counterpart');"),
            sql => Assert.Contains("STRICT", sql, StringComparison.Ordinal));
        Assert.Equal(4, _fixture.Column("SELECT name FROM sqlite_schema WHERE type = 'table' AND name IN ('meta', 'doc', 'alias', 'counterpart');").Count);
    }

    /// <summary>
    /// The search text puts the description first (snippets read as prose), keeps nested values (class names) and
    /// leaves out links, identifiers, Choice machinery and the glossary's source, and holds no markdown markers.
    /// </summary>
    [Fact]
    public void Text_Fireball2014_StartsWithTheDescriptionAndKeepsNestedNamesButNoLinks()
    {
        var text = _fixture.Column("SELECT text FROM doc WHERE edition = '2014' AND kind = 'spell' AND slug = 'fireball';").Single();

        Assert.StartsWith("A bright streak flashes from your pointing finger", text, StringComparison.Ordinal);
        Assert.Contains("Wizard", text, StringComparison.Ordinal);       // classes[].name
        Assert.Contains("Evocation", text, StringComparison.Ordinal);    // school.name
        Assert.DoesNotContain("/api/", text, StringComparison.Ordinal);

        // The record's own name has its own, heavier FTS column; repeating it here would double-count it in bm25.
        Assert.DoesNotContain("fireball", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_AllDocuments_HaveNoMarkdownMarkersLinksOrChoiceMachinery()
    {
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE instr(text, '*') > 0 OR instr(text, '_') > 0;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE text LIKE '#%' OR instr(text, char(10) || '#') > 0;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE instr(text, '/api/') > 0;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE instr(text, 'options_array') > 0 OR instr(text, 'counted_reference') > 0;"));
        Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM doc WHERE edition = '2024' AND kind = 'rule' AND instr(text, 'SRD 5.2') > 0;"));
    }

    [Fact]
    public void Text_GlossaryEntry_IsItsDescriptionWithoutEmphasis()
    {
        var text = _fixture.Column("SELECT text FROM doc WHERE edition = '2024' AND kind = 'rule' AND slug = 'prone';").Single();

        Assert.StartsWith("While you have the Prone condition, you experience the following effects.", text, StringComparison.Ordinal);
        Assert.Contains("Restricted Movement. Your only movement options", text, StringComparison.Ordinal);
    }
}
