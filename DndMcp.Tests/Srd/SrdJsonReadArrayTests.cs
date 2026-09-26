using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins what <see cref="SrdJson.ReadArray{T}"/> promises the Phase 2 importer. A null record is refused, because the
/// serializer would otherwise hand back a list holding null. Every failure names the file and the JSON path in its
/// MESSAGE, because a test runner or log line shows only the message. A strict re-vendor failure that says "could not
/// map 'new_field'" without saying where would leave someone searching a 1.3 MB file by hand.
/// </summary>
public sealed class SrdJsonReadArrayTests : IDisposable
{
    private const string Slashing = """{"index":"slashing","name":"Slashing","url":"/api/2014/damage-types/slashing"}""";

    private readonly string _path = Path.Combine(Path.GetTempPath(), "dndmcp-readarray-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Theory]
    [InlineData("""[{"index":"a","url":"/api/2014/a"},null]""", "$[1]")]
    [InlineData("""[null]""", "$[0]")]
    public void ReadArray_NullRecord_ThrowsNamingFileAndIndex(string json, string expectedPath)
    {
        File.WriteAllText(_path, json);

        var ex = Assert.Throws<JsonException>(() => SrdJson.ReadArray<SrdEntry>(_path));

        Assert.Contains(_path, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"at {expectedPath}:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadArray_JsonNull_Throws()
    {
        File.WriteAllText(_path, "null");

        var ex = Assert.Throws<JsonException>(() => SrdJson.ReadArray<SrdEntry>(_path));

        Assert.Contains("contains JSON null, not an array", ex.Message, StringComparison.Ordinal);
    }

    // The two re-vendor failures: an unknown field under the tests' strict options, deep inside a damage entry (which
    // goes through a custom converter), and a missing required field. Each message must carry the absolute path.
    [Theory]
    [InlineData(true, $$"""{"damage_type":{{Slashing}},"damage_dice":"1d6","new_field":1}""", "$[1].damage[0]", "new_field")]
    [InlineData(false, $$"""{"damage_type":{{Slashing}}}""", "$[1].damage[0]", "damage_dice")]
    public void ReadArray_InvalidRecord_MessageNamesFilePathAndLine(bool strict, string damageJson, string expectedPath, string expectedFragment)
    {
        File.WriteAllText(_path, "[\n" + """{"name":"Claw","desc":"d"}""" + ",\n" + """{"name":"Bite","desc":"d","damage":[""" + damageJson + "]}\n]");

        var ex = Assert.Throws<JsonException>(() =>
            SrdJson.ReadArray<MonsterAction2014>(_path, strict ? SrdTestContent.StrictOptions : SrdJson.Options));

        Assert.StartsWith($"{_path} at {expectedPath}, line 3: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        Assert.Equal(expectedPath, ex.Path);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public void ReadArray_ValidFile_ReturnsEveryRecordInOrder()
    {
        File.WriteAllText(_path, """[{"index":"a","url":"/api/2014/a"},{"index":"b","url":"/api/2014/b"}]""");

        var entries = SrdJson.ReadArray<SrdEntry>(_path);

        Assert.Equal(["a", "b"], entries.Select(e => e.Index));
    }
}
