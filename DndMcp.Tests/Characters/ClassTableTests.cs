using System.Text.Json;
using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: the class table is the vendored data's 12 classes in both editions: the same hit dice, starting saving
/// throws and spellcasting (a class without a <c>spellcasting</c> record does not cast, the warlock's is Pact Magic, the
/// paladin and ranger are half casters by their level tables); names match forgivingly and nothing else matches.
/// </summary>
public sealed class ClassTableTests
{
    [Theory]
    [InlineData(E2014)]
    [InlineData(E2024)]
    public void All_TwelveClasses_MatchTheVendoredClassRecords(string edition)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "5e-database", "v7.0.0", edition, "5e-SRD-Classes.json");
        var records = JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray().ToList();

        Assert.Equal(records.Select(r => r.GetProperty("index").GetString()), ClassTable.All.Select(c => c.Index));
        foreach (var record in records)
        {
            var info = ClassTable.Find(record.GetProperty("index").GetString())!;
            Assert.Equal(record.GetProperty("name").GetString(), info.Name);
            Assert.Equal(record.GetProperty("hit_die").GetInt32(), info.HitDie);
            Assert.Equal(record.GetProperty("saving_throws").EnumerateArray().Select(s => s.GetProperty("index").GetString()), info.Saves);
            Assert.Equal(record.TryGetProperty("spellcasting", out _), info.IsCaster);
        }
    }

    [Theory]
    [InlineData("paladin", "half")]
    [InlineData("ranger", "half")]
    [InlineData("warlock", "pact")]
    [InlineData("wizard", "full")]
    [InlineData("bard", "full")]
    [InlineData("fighter", "none")]
    public void CasterKind_IsTheClassTablesKind(string index, string kind)
    {
        Assert.Equal(kind, ClassTable.Find(index)!.CasterKind);
    }

    [Theory]
    [InlineData("Wizard", "wizard")]
    [InlineData(" WIZARD ", "wizard")]
    [InlineData("Bard", "bard")]
    public void Find_ForgivingSpelling_IsTheSrdClass(string text, string index)
    {
        Assert.Equal(index, ClassTable.Find(text)!.Index);
    }

    [Theory]
    [InlineData("artificer")]
    [InlineData("Dragon Slayer")]
    [InlineData("")]
    [InlineData(null)]
    public void Find_NotAnSrdClass_IsNull(string? text)
    {
        Assert.Null(ClassTable.Find(text));
    }
}
