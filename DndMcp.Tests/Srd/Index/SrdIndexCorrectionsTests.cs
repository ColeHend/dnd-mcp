using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// How the builder applies <c>content/srd-corrections.json</c> (<see cref="SrdCorrections"/>): the corrected record is
/// what srd.db stores, what search matches and what the name lookup keys, and the reason travels with it in
/// <c>doc.corrections</c>, so corrected text is never shown as the untouched upstream record.
///
/// <para>
/// Each test writes its own corrections into a private content copy, so these hold whatever the shipped file contains.
/// An entry the content cannot take (its record is gone after a re-vendor, or it sets a property the record lacks) is
/// skipped with a build warning, like a manual counterpart whose record is gone: the tests that pin the shipped file
/// (<c>SrdCorrectionsTests</c>) catch it before release, and an installed server keeps serving rules instead of
/// refusing to build over one stale entry.
/// </para>
/// </summary>
public sealed class SrdIndexCorrectionsTests : IDisposable
{
    private const string HeroismText = "When you drink this potion, you gain 10 Temporary Hit Points and the Blessed quxlorem effect.";

    private readonly SrdContentCopy _copy = new();

    public void Dispose() => _copy.Dispose();

    [Fact]
    public void Build_CorrectedRecord_StoresTheCorrectedJsonAndTheReason()
    {
        _copy.WriteCorrections(Correction("2024/magic-item/potion-of-heroism", $$"""{"desc": "{{HeroismText}}"}""", "Upstream text is the Potion of Gaseous Form's."));

        using var index = Build(out var summary);

        var doc = index.Get("2024", "magic-item", "potion-of-heroism")!;
        Assert.Equal(HeroismText, doc.Root.GetProperty("desc").GetString());
        Assert.Equal("Rare", doc.Root.GetProperty("rarity").GetProperty("name").GetString()); // untouched properties stay
        Assert.Equal("Potion of Heroism", doc.Name);
        Assert.Equal(["Upstream text is the Potion of Gaseous Form's."], Column("SELECT corrections FROM doc WHERE slug = 'potion-of-heroism' AND edition = '2024';"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM doc WHERE corrections <> '';"));
        Assert.Empty(summary.Warnings);
    }

    // Search reads the corrected text: the old words are gone and the new ones are found.
    [Fact]
    public void Build_CorrectedRecord_IsSearchedByItsCorrectedText()
    {
        _copy.WriteCorrections(Correction("2024/magic-item/potion-of-heroism", $$"""{"desc": "{{HeroismText}}"}""", "Spliced text."));

        using var index = Build(out _);

        Assert.Equal(["2024/magic-item/potion-of-heroism"], index.Search("quxlorem", ["2024"]).Hits.Select(h => h.Ref.ToString()));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM doc WHERE slug = 'potion-of-heroism' AND edition = '2024' AND instr(text, 'Gaseous Form') > 0;"));
    }

    // The ref stays the record's identity; a corrected name is what the name lookup and the name column use.
    [Fact]
    public void Build_CorrectedName_IsTheNameLookedUpButTheSlugStays()
    {
        _copy.WriteCorrections(Correction("2014/monster/goblin", """{"name": "Goblin Skulker"}""", "Test rename."));

        using var index = Build(out _);

        Assert.Equal("Goblin Skulker", index.Get("2014", "monster", "goblin")!.Name);
        Assert.Equal("2014/monster/goblin", index.FindByName("Goblin Skulker", "2014").Best!.Document.Ref.ToString());
    }

    [Fact]
    public void Build_CorrectedGlossaryEntry_IsStoredCorrected()
    {
        _copy.WriteCorrections(Correction("2024/rule/prone", """{"description": "Corrected prone text."}""", "Test."));

        using var index = Build(out _);

        Assert.Equal("Corrected prone text.", index.Get("2024", "rule", "prone")!.Root.GetProperty("description").GetString());
        Assert.Equal(["Test."], Column("SELECT corrections FROM doc WHERE edition = '2024' AND kind = 'rule' AND slug = 'prone';"));
    }

    [Fact]
    public void Build_CorrectionForARecordTheContentLacks_IsSkippedWithAWarning()
    {
        _copy.WriteCorrections(Correction("2024/spell/no-such-spell", """{"desc": "x"}""", "Test."));

        using var index = Build(out var summary);

        Assert.Equal(
            ["Correction for 2024/spell/no-such-spell skipped: the content has no such record (content/srd-corrections.json)."],
            summary.Warnings);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM doc WHERE corrections <> '';"));
    }

    // Replacing only properties the record has keeps a typo ("descr") from adding a field no formatter reads while the
    // real text stays wrong; such an entry is reported, and the record is stored as upstream wrote it.
    [Fact]
    public void Build_CorrectionSettingAPropertyTheRecordLacks_IsSkippedWithAWarning()
    {
        _copy.WriteCorrections(Correction("2024/spell/fireball", """{"descr": "x"}""", "Test."));

        using var index = Build(out var summary);

        var warning = Assert.Single(summary.Warnings);
        Assert.StartsWith("Correction for 2024/spell/fireball skipped: srd-corrections.json: the correction for \"2024/spell/fireball\" sets \"descr\"", warning, StringComparison.Ordinal);
        Assert.False(index.Get("2024", "spell", "fireball")!.Root.TryGetProperty("descr", out _));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM doc WHERE corrections <> '';"));
    }

    // The staleness key covers the corrections file, and the build records its hash.
    [Fact]
    public void Build_Meta_RecordsTheCorrectionsHash()
    {
        using var index = Build(out _);

        Assert.Equal([SrdIndexContent.Load(_copy.ContentRoot).CorrectionsSha256], Column("SELECT value FROM meta WHERE key = 'corrections_sha256';"));
    }

    private static string Correction(string reference, string set, string reason) =>
        $$"""{"ref": "{{reference}}", "set": {{set}}, "reason": "{{reason}}", "source": "test"}""";

    private SrdIndex Build(out SrdIndexBuildSummary summary)
    {
        summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        return SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey) ?? throw new InvalidOperationException("The index did not open.");
    }

    private IReadOnlyList<string> Column(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_copy.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }

    private long Scalar(string sql) => long.Parse(Column(sql).Single(), System.Globalization.CultureInfo.InvariantCulture);
}
