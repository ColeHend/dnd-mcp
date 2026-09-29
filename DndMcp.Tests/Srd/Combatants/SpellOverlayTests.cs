using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Invariants of <c>content/overrides/spells.{2014,2024}.json</c>: every number is the SRD's own. The 2024 entries are
/// held to the SRD 5.2 markdown (<c>07_Spells.md</c>) where it is checked out; the 2014 entries to the spell record's own
/// text, which ships in the data. Every 2024 combat spell a monster casts has an entry, since a 2024 record alone has no
/// save, area or healing.
/// </summary>
public sealed partial class SpellOverlayTests
{
    private static SpellOverlay Shipped => CorrectedSrd.Shipped.Normalizer.Spells;

    private static readonly string[] AbilityNames = ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"];

    private static readonly string[] Words = ["zero", "one", "two", "three", "four", "five", "six"];

    [SiblingRepoFact]
    public void Entries_2024_EveryValueIsInTheSrdMarkdown()
    {
        var sections = Regex.Split(File.ReadAllText(Path.Combine(SiblingRepoFactAttribute.Srd52, "07_Spells.md")), @"\n(?=#### )")
            .ToDictionary(s => s.Split('\n')[0].TrimStart('#').Trim(), s => s, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (slug, entry) in Shipped.Entries(SrdEdition.Edition2024))
        {
            var name = CorrectedSrd.Shipped.Get(SrdEdition.Edition2024, SrdKinds.Spell, slug)?.Name ?? slug;
            if (!sections.TryGetValue(name, out var section))
            {
                problems.Add($"{slug}: no \"#### {name}\" in 07_Spells.md");
                continue;
            }

            problems.AddRange(Check(slug, entry, section).Select(p => $"{slug}: {p}"));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Entries_2014_EveryValueIsInTheSpellsOwnText()
    {
        var problems = new List<string>();
        foreach (var (slug, entry) in Shipped.Entries(SrdEdition.Edition2014))
        {
            var record = CorrectedSrd.Shipped.Get(SrdEdition.Edition2014, SrdKinds.Spell, slug);
            if (record is null)
            {
                problems.Add($"{slug}: no such 2014 spell");
                continue;
            }

            var root = record.Root;
            var text = string.Join(" ", root.GetProperty("desc").EnumerateArray().Select(e => e.GetString()));
            if (root.TryGetProperty("higher_level", out var higher))
            {
                text += " " + string.Join(" ", higher.EnumerateArray().Select(e => e.GetString()));
            }

            problems.AddRange(Check(slug, entry, text).Select(p => $"{slug}: {p}"));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>What an entry claims, each looked for in the text: dice, damage types, the save's ability, "half as much",
    /// the area's size and shape, counts, conditions, healing, AC and a kill threshold. An entry with an approximation may name an area the
    /// text describes in other words (a wall counted as a line).</summary>
    private static IEnumerable<string> Check(string slug, SpellOverlayEntry entry, string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ");
        var squashed = Regex.Replace(flat, @"\s+", string.Empty).ToLowerInvariant();
        foreach (var damage in (entry.Damage ?? []).Concat(entry.Upcast ?? []))
        {
            if (!squashed.Contains(damage.Dice.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), StringComparison.Ordinal))
            {
                yield return $"dice {damage.Dice} not in the text";
            }

            if (damage.Type is not null && !flat.Contains(damage.Type, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"damage type {damage.Type} not in the text";
            }
        }

        if (entry.Save is { } save && !flat.Contains(AbilityNames[Array.IndexOf(DndMcp.Domain.Features.DslValues.Abilities.All.ToArray(), save)] + " saving throw", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"no {save} saving throw in the text";
        }

        if (entry.OnSuccess is { } onSuccess &&
            (onSuccess == StatBlockValues.OnSuccess.Half) != Regex.IsMatch(flat, "half as much damage|half the initial damage", RegexOptions.IgnoreCase))
        {
            yield return $"on_success {onSuccess} disagrees with the text";
        }

        if (entry.Area is { } area)
        {
            if (!Regex.IsMatch(flat, $@"\b{area.Size}(?:-foot| feet| ft)", RegexOptions.IgnoreCase))
            {
                yield return $"area size {area.Size} not in the text";
            }

            if (entry.Approximation is null && !flat.Contains(area.Shape, StringComparison.OrdinalIgnoreCase) &&
                !(area.Shape == StatBlockValues.Shapes.Emanation && flat.Contains("distance of", StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"area shape {area.Shape} not in the text";
            }
        }

        if (entry.Targets is { } targets && entry.Note is null && !flat.Contains(Words[targets], StringComparison.OrdinalIgnoreCase))
        {
            yield return $"targets {targets} not in the text";
        }

        if (entry.UpcastTargets is > 0 && !Regex.IsMatch(flat, "one (?:additional|more)", RegexOptions.IgnoreCase))
        {
            yield return "upcast targets not in the text";
        }

        if (entry.Condition is { } condition && !flat.Contains(condition.Condition, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"condition {condition.Condition} not in the text";
        }

        foreach (var dice in new[] { entry.Heal, entry.UpcastHeal }.OfType<string>())
        {
            if (!squashed.Contains(dice.ToLowerInvariant(), StringComparison.Ordinal))
            {
                yield return $"healing {dice} not in the text";
            }
        }

        if ((entry.HealModifier == true || entry.DamageAddsModifier == true) && !Regex.IsMatch(flat, "spellcasting ability modifier", RegexOptions.IgnoreCase))
        {
            yield return "modifier not in the text";
        }

        if (entry.AcBonus is { } ac && !flat.Contains($"+{ac} bonus to AC", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"+{ac} AC not in the text";
        }

        if (entry.KillAtOrBelowHp is { } kill && !Regex.IsMatch(flat, $@"\b{kill} hit points or fewer, it dies", RegexOptions.IgnoreCase))
        {
            yield return $"a kill at {kill} hit points or fewer not in the text";
        }

        if (entry.Cantrip == "beams" && !flat.Contains("two beams", StringComparison.OrdinalIgnoreCase))
        {
            yield return "beams not in the text";
        }

        if (entry.Cantrip == "dice" && !Regex.IsMatch(flat, @"levels 5 \(2d", RegexOptions.IgnoreCase))
        {
            yield return "cantrip dice scaling not in the text";
        }
    }

    [Fact]
    public void Entries_2024_CoverEveryCombatSpellA2024MonsterCasts()
    {
        var cast = CorrectedSrd.Shipped.StatBlocks(SrdEdition.Edition2024).SelectMany(b => b.Spells.Concat(b.Reactions.Where(r => r.IsSpell)))
            .Select(s => s.Text)
            .Distinct()
            .ToList();

        Assert.NotEmpty(cast);
        foreach (var name in cast)
        {
            var slug = Regex.Replace(name.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal), "[^a-z0-9]+", "-").Trim('-');
            Assert.True(Shipped.For(SrdEdition.Edition2024, slug) is not null, $"2024 {name} is cast but has no overlay entry.");
        }
    }

    [Theory]
    [InlineData("""{"fireball": {"kind": "blast"}}""", "kind")]
    [InlineData("""{"fireball": {"save": "dexterity"}}""", "ability key")]
    [InlineData("""{"fireball": {"on_success": "quarter"}}""", "half or none")]
    [InlineData("""{"fireball": {"damage": [{"dice": "8d6", "type": "plasma"}]}}""", "damage")]
    [InlineData("""{"fireball": {"damage": [{"dice": "eight", "type": "fire"}]}}""", "damage")]
    [InlineData("""{"fireball": {"cantrip": "rays"}}""", "cantrip")]
    [InlineData("""{"fireball": {"area": {"shape": "blob", "size": 20}}}""", "area")]
    [InlineData("""{"hold-person": {"condition": {"condition": "held", "duration": "save_ends"}}}""", "condition")]
    [InlineData("""{"fireball": {"dice": "8d6"}}""", "not valid")]
    [InlineData("""{"scorching-ray": {"targets": 21}}""", "targets must be 1-20; it is 21")]
    [InlineData("""{"scorching-ray": {"targets": 0}}""", "targets must be 1-20; it is 0")]
    [InlineData("""{"scorching-ray": {"upcast_targets": 11}}""", "upcast_targets must be 0-10; it is 11")]
    [InlineData("""{"fireball": {"area": {"shape": "sphere", "size": 1001}}}""", "size 1-1000")]
    [InlineData("""{"shield": {"ac_bonus": 50}}""", "ac_bonus must be 1-10; it is 50")]
    [InlineData("""{"power-word-kill": {"kill_at_or_below_hp": 0}}""", "kill_at_or_below_hp must be 1-1000; it is 0")]
    public void Parse_BrokenEntry_IsRefusedWithWhatIsWrong(string json, string fragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SpellOverlay.Parse([("2024", json)]));

        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Power Word Kill in both editions: "If the target has 100 Hit Points or fewer, it dies. Otherwise, it takes 12d12
    /// Psychic damage" (2024); "If the creature you choose has 100 hit points or fewer, it dies. Otherwise, the spell has
    /// no effect" (2014). Without its entry the 2014 spell is not cast at all and the 2024 one only deals the 12d12.
    /// </summary>
    [Theory]
    [InlineData("2014", "")]
    [InlineData("2024", "12d12 psychic")]
    public void Overlay_PowerWordKill_KillsAt100HitPointsOrFewerAndOtherwiseDealsItsDamage(string edition, string damage)
    {
        var profile = SpellNormalizer.Normalize(CorrectedSrd.Shipped.Get(edition, SrdKinds.Spell, "power-word-kill")!, Shipped);

        Assert.Equal((SpellProfileKinds.AutoHit, (int?)100, damage),
            (profile.Kind, profile.KillAtOrBelowHp, string.Join(" + ", profile.Damage.Select(d => $"{d.Dice} {d.DamageType}"))));
    }

    [Fact]
    public void Overlay_2024Fireball_TurnsTheThinRecordIntoASave()
    {
        var record = CorrectedSrd.Shipped.Get(SrdEdition.Edition2024, SrdKinds.Spell, "fireball")!;

        var bare = SpellNormalizer.Normalize(record, SpellOverlay.Empty);
        var overlaid = SpellNormalizer.Normalize(record, Shipped);

        Assert.False(bare.IsCombat);
        Assert.Equal((SpellProfileKinds.Save, "dex", StatBlockValues.OnSuccess.Half, new AreaSpec(StatBlockValues.Shapes.Sphere, 20)),
            (overlaid.Kind, overlaid.SaveAbility, overlaid.OnSuccess, overlaid.Area));
    }
}
