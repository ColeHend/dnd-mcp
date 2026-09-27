using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: a damage formula is plain NdM terms and whole numbers joined by + or −, read through the dice grammar and
/// kept in a canonical form (same-sided terms merged) that the engine can take apart die by die; anything that would
/// change what a die means is refused. Bonus dice keep their signs and refuse flat numbers.
/// </summary>
public sealed class DamageFormulaTests
{
    [Theory]
    [InlineData("2d6", "2d6", 0, 2)]
    [InlineData("1d8+1", "1d8+1", 1, 1)]
    [InlineData("1d10+1d6", "1d10+1d6", 0, 2)]
    [InlineData("1d6+1d6", "2d6", 0, 2)]
    [InlineData("1d4-1", "1d4-1", -1, 1)]
    [InlineData("-2+1d8", "1d8-2", -2, 1)]
    [InlineData("1d6+2+1d6-1", "2d6+1", 1, 2)]
    [InlineData(" 2D6 + 3 ", "2d6+3", 3, 2)]
    [InlineData("d8", "1d8", 0, 1)]
    [InlineData("d%", "1d100", 0, 1)]
    [InlineData("3", "3", 3, 0)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("-5", "-5", -5, 0)]
    [InlineData("25d6+25d4", "25d6+25d4", 0, 50)]
    public void ParseDamage_PlainDice_IsCanonical(string text, string canonical, int flat, int dice)
    {
        var formula = DamageFormula.ParseDamage(text, "damage");

        Assert.Equal(canonical, formula.Text);
        Assert.Equal(canonical, formula.ToString());
        Assert.Equal(flat, formula.Flat);
        Assert.Equal(dice, formula.DiceCount);
        Assert.All(formula.Dice, d => Assert.False(d.Negative));
    }

    [Fact]
    public void ParseDamage_Terms_KeepFirstAppearanceOrderAndMergeBySides()
    {
        var formula = DamageFormula.ParseDamage("1d4+2d6+1d4+1d8", "damage");

        Assert.Equal([new DiceTerm(2, 4), new DiceTerm(2, 6), new DiceTerm(1, 8)], formula.Dice);
    }

    [Theory]
    [InlineData("1d4", "1d4")]
    [InlineData("-1d4", "-1d4")]
    [InlineData("1d4-1d4", "1d4-1d4")]
    [InlineData("-1d4-1d4", "-2d4")]
    [InlineData("1d4+1d6", "1d4+1d6")]
    [InlineData("-1d4+1d6", "-1d4+1d6")]
    public void ParseBonusDice_SignedDice_KeepTheirSignsAndBlessPlusBaneDoesNotCancel(string text, string canonical)
    {
        var formula = DamageFormula.ParseBonusDice(text, "dice", "put a flat bonus in amount");

        Assert.Equal(canonical, formula.Text);
        Assert.Equal(0, formula.Flat);
    }

    [Fact]
    public void ParseBonusDice_BlessAndBane_AreTwoTermsWithOppositeSigns()
    {
        var formula = DamageFormula.ParseBonusDice("1d4-1d4", "dice", "hint");

        Assert.Equal([new DiceTerm(1, 4), new DiceTerm(1, 4, Negative: true)], formula.Dice);
    }

    [Theory]
    [InlineData("1d4+1", "dice \"1d4+1\" has a whole number (+1); these are dice only: put a flat bonus in amount.")]
    [InlineData("1d4-2", "dice \"1d4-2\" has a whole number (-2); these are dice only: put a flat bonus in amount.")]
    [InlineData("2", "has a whole number (+2)")]
    [InlineData("2d4kh1", "keeps or drops dice (\"2d4kh1\"); these dice are plain dice joined by + or -, e.g. \"1d4\" (Bless) or \"-1d4\" (Bane).")]
    public void ParseBonusDice_Refused_SaysWhatBelongsWhere(string text, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => DamageFormula.ParseBonusDice(text, "dice", "put a flat bonus in amount"));

        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDamage_Empty_IsRefusedWithAnExample(string? text)
    {
        var ex = Assert.Throws<DndInputException>(() => DamageFormula.ParseDamage(text, "damage"));

        Assert.Equal("damage is empty; damage is plain dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".", ex.Message);
    }

    [Theory]
    [InlineData("1d6+1d8-1d4")]
    [InlineData("2d6-(1d4)")]
    [InlineData("2d6/2")]
    [InlineData("4d6kh3")]
    [InlineData("4d6dl1")]
    [InlineData("1d20ro1")]
    [InlineData("1d6!!")]
    [InlineData("1d20max19")]
    [InlineData("5d10cs>=8")]
    [InlineData("ea")]
    [InlineData("dis")]
    [InlineData("2d6>7")]
    [InlineData("51d6")]
    [InlineData("1d101")]
    [InlineData("1d6+101")]
    [InlineData("1d6-101")]
    [InlineData("2 d6")]
    public void ParseDamage_AnythingButPlainDice_IsRefusedNamingTheField(string text)
    {
        var ex = Assert.Throws<DndInputException>(() => DamageFormula.ParseDamage(text, "damage at level 5"));

        Assert.StartsWith("damage at level 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDamage_KeepingEveryDie_IsThePlainSumTheDiceGrammarAlreadyMakesIt()
    {
        Assert.Equal("1d12", DamageFormula.ParseDamage("1d12kh1", "damage").Text);
        Assert.Equal("4d6", DamageFormula.ParseDamage("4d6kh4", "damage").Text);
    }

    [Fact]
    public void ScaleDice_MultipliesEveryDiceCountButNotTheFlat()
    {
        var formula = DamageFormula.ParseDamage("1d10+1d6+3", "damage");

        Assert.Equal("2d10+2d6+3", formula.ScaleDice(2).Text);
        Assert.Equal("4d10+4d6+3", formula.ScaleDice(4).Text);
        Assert.Same(formula, formula.ScaleDice(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => formula.ScaleDice(0));
    }

    [Fact]
    public void PlusAndConstant_CombineIntoCanonicalForm()
    {
        var dice = DamageFormula.ParseDamage("1d6", "dice");

        Assert.Equal("1d6+3", dice.Plus(DamageFormula.Constant(3)).Text);
        Assert.Equal("2d6", dice.Plus(dice).Text);
        Assert.Same(DamageFormula.Zero, DamageFormula.Constant(0));
        Assert.Equal("0", DamageFormula.Zero.Text);
        Assert.False(DamageFormula.Zero.HasDice);
        Assert.Equal("-1d4+1d6-2", DamageFormula.Of([new DiceTerm(1, 4, true), new DiceTerm(1, 6)], -2).Text);
    }

    [Fact]
    public void Equality_IsByCanonicalText()
    {
        var a = DamageFormula.ParseDamage("1d6+1d6+1", "damage");
        var b = DamageFormula.ParseDamage("2d6+1", "damage");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, DamageFormula.ParseDamage("2d6", "damage"));
        Assert.False(a.Equals(null));
    }
}
