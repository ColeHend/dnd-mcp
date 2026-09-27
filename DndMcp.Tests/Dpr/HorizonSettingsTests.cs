using DndMcp.Domain.Core;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the horizon parameters the tools take (horizon, rounds, rest_preset, encounters_per_day, short_rests)
/// resolve to exactly the assumptions of contract §4.4 — the 2014 DMG's 7 encounters and 2 short rests by default, the
/// light day's 3.5 and 1 labelled unofficial, overrides labelled custom — whatever the headline horizon (the summary
/// reports all three), and every bad value is refused with a message that names it and what is accepted. The label a
/// result prints carries the source, so a model never quotes the light day as a rule.
/// </summary>
public sealed class HorizonSettingsTests
{
    [Fact]
    public void Resolve_Nothing_IsAThreeRoundFight()
    {
        var settings = HorizonSettings.Resolve(null, null, null, null, null);

        Assert.Equal(DprHorizons.Fight, settings.Horizon);
        Assert.Equal(3, settings.Rounds);
        Assert.Same(DayAssumptions.Dmg2014, settings.Day); // for the summary's day figure
        Assert.Equal("a 3-round fight (the mean per round)", settings.Label);
        Assert.Equal(settings.FightLabel, settings.Label);
    }

    [Theory]
    [InlineData("round1", DprHorizons.Round1)]
    [InlineData("Round 1", DprHorizons.Round1)]
    [InlineData("round_1", DprHorizons.Round1)]
    [InlineData("nova", DprHorizons.Round1)]
    [InlineData("FIGHT", DprHorizons.Fight)]
    [InlineData("encounter", DprHorizons.Fight)]
    [InlineData("day", DprHorizons.Day)]
    [InlineData("adventuring-day", DprHorizons.Day)]
    [InlineData(" ", DprHorizons.Fight)]
    public void Resolve_HorizonSpellings_MatchLeniently(string horizon, string expected)
    {
        Assert.Equal(expected, HorizonSettings.Resolve(horizon, null, null, null, null).Horizon);
    }

    [Fact]
    public void Resolve_UnknownHorizon_ListsTheHorizons()
    {
        var error = Assert.Throws<DndInputException>(() => HorizonSettings.Resolve("week", null, null, null, null));

        Assert.Equal(
            "horizon \"week\" is not a horizon; use \"round1\" (one turn: the nova), \"fight\" (the mean over a fight of rounds, " +
            "default 3) or \"day\" (limited features spread over an adventuring day, see rest_preset).",
            error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-3)]
    public void Resolve_RoundsOutOfRange_IsRefusedWhateverTheHorizon(int rounds)
    {
        foreach (var horizon in new[] { "round1", "fight", "day" })
        {
            var error = Assert.Throws<DndInputException>(() => HorizonSettings.Resolve(horizon, rounds, null, null, null));

            Assert.Equal($"rounds is {rounds}; a fight is 1 to 10 rounds (3 is the DMG's convention).", error.Message);
        }
    }

    [Fact]
    public void Resolve_Day_DefaultsToTheDmgDay()
    {
        var settings = HorizonSettings.Resolve("day", 4, null, null, null);

        Assert.Equal(DprHorizons.Day, settings.Horizon);
        Assert.Equal(4, settings.Rounds);
        Assert.Same(DayAssumptions.Dmg2014, settings.Day);
        Assert.Equal(
            "an adventuring day (2014 DMG: 6–8 encounters, 2 short rests; 7 encounters of 4 rounds, 2 short rests)",
            settings.Label);
    }

    [Fact]
    public void Presets_AreTheContractsNumbersAndLabels()
    {
        Assert.Equal((RestPresets.Dmg2014, 7.0, 2), (DayAssumptions.Dmg2014.Preset, DayAssumptions.Dmg2014.EncountersPerDay, DayAssumptions.Dmg2014.ShortRests));
        Assert.Equal("2014 DMG: 6–8 encounters, 2 short rests", DayAssumptions.Dmg2014.Label);
        Assert.Equal((RestPresets.Light, 3.5, 1), (DayAssumptions.Light.Preset, DayAssumptions.Light.EncountersPerDay, DayAssumptions.Light.ShortRests));
        Assert.Equal("light day: 3–4 encounters, 1 short rest — unofficial; 2024 has no adventuring-day guidance", DayAssumptions.Light.Label);
    }

    [Theory]
    [InlineData(null, RestPresets.Dmg2014)]
    [InlineData("dmg2014", RestPresets.Dmg2014)]
    [InlineData("DMG_2014", RestPresets.Dmg2014)]
    [InlineData("dmg", RestPresets.Dmg2014)]
    [InlineData("Light", RestPresets.Light)]
    [InlineData("light day", RestPresets.Light)]
    public void Resolve_PresetSpellings_MatchLeniently(string? preset, string expected)
    {
        Assert.Equal(expected, DayAssumptions.Resolve(preset, null, null).Preset);
    }

    [Theory]
    [InlineData(null, 6.0, null, 6.0, 2, "custom: 6 encounters, 2 short rests")] // the dmg day with E overridden
    [InlineData("light", null, 0, 3.5, 0, "custom: 3.5 encounters, 0 short rests")] // the light day with S overridden
    [InlineData("custom", 4.0, 1, 4.0, 1, "custom: 4 encounters, 1 short rest")]
    [InlineData("dmg2014", 20.0, 5, 20.0, 5, "custom: 20 encounters, 5 short rests")]
    [InlineData("custom", 1.0, 0, 1.0, 0, "custom: 1 encounter, 0 short rests")]
    public void Resolve_Overrides_AreACustomDay(string? preset, double? encounters, int? shortRests, double e, int s, string label)
    {
        var day = DayAssumptions.Resolve(preset, encounters, shortRests);

        Assert.Equal(RestPresets.Custom, day.Preset);
        Assert.Equal(e, day.EncountersPerDay);
        Assert.Equal(s, day.ShortRests);
        Assert.Equal(label, day.Label);
    }

    [Theory]
    [InlineData(4.0, null)]
    [InlineData(null, 1)]
    [InlineData(null, null)]
    public void Resolve_CustomWithoutBothNumbers_SaysWhatItNeeds(double? encounters, int? shortRests)
    {
        var error = Assert.Throws<DndInputException>(() => DayAssumptions.Resolve("custom", encounters, shortRests));

        Assert.StartsWith("rest_preset \"custom\" needs both encounters_per_day and short_rests, e.g.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnknownPreset_ListsThePresets()
    {
        var error = Assert.Throws<DndInputException>(() => DayAssumptions.Resolve("heroic", null, null));

        Assert.Equal(
            "rest_preset \"heroic\" is not a rest preset; use \"dmg2014\" (the 2014 DMG's 6–8 encounters and 2 short rests), \"light\" " +
            "(3–4 encounters, 1 short rest; unofficial) or \"custom\" with encounters_per_day and short_rests, e.g. " +
            "{\"rest_preset\": \"custom\", \"encounters_per_day\": 4, \"short_rests\": 1}.",
            error.Message);
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.99, "0.99")]
    [InlineData(20.5, "20.5")]
    [InlineData(-1.0, "-1")]
    [InlineData(double.NaN, "not a finite number")]
    [InlineData(double.PositiveInfinity, "not a finite number")]
    // A huge value is echoed in exponent form, not as a 301-digit number.
    [InlineData(1e300, "1e+300")]
    [InlineData(-2.5e7, "-2.5e+7")]
    [InlineData(999999.0, "999999")]
    public void Resolve_EncountersOutOfRange_IsRefused(double encounters, string shown)
    {
        var error = Assert.Throws<DndInputException>(() => DayAssumptions.Resolve(null, encounters, null));

        Assert.Equal(
            $"encounters_per_day is {shown}; an adventuring day has 1 to 20 encounters (the 2014 DMG's is 6–8; 3.5 means 3–4).",
            error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void Resolve_ShortRestsOutOfRange_IsRefused(int shortRests)
    {
        var error = Assert.Throws<DndInputException>(() => DayAssumptions.Resolve("light", null, shortRests));

        Assert.Equal($"short_rests is {shortRests}; a day has 0 to 5 short rests between long rests (the 2014 DMG assumes 2).", error.Message);
    }

    [Fact]
    public void Resolve_DayAndRoundsWithAnotherHorizon_AreCheckedAndKeptForTheSummary()
    {
        var fight = HorizonSettings.Resolve("fight", null, "light", 4, null);
        Assert.Equal(("custom: 4 encounters, 1 short rest", 4.0, 1), (fight.Day.Label, fight.Day.EncountersPerDay, fight.Day.ShortRests));
        Assert.Equal("a 3-round fight (the mean per round)", fight.Label);
        Assert.Equal("an adventuring day (custom: 4 encounters, 1 short rest; 4 encounters of 3 rounds, 1 short rest)", fight.DayLabel);

        var round1 = HorizonSettings.Resolve("round1", 5, null, null, null);
        Assert.Equal((DprHorizons.Round1, 5), (round1.Horizon, round1.Rounds));
        Assert.Equal("round 1 (the nova)", round1.Label);
        Assert.Equal("a 5-round fight (the mean per round)", round1.FightLabel);

        Assert.Throws<DndInputException>(() => HorizonSettings.Resolve("fight", null, null, 0, null));
        Assert.Throws<DndInputException>(() => HorizonSettings.Resolve("round1", null, "weekly", null, null));
    }

    [Theory]
    [InlineData(DslValues.Rests.LongRest, 3, 2, 3)]
    [InlineData(DslValues.Rests.ShortRest, 1, 2, 3)]
    [InlineData(DslValues.Rests.ShortRest, 2, 0, 2)]
    [InlineData(DslValues.Rests.ShortRest, 4, 5, 24)]
    public void UsesPerDay_ShortRestFeatures_ComeBackAtEachShortRest(string per, int uses, int shortRests, int expected)
    {
        Assert.Equal(expected, DayAssumptions.Custom(6, shortRests).UsesPerDay(new ResolvedResource(uses, per)));
    }

    [Fact]
    public void EngineOptions_FollowTheHorizon()
    {
        Assert.Equal(DprHorizons.Round1, HorizonSettings.Round1.EngineOptions(true).Horizon);
        var day = HorizonSettings.ForDay(DayAssumptions.Light, 5).EngineOptions(false);
        Assert.Equal((DprHorizons.Fight, 5, false), (day.Horizon, day.Rounds, day.IncludeDistribution));
    }
}
