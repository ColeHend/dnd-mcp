using DndMcp.Domain.Probability;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: the concentration DC is max(10, ⌊damage/2⌋), capped at 30 in 2024 only, exactly as the simulator computes
/// it; the save is the given total, else the kept face + the save bonus (sheet proficiency and bonus, else the stat
/// block's, else the modifier) − 2 × Exhaustion in 2024, with no natural-20 success; the conditions that are or include
/// Incapacitated break concentration without a save.
/// </summary>
public sealed class ConcentrationRulesTests
{
    [Theory]
    [InlineData(35, "2014", 17)]
    [InlineData(12, "2014", 10)]
    [InlineData(12, "2024", 10)]
    [InlineData(70, "2014", 35)]
    [InlineData(70, "2024", 30)]
    [InlineData(0, "2024", 10)]
    [InlineData(21, "2014", 10)]
    [InlineData(22, "2014", 11)]
    [InlineData(59, "2024", 29)]
    [InlineData(61, "2024", 30)]
    [InlineData(61, "2014", 30)]
    public void ConcentrationDc_HalfTheDamageAtLeastTenCappedAtThirtyIn2024(int damage, string edition, int dc)
    {
        Assert.Equal(dc, CombatRules.ConcentrationDc(damage, edition));
    }

    [Fact]
    public void ConcentrationDc_EveryDamageBothEditions_AgreesWithTheEngine()
    {
        for (var damage = 0; damage <= 400; damage++)
        {
            Assert.Equal(Fight.ConcentrationDc(damage, is2024: false), CombatRules.ConcentrationDc(damage, "2014"));
            Assert.Equal(Fight.ConcentrationDc(damage, is2024: true), CombatRules.ConcentrationDc(damage, "2024"));
        }
    }

    [Fact]
    public void ConcentrationDc_NegativeDamageOrAnUnknownEdition_IsAHostBug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.ConcentrationDc(-1, "2024"));
        Assert.Throws<ArgumentException>(() => CombatRules.ConcentrationDc(10, "2025"));
    }

    [Theory]
    // score, proficient, PB, bonus, stat block → bonus
    [InlineData(16, true, 4, null, null, 7)]
    [InlineData(16, true, 4, 1, null, 8)]
    [InlineData(14, false, 4, 2, null, 4)]
    [InlineData(16, false, 4, null, 5, 5)]
    [InlineData(16, false, null, null, null, 3)]
    [InlineData(null, false, null, null, null, 0)]
    [InlineData(null, false, null, null, 9, 9)]
    [InlineData(9, true, 2, null, 6, 1)]
    public void SaveBonus_SheetFirstThenStatBlockThenModifier(int? score, bool proficient, int? pb, int? bonus, int? statBlock, int expected)
    {
        Assert.Equal(expected, CombatRules.SaveBonus(new SaveBonusSources(score, proficient, pb, bonus, statBlock)));
    }

    [Fact]
    public void SaveBonus_FromAStatBlock_IsItsSaveBonus()
    {
        var mummyLord = RulesKit.SrdBlock("2014", "mummy-lord");
        var ogre = RulesKit.SrdBlock("2014", "ogre");

        Assert.Equal(mummyLord.SaveBonuses["con"], CombatRules.SaveBonus("con", mummyLord));
        Assert.Equal(8, CombatRules.SaveBonus("con", mummyLord));
        Assert.Equal(3, CombatRules.SaveBonus("CON", ogre));
        Assert.Throws<ArgumentException>(() => CombatRules.SaveBonus("luck", ogre));
    }

    [Theory]
    // con score, proficient list, bonus, PB → bonus
    [InlineData(16, "int,wis,con", 1, 4, 8)]
    [InlineData(16, "int,wis", null, 4, 3)]
    [InlineData(16, "int,wis", 2, 4, 5)]
    [InlineData(null, "con", null, 4, 4)]
    [InlineData(16, "con", null, null, 3)]
    public void SaveBonus_FromASheet_ModifierPlusProficiencyPlusBonus(int? con, string proficient, int? bonus, int? pb, int expected)
    {
        var abilities = con is { } score ? new Dictionary<string, int> { ["con"] = score, ["dex"] = 20 } : null;
        var flat = bonus is { } b ? new Dictionary<string, int> { ["con"] = b } : null;

        Assert.Equal(expected, CombatRules.SaveBonus("con", abilities, proficient.Split(','), flat, pb));
    }

    [Theory]
    [InlineData("poisoned", "poisoned,charmed", true)]
    [InlineData("Poisoned", "poisoned", true)]
    [InlineData("prone", "poisoned", false)]
    [InlineData("prone", "", false)]
    public void IsImmuneToCondition_ByTheStatBlockOrSheetList(string condition, string immunities, bool immune)
    {
        Assert.Equal(immune, CombatRules.IsImmuneToCondition(condition, immunities.Split(',', StringSplitOptions.RemoveEmptyEntries)));
        Assert.False(CombatRules.IsImmuneToCondition(condition, null));
    }

    [Theory]
    // dc, face, total, bonus, exhaustion, edition → total, success
    [InlineData(17, null, 20, 7, 0, "2014", 20, true)]
    [InlineData(17, 8, null, 12, 0, "2014", 20, true)]
    [InlineData(17, 4, null, 12, 0, "2014", 16, false)]
    [InlineData(17, 8, null, 12, 3, "2014", 20, true)]
    [InlineData(15, 15, null, 3, 2, "2024", 14, false)]
    [InlineData(15, 16, null, 3, 2, "2024", 15, true)]
    [InlineData(30, 20, null, 0, 0, "2024", 20, false)]
    public void ConcentrationSave_TotalElseFacePlusBonusMinusTheEditionsPenalty(int dc, int? face, int? total, int bonus, int exhaustion, string edition, int value, bool success)
    {
        var outcome = CombatRules.ConcentrationSave(dc, face, total, bonus, exhaustion, edition);

        Assert.Equal(new SaveOutcome(dc, value, success), outcome);
    }

    [Fact]
    public void ConcentrationSave_NeitherFaceNorTotal_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatRules.ConcentrationSave(10, null, null, 0, 0, "2024"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.ConcentrationSave(10, 0, null, 0, 0, "2024"));
    }

    [Theory]
    [InlineData(7, 0, "2014", D20Mode.Normal, "1d20+7")]
    [InlineData(7, 1, "2014", D20Mode.Normal, "1d20+7")]
    [InlineData(7, 2, "2014", D20Mode.Normal, "1d20+7")]
    [InlineData(7, 3, "2014", D20Mode.Disadvantage, "2d20kl1+7")]
    [InlineData(5, 1, "2024", D20Mode.Normal, "1d20+3")]
    [InlineData(1, 2, "2024", D20Mode.Normal, "1d20-3")]
    public void ConcentrationRoll_ExhaustionPerEdition(int bonus, int exhaustion, string edition, D20Mode mode, string expression)
    {
        var plan = CombatRules.ConcentrationRoll(bonus, exhaustion, edition);

        Assert.Equal(mode, plan.Mode);
        Assert.Equal(expression, plan.Expression);
    }

    [Theory]
    [InlineData("incapacitated", true)]
    [InlineData("paralyzed", true)]
    [InlineData("petrified", true)]
    [InlineData("stunned", true)]
    [InlineData("unconscious", true)]
    [InlineData("Unconscious", true)]
    [InlineData("prone", false)]
    [InlineData("grappled", false)]
    [InlineData("poisoned", false)]
    [InlineData("frightened", false)]
    public void BreaksConcentration_ConditionsThatAreOrIncludeIncapacitated(string condition, bool breaks)
    {
        Assert.Equal(breaks, CombatRules.BreaksConcentration(condition));
        Assert.Equal(breaks, CombatRules.IsIncapacitating(condition));
    }

    [Theory]
    [InlineData(D20Mode.Normal, 5, "1d20+5")]
    [InlineData(D20Mode.Normal, 0, "1d20")]
    [InlineData(D20Mode.Normal, -1, "1d20-1")]
    [InlineData(D20Mode.Advantage, 3, "2d20kh1+3")]
    [InlineData(D20Mode.Disadvantage, 0, "2d20kl1")]
    public void D20Expression_IsInTheDiceGrammar(D20Mode mode, int modifier, string expected)
    {
        var expression = CombatRules.D20Expression(mode, modifier);

        Assert.Equal(expected, expression);
        Assert.Equal(expected, Domain.Dice.DiceExpression.Parse(expression).Text);
    }
}
