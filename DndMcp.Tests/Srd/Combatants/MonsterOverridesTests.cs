using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Invariants of <c>content/overrides/monsters.{2014,2024}.json</c>: every entry names a real monster (and a real
/// action), says why, and still changes the stat block it overrides (an override the data no longer needs fails here);
/// the five mislabeled 2014 half-damage saves are each corrected; every 2024 monster has the Initiative its stat block
/// prints, and where the SRD 5.2 markdown is checked out, each value is re-read from it.
/// </summary>
public sealed partial class MonsterOverridesTests
{
    private static MonsterOverrides Shipped => CorrectedSrd.Shipped.Normalizer.Monsters;

    private static StatBlock Normalize(MonsterOverrides overrides, string key)
    {
        var (edition, slug) = (key[..4], key[5..]);
        var normalizer = new MonsterNormalizer(overrides, CorrectedSrd.Shipped.Normalizer.Spells);
        return normalizer.Normalize(CorrectedSrd.Shipped.Monster(edition, slug), CorrectedSrd.Shipped);
    }

    [Fact]
    public void EveryEntry_NamesAMonsterAndItsActions()
    {
        foreach (var (key, entry) in Shipped.Entries)
        {
            var block = Normalize(Shipped, key);
            foreach (var action in entry.Actions?.Keys ?? [])
            {
                Assert.True(block.FindAction(action) is not null, $"{key}: no action {action}");
            }
        }
    }

    /// <summary>Removing any one override (a top-level fact or one action's) changes the normalized stat block.</summary>
    [Fact]
    public void EveryOverride_StillChangesItsStatBlock()
    {
        var dead = new List<string>();
        foreach (var (key, entry) in Shipped.Entries)
        {
            var with = StatBlockJson.Canonical(Normalize(Shipped, key));
            var removals = new List<(string Label, MonsterOverrides Without)>();
            if (entry.Initiative is not null)
            {
                removals.Add(("initiative", Shipped.Without(key, field: "initiative")));
            }

            if (entry.LegendaryUses is not null)
            {
                removals.Add(("legendary_uses", Shipped.Without(key, field: "legendary_uses")));
            }

            if (entry.LegendaryUsesInLair is not null)
            {
                removals.Add(("legendary_uses_in_lair", Shipped.Without(key, field: "legendary_uses_in_lair")));
            }

            removals.AddRange((entry.Actions?.Keys ?? []).Select(a => ($"action {a}", Shipped.Without(key, action: a))));
            dead.AddRange(removals.Where(r => StatBlockJson.Canonical(Normalize(r.Without, key)) == with).Select(r => $"{key} {r.Label}"));
        }

        Assert.Empty(dead);
    }

    /// <summary>
    /// The five 2014 saves whose text halves the damage but whose data says "none" (research 01 §7.4): corrected by the
    /// overrides, and without them the normalizer reports the conflict instead of guessing.
    /// </summary>
    [Theory]
    [InlineData("2014/adult-red-dragon", "Fire Breath")]
    [InlineData("2014/ancient-white-dragon", "Cold Breath")]
    [InlineData("2014/green-dragon-wyrmling", "Poison Breath")]
    [InlineData("2014/lich", "Disrupt Life")]
    [InlineData("2014/winter-wolf", "Cold Breath")]
    public void MislabeledHalfSave_2014_IsHalvedByItsOverride(string key, string action)
    {
        var fixedBlock = Normalize(Shipped, key);
        Assert.Equal(StatBlockValues.OnSuccess.Half, fixedBlock.FindAction(action)!.Save!.OnSuccess);
        Assert.DoesNotContain(fixedBlock.Warnings, w => w.Code == StatBlockValues.WarningCodes.DataConflict);

        var raw = Normalize(Shipped.Without(key, action: action), key);
        Assert.Equal(StatBlockValues.OnSuccess.None, raw.FindAction(action)!.Save!.OnSuccess);
        Assert.Contains(raw.Warnings, w => w.Code == StatBlockValues.WarningCodes.DataConflict && w.Where.EndsWith(action, StringComparison.Ordinal));
    }

    [Fact]
    public void ActionOverrides_2014_AreExactlyTheFiveHalfSaves()
    {
        Assert.Equal(5, Shipped.Entries.Where(e => e.Key.StartsWith("2014/", StringComparison.Ordinal)).Sum(e => e.Value.Actions?.Count ?? 0));
        Assert.DoesNotContain(Shipped.Entries, e => e.Key.StartsWith("2024/", StringComparison.Ordinal) && e.Value.Actions is { Count: > 0 });
    }

    [Fact]
    public void Initiative_2024_EveryMonsterHasOneAndNo2014MonsterDoes()
    {
        var monsters = CorrectedSrd.Shipped.Monsters(SrdEdition.Edition2024).Select(m => $"2024/{m.Slug}").Order(StringComparer.Ordinal);
        var withInitiative = Shipped.Entries.Where(e => e.Value.Initiative is not null).Select(e => e.Key).Order(StringComparer.Ordinal);

        Assert.Equal(monsters, withInitiative);
        Assert.All(CorrectedSrd.Shipped.StatBlocks(SrdEdition.Edition2024), b => Assert.Equal(Shipped.For("2024", b.Ref.Split('/')[^1])!.Initiative, b.InitiativeBonus));
    }

    /// <summary>
    /// Every 2024 Initiative override is the "Initiative: +N (M)" of its stat block in the SRD 5.2 markdown
    /// (<c>12_MonstersA-Z.md</c>, <c>13_Animals.md</c>), mapped by heading: the heading's slug is the monster's index, or
    /// its forms' prefix (Werewolf → werewolf-human/-hybrid/-wolf). Every heading with an Initiative maps, and the
    /// printed score is 10 + the modifier.
    /// </summary>
    [SiblingRepoFact]
    public void Initiative_2024_IsTheSrdMarkdownsValue()
    {
        var monsters = CorrectedSrd.Shipped.Monsters(SrdEdition.Edition2024).ToDictionary(m => m.Slug);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in new[] { "12_MonstersA-Z.md", "13_Animals.md" })
        {
            string? heading = null;
            foreach (var line in File.ReadLines(Path.Combine(SiblingRepoFactAttribute.Srd52, file)))
            {
                if (HeadingLine().Match(line) is { Success: true } h)
                {
                    heading = h.Groups["name"].Value.Trim();
                }

                if (InitiativeLine().Match(line) is not { Success: true } init)
                {
                    continue;
                }

                var modifier = int.Parse(init.Groups["mod"].Value.Replace('−', '-'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                Assert.Equal(10 + modifier, int.Parse(init.Groups["score"].Value, CultureInfo.InvariantCulture));
                var slug = Slug(heading!);
                var targets = monsters.ContainsKey(slug)
                    ? [slug]
                    : monsters.Keys.Where(k => k.StartsWith(slug + "-", StringComparison.Ordinal) && monsters[k].Root.TryGetProperty("forms", out _)).ToList();
                Assert.True(targets.Count > 0, $"{file}: \"{heading}\" maps to no 2024 monster.");
                foreach (var target in targets)
                {
                    Assert.True(found.TryAdd(target, modifier), $"{target} has two Initiative lines.");
                }
            }
        }

        Assert.Equal(monsters.Keys.Order(StringComparer.Ordinal), found.Keys.Order(StringComparer.Ordinal));
        var wrong = found.Where(f => Shipped.For("2024", f.Key)?.Initiative != f.Value).Select(f => $"{f.Key}: markdown {f.Value:+0;-0}, override {Shipped.For("2024", f.Key)?.Initiative}").ToList();
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("""{"2014/goblin": {"initiative": 2}}""", "note")]
    [InlineData("""{"2014/goblin": {"initiative": 2, "note": "x", "speed": 30}}""", "speed")]
    [InlineData("""{"2024/goblin": {"initiative": 2, "note": "x"}}""", "2014/<monster index>")]
    [InlineData("""{"2014/goblin": {"note": "x"}}""", "overrides nothing")]
    [InlineData("""{"2014/goblin": {"actions": {"Scimitar": {"on_success": "quarter", "note": "x"}}}}""", "on_success")]
    [InlineData("""{"2014/goblin": {"actions": {"Scimitar": {"on_success": "half"}}}}""", "note")]
    [InlineData("""{"2014/goblin": {"actions": {"Scimitar": {"note": "x"}}}}""", "overrides nothing")]
    [InlineData("""{"2014/goblin": {"actions": {"Scimitar": {"area": {"shape": "blob", "size": 5}, "note": "x"}}}}""", "shape")]
    [InlineData("""not json""", "not valid")]
    public void Parse_BrokenEntry_IsRefusedWithWhatIsWrong(string json, string fragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => MonsterOverrides.Parse([("2014", json)]));

        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        var empty = Path.Combine(Path.GetTempPath(), "dnd-mcp-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<FileNotFoundException>(() => MonsterOverrides.Load(empty));
            Assert.Throws<FileNotFoundException>(() => SpellOverlay.Load(empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Theory]
    [InlineData("monsters.2014")]
    [InlineData("monsters.2024")]
    [InlineData("spells.2014")]
    [InlineData("spells.2024")]
    public void ProvenanceNote_EveryOverrideFile_HasOne(string name)
    {
        var note = Path.Combine(CorrectedSrd.DefaultContentRoot, MonsterOverrides.DirectoryName, name + ".md");

        Assert.True(File.Exists(note), $"{name}.md is missing.");
        Assert.Contains($"{name}.json", File.ReadAllText(note), StringComparison.Ordinal);
    }

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Replace("’", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal), "[^a-z0-9]+", "-").Trim('-');

    [GeneratedRegex(@"^#{2,4} (?<name>.+?)\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^- \*\*Initiative\*\*:?\s*(?<mod>[+\-−]\d+) \((?<score>\d+)\)")]
    private static partial Regex InitiativeLine();
}
