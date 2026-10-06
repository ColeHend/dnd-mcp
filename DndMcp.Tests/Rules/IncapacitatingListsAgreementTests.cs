using DndMcp.Domain.Characters;
using DndMcp.Domain.Rules;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: the sheet and the combat tracker break concentration on the same conditions. The sheet's list
/// (<see cref="SheetValues.Conditions.Incapacitating"/>) was written in parallel with the shared rules' list
/// (<see cref="CombatRules.IncapacitatingConditions"/>) because neither agent could see the other's file; if the two ever
/// differ, a character who keeps concentration on the sheet would lose it in the fight (or the reverse), and the
/// end-of-combat write-back would then disagree with what the sheet rules say.
/// </summary>
public sealed class IncapacitatingListsAgreementTests
{
    [Fact]
    public void SheetAndCombatRules_IncapacitatingConditions_AreTheSameFive()
    {
        Assert.Equal(
            CombatRules.IncapacitatingConditions.Order(StringComparer.Ordinal),
            SheetValues.Conditions.Incapacitating.Order(StringComparer.Ordinal));
        Assert.Equal(5, CombatRules.IncapacitatingConditions.Count);
    }

    [Theory]
    [InlineData("incapacitated")]
    [InlineData("paralyzed")]
    [InlineData("petrified")]
    [InlineData("stunned")]
    [InlineData("unconscious")]
    public void IsIncapacitating_EverySheetListEntry_IsTrue(string condition)
    {
        Assert.Contains(condition, SheetValues.Conditions.Incapacitating);
        Assert.True(CombatRules.IsIncapacitating(condition));
    }
}
