using DndMcp.Domain.Core;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdKindNames"/>: every spelling of a kind the model is likely to send, mapped to the wire kind the edition
/// actually has, and the exact messages for what cannot be mapped. Those messages are what the model reads to fix its
/// next call, so they are pinned word for word.
/// </summary>
public sealed class SrdKindNamesTests
{
    [Theory]
    [InlineData("spell", "2024", "spell")]
    [InlineData("spells", "2014", "spell")]
    [InlineData("Spells", "2024", "spell")]
    [InlineData(" magic item ", "2014", "magic-item")]
    [InlineData("magic_items", "2024", "magic-item")]
    [InlineData("Magic--Item", "2024", "magic-item")]
    [InlineData("classes", "2024", "class")]
    [InlineData("proficiencies", "2014", "proficiency")]
    [InlineData("equipment", "2024", "equipment")]
    [InlineData("equipment-categories", "2014", "equipment-category")]
    [InlineData("ability-scores", "2024", "ability-score")]
    [InlineData("damage types", "2014", "damage-type")]
    [InlineData("levels", "2014", "level")]
    [InlineData("weapon-properties", "2024", "weapon-property")]
    [InlineData("weapon-mastery-properties", "2024", "weapon-mastery")]   // upstream's API resource name
    [InlineData("weapon-mastery-property", "2024", "weapon-mastery")]
    [InlineData("mastery", "2024", "weapon-mastery")]
    [InlineData("weapon masteries", "2024", "weapon-mastery")]
    [InlineData("glossary", "2024", "rule")]
    [InlineData("rules", "2014", "rule")]
    [InlineData("race", "2014", "race")]
    [InlineData("race", "2024", "species")]         // the same thing, renamed; refusing would only cause a retry
    [InlineData("races", "2024", "species")]
    [InlineData("species", "2014", "race")]
    [InlineData("subrace", "2024", "subspecies")]
    [InlineData("subspecies", "2014", "subrace")]
    [InlineData("poisons", "2024", "poison")]
    public void Normalize_KnownSpelling_ReturnsTheEditionsWireKind(string input, string edition, string expected)
    {
        Assert.Equal(expected, SrdKindNames.Normalize(input, edition));
    }

    [Theory]
    [InlineData("poison", "2014", "Kind 'poison' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    [InlineData("weapon-mastery", "2014", "Kind 'weapon-mastery' is not in the 2014 SRD; only the 2024 SRD has it. Pass edition 2024 (or both).")]
    public void Normalize_KindOnlyInTheOtherEdition_SaysWhichEditionHasIt(string input, string edition, string expected)
    {
        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize(input, edition));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Normalize_UnknownKind_ListsTheEditionsKinds()
    {
        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize("spels", "2024"));

        Assert.Equal(
            "Unknown kind 'spels'. Kinds in the 2024 SRD: ability-score, alignment, background, class, condition, damage-type, " +
            "equipment, equipment-category, feat, feature, language, level, magic-item, magic-school, monster, poison, " +
            "proficiency, rule, skill, species, spell, subclass, subspecies, trait, weapon-mastery, weapon-property.",
            error.Message);
    }

    // The unknown kind is quoted back only as far as a kind could reasonably go; a pasted paragraph is not repeated.
    [Fact]
    public void Normalize_VeryLongUnknownKind_QuotesOnlyItsStart()
    {
        var input = new string('x', 5_000);

        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize(input, "2024"));

        Assert.StartsWith($"Unknown kind '{new string('x', 40)}…' (5,000 characters). Kinds in the 2024 SRD: ability-score,", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 500, $"{error.Message.Length} characters");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("--")]
    public void Normalize_Empty_SaysSoAndListsTheKinds(string? input)
    {
        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize(input, "2014"));

        Assert.StartsWith("The kind is empty. Kinds in the 2014 SRD: ability-score, alignment,", error.Message, StringComparison.Ordinal);
        Assert.Contains("race", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("species", error.Message, StringComparison.Ordinal);
    }

    // edition "both": a kind is valid when either edition has it, and names each edition's own kind.
    [Theory]
    [InlineData("race", "race,species")]
    [InlineData("species", "race,species")]
    [InlineData("poison", "poison")]
    [InlineData("spells", "spell")]
    public void Normalize_BothEditions_ReturnsEachEditionsKind(string input, string expected)
    {
        Assert.Equal(expected, string.Join(',', SrdKindNames.Normalize(input, ["2014", "2024"])));
    }

    [Fact]
    public void Normalize_BothEditionsUnknownKind_ListsEveryKind()
    {
        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize("wands", ["2014", "2024"]));

        Assert.StartsWith("Unknown kind 'wands'. Kinds in the SRD: ability-score,", error.Message, StringComparison.Ordinal);
        Assert.Contains("race", error.Message, StringComparison.Ordinal);
        Assert.Contains("species", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_OneEditionInTheList_BehavesLikeTheSingleEditionForm()
    {
        var error = Assert.Throws<DndInputException>(() => SrdKindNames.Normalize("poison", ["2014"]));

        Assert.StartsWith("Kind 'poison' is not in the 2014 SRD", error.Message, StringComparison.Ordinal);
        Assert.Equal(["species"], SrdKindNames.Normalize("race", ["2024", "2024"]));
    }

    [Fact]
    public void Normalize_UnknownEdition_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => SrdKindNames.Normalize("spell", "both"));
    }

    [Theory]
    [InlineData("2014", 24)]
    [InlineData("2024", 26)]
    public void KindsIn_Edition_HasOneKindPerFilePlusTheGlossaryRules(string edition, int expected)
    {
        Assert.Equal(expected, SrdKindNames.KindsIn(edition).Count);
    }

    // A kind missing from the priority list would sort after every other kind in a name lookup, silently.
    [Fact]
    public void LookupPriority_EveryKind_IsListedExactlyOnce()
    {
        Assert.Equal(
            SrdKinds.All.Select(k => k.Name).Order(StringComparer.Ordinal),
            SrdKindNames.LookupPriority.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PriorityOf_ConditionBeforeSpellBeforeClassBeforeMonster_AndUnknownLast()
    {
        Assert.True(SrdKindNames.PriorityOf("condition") < SrdKindNames.PriorityOf("spell"));
        Assert.True(SrdKindNames.PriorityOf("spell") < SrdKindNames.PriorityOf("class"));
        Assert.True(SrdKindNames.PriorityOf("class") < SrdKindNames.PriorityOf("subclass"));
        Assert.True(SrdKindNames.PriorityOf("subclass") < SrdKindNames.PriorityOf("monster"));
        Assert.True(SrdKindNames.PriorityOf("monster") < SrdKindNames.PriorityOf("species"));
        Assert.True(SrdKindNames.PriorityOf("rule") < SrdKindNames.PriorityOf("trait"));
        Assert.Equal(int.MaxValue, SrdKindNames.PriorityOf("wand"));
    }
}
