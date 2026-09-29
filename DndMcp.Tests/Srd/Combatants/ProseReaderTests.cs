using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Combatants;
using Xunit;
using V = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// The prose readers, one phrasing at a time, in both editions' words. Each case is text from an SRD stat block; the
/// whole-data tests show the readers cover every monster, these show each reading is the right one.
/// </summary>
public sealed class ProseReaderTests
{
    [Theory]
    [InlineData("2d10+8", "2d10+8")]
    [InlineData("19d12 + 133", "19d12+133")]
    [InlineData("1d4-1", "1d4-1")]
    [InlineData("1", "1")]
    [InlineData("33d20+330", "33d20+330")]
    public void Dice_MonsterDiceStrings_ReadWithoutTheDslLimits(string text, string expected)
    {
        Assert.Equal(expected, ProseText.Dice(text)!.Text);
    }

    [Theory]
    [InlineData("Melee Weapon Attack: +9 to hit, reach 10 ft., one target.", V.AttackRanges.Melee, false, false, 9)]
    [InlineData("Ranged Spell Attack: +7 to hit, range 120 ft., one target.", V.AttackRanges.Ranged, false, true, 7)]
    [InlineData("Melee or Ranged Weapon Attack: +5 to hit, reach 5 ft. or range 20/60 ft.", V.AttackRanges.Melee, true, false, 5)]
    [InlineData("Melee Attack Roll: +14, reach 10 ft.", V.AttackRanges.Melee, false, false, 14)]
    [InlineData("Melee or Ranged Attack Roll: +12, reach 5 ft. or range 120 ft.", V.AttackRanges.Melee, true, false, 12)]
    [InlineData("Ranged Attack Roll: -1, range 30 ft.", V.AttackRanges.Ranged, false, false, -1)]
    public void ReadAttackHeader_BothEditions_ReadsRangeAndBonus(string text, string range, bool alsoRanged, bool spell, int bonus)
    {
        Assert.Equal(new AttackHeader(range, alsoRanged, spell, bonus), ProseText.ReadAttackHeader(text));
    }

    [Fact]
    public void DamageMentions_ReadDiceFlatUntypedAndAlternativeTypes()
    {
        var mentions = ProseText.DamageMentions(
            "Hit: 12 (2d6 + 5) slashing damage plus 3 (1d6) lightning or thunder damage. Hit: 1 Piercing damage. Failure: 28 (8d6) damage of the type chosen.");

        Assert.Equal(["2d6+5 slashing", "1d6 lightning", "1 piercing", "8d6 "], mentions.Select(m => $"{m.Dice} {m.Type}"));
        Assert.Equal("thunder", mentions[1].AlternativeType);
    }

    [Theory]
    [InlineData("each creature in a 60-foot Cone", V.Shapes.Cone, 60)]
    [InlineData("each creature in a 30-foot-long, 5-foot-wide Line", V.Shapes.Line, 30)]
    [InlineData("each creature in a 90-foot-long, 5-footwide Line", V.Shapes.Line, 90)]
    [InlineData("The dragon exhales acid in a 60-foot line that is 5 feet wide.", V.Shapes.Line, 60)]
    [InlineData("The ankheg spits acid in a line that is 30 ft. long and 5 ft. wide", V.Shapes.Line, 30)]
    [InlineData("each creature in a 20-foot-radius Sphere centered on a point", V.Shapes.Sphere, 20)]
    [InlineData("each creature in a 10-foot-radius, 40-foot-high Cylinder", V.Shapes.Cylinder, 10)]
    [InlineData("each creature in a 15-foot Emanation originating from the vrock", V.Shapes.Emanation, 15)]
    [InlineData("Each creature within 10 ft. of the dragon must succeed", V.Shapes.Emanation, 10)]
    [InlineData("Each creature of the dragon's choice that is within 120 feet of the dragon", V.Shapes.Emanation, 120)]
    [InlineData("Each creature within 10 feet of that point must make", V.Shapes.Sphere, 10)]
    [InlineData("A 15-foot-radius cloud of toxic spores extends out", V.Shapes.Sphere, 15)]
    public void ReadArea_BothEditions_ReadsShapeAndSize(string text, string shape, int size)
    {
        Assert.Equal(new AreaSpec(shape, size), ProseText.ReadArea(ProseText.Normalize(text)));
    }

    [Theory]
    [InlineData("Constitution Saving Throw: DC 21, each creature", "con", 21)]
    [InlineData("must succeed on a DC 13 Strength saving throw or be knocked prone", "str", 13)]
    public void ReadSave_BothEditions_ReadsAbilityAndDc(string text, string ability, int dc)
    {
        var save = ProseText.ReadSave(text)!;

        Assert.Equal((ability, dc), (save.Ability, save.Dc));
    }

    [Fact]
    public void ReadDuration_RepeatTheSaveWithoutASaveClause_IsNotSaveEnds()
    {
        // Save-ends needs the save to repeat: without one the repeat sentence cannot end it, and "for 1 minute" rules.
        const string text = "The target is poisoned for 1 minute. It repeats the saving throw at the end of each of its turns, ending the effect on itself on a success.";

        var reading = ProseText.ReadDuration(text, text.IndexOf("poisoned", StringComparison.Ordinal), save: null);

        Assert.NotNull(reading);
        Assert.Equal((StatBlockValues.Durations.Rounds, (int?)10), (reading.Duration, reading.Rounds));
    }

    [Theory]
    [InlineData("The target does nothing", "The target does nothing.")]
    [InlineData("The target takes no action", "The target takes no action or Bonus Action and uses all its movement to move in a random direction.")]
    public void SentenceEnd_AFullStopBeforeADigit_EndsTheSentence(string from, string sentence)
    {
        // 2024 gibbering mouther, Gibbering: each row of its d8 table starts with the roll ("5-6."), not a capital.
        var text = ProseText.Normalize(
            "Failure: The target rolls 1d8 to determine what it does during the current turn: 1–4. The target does nothing. " +
            "5–6. The target takes no action or Bonus Action and uses all its movement to move in a random direction. " +
            "7–8. The target makes a melee attack against a randomly determined creature within its reach or does nothing if it can’t make such an attack.");
        var start = text.IndexOf(from, StringComparison.Ordinal);

        var end = ProseText.SentenceEnd(text, start);

        Assert.Equal(sentence, text[start..end]);
    }

    [Theory]
    [InlineData("the target is grappled (escape DC 13).", "grappled")]
    [InlineData("it must succeed on a DC 11 Strength saving throw or be knocked prone.", "prone")]
    [InlineData("The target has the Blinded and Restrained conditions until the grapple ends.", "blinded,restrained")]
    [InlineData("or become frightened for 1 minute.", "frightened")]
    [InlineData("The target is restrained by webbing.", "restrained")]
    [InlineData("The target gains 1 Exhaustion level.", "exhaustion")]
    [InlineData("damage if the target is Grappled by the mimic", "")]
    [InlineData("unless the creature is incapacitated", "")]
    [InlineData("The wall lasts until the devil is incapacitated or dies.", "")]
    public void ConditionMentions_ImposedNotTested(string text, string expected)
    {
        Assert.Equal(expected, string.Join(",", ProseText.ConditionMentions(text).Select(c => c.Condition)));
    }

    [Theory]
    [InlineData("or be poisoned for 1 minute. The target can repeat the saving throw at the end of each of its turns, ending the effect on itself on a success.", V.Durations.SaveEnds, 10)]
    [InlineData("The target has the Poisoned condition until the start of the assassin's next turn.", V.Durations.UntilStartOfSourceTurn, null)]
    [InlineData("or be stunned until the end of the otyugh's next turn.", V.Durations.UntilEndOfSourceTurn, null)]
    [InlineData("The target has the Incapacitated condition until the end of its next turn, at which point it repeats the save.", V.Durations.UntilEndOfTargetTurn, null)]
    [InlineData("or be poisoned until the start of its next turn.", V.Durations.UntilStartOfTargetTurn, null)]
    [InlineData("The target has the Frightened condition for 1 minute.", V.Durations.Rounds, 10)]
    [InlineData("is poisoned for 1 hour.", V.Durations.Fight, null)]
    [InlineData("Until this grapple ends, the target is restrained.", V.Durations.UntilEscape, null)]
    public void ReadDuration_BothEditions_ReadsHowLongItLasts(string clause, string duration, int? rounds)
    {
        var mention = ProseText.ConditionMentions(clause)[0];

        var reading = ProseText.ReadDuration(clause, mention.Index, new SaveSpec("con", 12, V.OnSuccess.None))!;

        Assert.Equal((duration, rounds), (reading.Duration, reading.Rounds));
    }

    [Theory]
    [InlineData("The dragon makes a tail attack.", "tail")]
    [InlineData("The tarrasque makes one claw attack or tail attack.", "claw|tail")]
    [InlineData("The vampire makes one unarmed strike.", "unarmed strike")]
    [InlineData("The mummy lord makes one attack with its rotting fist or uses its Dreadful Glare.", "rotting fist|Dreadful Glare")]
    [InlineData("The kraken uses Lightning Strike.", "Lightning Strike")]
    [InlineData("The dragon uses Spellcasting to cast Scorching Ray (level 3 version).", "Scorching Ray")]
    [InlineData("The lich casts Fear, using the same spellcasting ability as Spellcasting.", "Fear")]
    [InlineData("The mummy makes one Rotting Fist or Channel Negative Energy attack.", "Rotting Fist|Channel Negative Energy")]
    public void UsedNames_LegendaryPhrasings_NameWhatTheyUse(string text, string expected)
    {
        Assert.Equal(expected, string.Join("|", ProseText.UsedNames(text)));
    }

    [Theory]
    [InlineData("Wing Attack (Costs 2 Actions)", "Wing Attack", 2)]
    [InlineData("Disrupt Life (Costs 3 Actions)", "Disrupt Life", 3)]
    [InlineData("Tail Attack", "Tail Attack", 1)]
    public void LegendaryCost_2014Names_CarryTheCost(string name, string bare, int cost)
    {
        Assert.Equal((bare, cost), ProseText.LegendaryCost(name));
    }

    [Theory]
    [InlineData("bludgeoning, piercing, and slashing from nonmagical weapons", V.DamageQualifiers.Nonmagical, 3)]
    [InlineData("bludgeoning, piercing, and slashing from nonmagical weapons that aren't silvered", V.DamageQualifiers.NonmagicalNotSilvered, 3)]
    [InlineData("bludgeoning, piercing, and slashing from nonmagical weapons that aren't adamantine", V.DamageQualifiers.NonmagicalNotAdamantine, 3)]
    [InlineData("piercing and slashing from nonmagical weapons that aren't adamantine", V.DamageQualifiers.NonmagicalNotAdamantine, 2)]
    [InlineData("bludgeoning, piercing, and slashing from nonmagical attacks (from stoneskin)", V.DamageQualifiers.Nonmagical, 3)]
    [InlineData("fire", null, 1)]
    public void DamageAdjustments_Qualifiers_AreNeverSplitOnCommas(string text, string? qualifier, int count)
    {
        var log = new NormalizationLog();

        var entries = DamageAdjustmentReader.Read(text, "immunity", log);

        Assert.Equal(count, entries.Count);
        Assert.All(entries, e => Assert.Equal(qualifier, e.Qualifier));
        Assert.All(entries, e => Assert.Contains(e.DamageType, DndMcp.Domain.Features.DslValues.DamageTypes.Set.Values));
        Assert.Empty(log.Warnings);
    }

    [Theory]
    [InlineData("resistance")]
    [InlineData("immunity")]
    public void DamageAdjustments_UnknownQualifierOnAResistanceOrImmunity_IsOtherWithAWarning(string what)
    {
        var log = new NormalizationLog();

        var entry = Assert.Single(DamageAdjustmentReader.Read("piercing from magic weapons wielded by good creatures", what, log));

        Assert.Equal((V.DamageQualifiers.Other, "piercing"), (entry.Qualifier, entry.DamageType));
        Assert.Equal(V.WarningCodes.Approximated, Assert.Single(log.Warnings).Code);
    }

    [Fact]
    public void DamageAdjustments_UnknownQualifierOnAVulnerability_IsLeftOutWithAWarning()
    {
        var log = new NormalizationLog();

        Assert.Empty(DamageAdjustmentReader.Read("piercing from magic weapons wielded by good creatures", DamageAdjustmentReader.Vulnerability, log));
        Assert.Equal(V.WarningCodes.NotModelled, Assert.Single(log.Warnings).Code);
    }

    [Fact]
    public void DamageAdjustments_NoDamageType_IsLeftOutWithAWarning()
    {
        var log = new NormalizationLog();

        Assert.Empty(DamageAdjustmentReader.Read("damage from spells", "resistance", log));
        Assert.Equal(V.WarningCodes.NotModelled, Assert.Single(log.Warnings).Code);
    }

    [Fact]
    public void Log_RepeatedWarningsAndNotes_AreKeptOnce()
    {
        var log = new NormalizationLog();

        log.NotModelled("Bite", "x");
        log.NotModelled("Bite", "x");
        log.Note("n");
        log.Note("n");

        Assert.Equal((1, 1), (log.Warnings.Count, log.Notes.Count));
    }
}
