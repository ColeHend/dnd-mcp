using System.Text.Json.Nodes;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
using DndMcp.Repository.Srd.Models;
using DndMcp.Tests.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Invariant (Phase 5 exit criterion): every SRD monster of both editions normalizes or warns. All 675 normalize
/// without an exception; every action and trait is classified; nothing the simulator cannot run passes unsaid; and
/// the warnings per edition and code are pinned, so a re-vendor or a normalizer change that alters what is understood
/// shows up here as a deliberate diff rather than as a quietly different simulation.
/// </summary>
public sealed partial class MonsterNormalizerCoverageTests
{
    public static TheoryData<string> Editions => new(SrdEdition.All);

    private static IReadOnlyList<StatBlock> Blocks(string edition) => CorrectedSrd.Shipped.StatBlocks(edition);

    private static IEnumerable<StatBlockAction> AllActions(StatBlock b) =>
        b.Actions.Concat(b.BonusActions).Concat(b.Reactions).Concat(b.Spells).Concat(b.Legendary?.Actions ?? []);

    [Theory]
    [InlineData("2014", 334)]
    [InlineData("2024", 341)]
    public void Normalize_EveryMonster_ReturnsAStatBlock(string edition, int count)
    {
        var blocks = Blocks(edition);

        Assert.Equal(count, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(edition, b.Edition));
        Assert.Equal(count, blocks.Select(b => b.Ref).Distinct().Count());
    }

    /// <summary>
    /// The warnings by code. A change here is a change in what the simulator understands about the SRD: review the
    /// warnings file the normalizer produces (every warning names its monster, where and why) before updating a number.
    /// </summary>
    [Theory]
    [InlineData("2014", StatBlockValues.WarningCodes.NotModelled, 215)]
    [InlineData("2014", StatBlockValues.WarningCodes.Approximated, 96)]
    [InlineData("2014", StatBlockValues.WarningCodes.UnresolvedReference, 3)]
    [InlineData("2014", StatBlockValues.WarningCodes.DataConflict, 0)]
    [InlineData("2014", StatBlockValues.WarningCodes.Unparsed, 0)]
    [InlineData("2024", StatBlockValues.WarningCodes.NotModelled, 191)]
    [InlineData("2024", StatBlockValues.WarningCodes.Approximated, 116)]
    [InlineData("2024", StatBlockValues.WarningCodes.UnresolvedReference, 2)]
    [InlineData("2024", StatBlockValues.WarningCodes.DataConflict, 0)]
    [InlineData("2024", StatBlockValues.WarningCodes.Unparsed, 0)]
    public void Warnings_ByEditionAndCode_ArePinned(string edition, string code, int expected)
    {
        var warnings = Blocks(edition).SelectMany(b => b.Warnings.Select(w => (b.Ref, w))).Where(x => x.w.Code == code).ToList();

        Assert.True(
            expected == warnings.Count,
            $"{edition} {code}: expected {expected}, found {warnings.Count}:\n" + string.Join("\n", warnings.Select(x => $"{x.Ref} | {x.w.Where} | {x.w.Message}")));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Warnings_EveryOne_UsesAKnownCodeAndSaysWhereAndWhy(string edition)
    {
        Assert.All(Blocks(edition).SelectMany(b => b.Warnings), w =>
        {
            Assert.Contains(w.Code, StatBlockValues.WarningCodes.All);
            Assert.False(string.IsNullOrWhiteSpace(w.Where));
            Assert.False(string.IsNullOrWhiteSpace(w.Message));
        });
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Kinds_EveryActionAndTrait_IsInTheVocabulary(string edition)
    {
        foreach (var block in Blocks(edition))
        {
            Assert.All(AllActions(block), a =>
            {
                Assert.Contains(a.Kind, StatBlockValues.ActionKinds.All);
                Assert.Contains(a.Slot, StatBlockValues.ActionSlots.All);
                Assert.Contains(a.Usage.Kind, StatBlockValues.UsageKinds.All);
            });
            Assert.All(block.Traits, t => Assert.Contains(t.Kind, StatBlockValues.TraitKinds.All));
        }
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Attacks_EveryOne_HasAnAttackBonusAndARange(string edition)
    {
        var attacks = Blocks(edition).SelectMany(AllActions).Where(a => a.Kind == StatBlockValues.ActionKinds.Attack).ToList();

        Assert.NotEmpty(attacks);
        Assert.All(attacks, a =>
        {
            Assert.NotNull(a.AttackBonus);
            Assert.InRange(a.AttackBonus!.Value, -5, 25);
            Assert.Contains(a.Range, StatBlockValues.AttackRanges.All);
            Assert.InRange(a.AttackRolls, 1, 10);
        });
    }

    /// <summary>Every save the simulator rolls has a real DC: none is 0 or computed (Undead Fortitude's is a trait, not an action).</summary>
    [Theory]
    [MemberData(nameof(Editions))]
    public void Saves_EveryOne_HasADcAndAnAbility(string edition)
    {
        var saves = Blocks(edition).SelectMany(AllActions)
            .SelectMany(a => a.OnHit.Select(e => e.Save).Append(a.Save))
            .Concat(Blocks(edition).SelectMany(b => b.Traits).Select(t => t.Save))
            .OfType<SaveSpec>()
            .ToList();

        Assert.NotEmpty(saves);
        Assert.All(saves, s =>
        {
            Assert.InRange(s.Dc, 5, 30);
            Assert.Contains(s.Ability, DslValues.Abilities.All);
            Assert.Contains(s.OnSuccess, StatBlockValues.OnSuccess.All);
        });
        Assert.All(Blocks(edition).SelectMany(AllActions).Where(a => a.Kind == StatBlockValues.ActionKinds.Save), a => Assert.NotNull(a.Save));
    }

    /// <summary>
    /// Every damage roll has a known type, except where the stat block itself names none: the 2024 Half-Dragon's Claw
    /// rider and breath deal "damage of the type chosen for the Draconic Origin trait". Pinned so a new untyped roll is
    /// looked at.
    /// </summary>
    [Theory]
    [InlineData("2014", 0)]
    [InlineData("2024", 2)]
    public void DamageRolls_EveryType_IsKnownOrTheCountedUntypedOnes(string edition, int untyped)
    {
        var rolls = Blocks(edition).SelectMany(b => AllActions(b)
                .SelectMany(a => a.Damage.Concat(a.OnHit.SelectMany(e => e.Damage.Concat(e.Condition?.OngoingDamage ?? [])))
                    .Concat(a.Condition?.OngoingDamage ?? []))
                .Concat(b.Traits.SelectMany(t => t.Damage)))
            .ToList();

        Assert.All(rolls.Where(r => r.DamageType is not null), r => Assert.True(DslValues.DamageTypes.Set.Contains(r.DamageType), r.DamageType));
        Assert.All(rolls, r => Assert.True(r.Dice.HasDice || r.Dice.Flat > 0, r.Dice.Text));
        Assert.Equal(untyped, rolls.Count(r => r.DamageType is null));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Conditions_EveryOne_IsInTheVocabularyWithACoherentDuration(string edition)
    {
        var conditions = Blocks(edition).SelectMany(AllActions)
            .SelectMany(a => a.OnHit.SelectMany(e => e.ExtraConditions.Prepend(e.Condition)).Concat(a.ExtraConditions.Prepend(a.Condition)))
            .Concat(Blocks(edition).SelectMany(b => b.Traits).Select(t => t.Condition))
            .OfType<ConditionEffect>()
            .ToList();

        Assert.NotEmpty(conditions);
        Assert.All(conditions, c =>
        {
            Assert.Contains(c.Condition, StatBlockValues.Conditions.All);
            Assert.Contains(c.Duration, StatBlockValues.Durations.All);
            Assert.Equal(c.Duration == StatBlockValues.Durations.UntilEscape, c.EscapeDc is not null);
            if (c.Duration == StatBlockValues.Durations.Rounds)
            {
                Assert.NotNull(c.Rounds);
            }
        });
    }

    /// <summary>Every multiattack step and option and every use-actions reference names an action or spell of the same stat block.</summary>
    [Theory]
    [MemberData(nameof(Editions))]
    public void References_EveryStepAndUse_ResolvesToAnActionOrSpell(string edition)
    {
        foreach (var block in Blocks(edition))
        {
            var names = block.Multiattacks.SelectMany(r => r.Steps.Concat(r.Options)).Concat(AllActions(block).SelectMany(a => a.Uses)).Select(u => u.ActionName);
            Assert.All(names, n => Assert.True(block.FindAction(n) is not null, $"{block.Ref}: {n}"));
            Assert.All(block.Multiattacks, r =>
            {
                Assert.True(r.TotalUses > 0, block.Ref);
                Assert.True(r.Choose == 0 || r.Options.Count > 0, block.Ref);
            });
        }
    }

    /// <summary>Nothing is dropped unsaid: every not-modelled action or trait is named by a warning or, for one with no effect in a fight, a note.</summary>
    [Theory]
    [MemberData(nameof(Editions))]
    public void NotModelled_EveryActionAndTrait_IsWarnedOrNoted(string edition)
    {
        foreach (var block in Blocks(edition))
        {
            var said = block.Warnings.Select(w => w.Where + " " + w.Message).Concat(block.Notes).ToList();
            var silent = AllActions(block).Where(a => a.Kind == StatBlockValues.ActionKinds.NotModelled).Select(a => a.Name)
                .Concat(block.Traits.Where(t => t.Kind is StatBlockValues.TraitKinds.NotModelled or StatBlockValues.TraitKinds.NoCombatEffect)
                    .Select(t => ProseText.StripParentheticals(t.Name)))
                .Where(name => !said.Any(s => s.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.True(silent.Count == 0, $"{block.Ref}: {string.Join(", ", silent)}");
        }
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Numbers_EveryStatBlock_HasSixSavesHitDiceAndSpeeds(string edition)
    {
        Assert.All(Blocks(edition), b =>
        {
            Assert.Equal(DslValues.Abilities.All.Order(), b.SaveBonuses.Keys.Order());
            Assert.True(b.HitDice.HasDice, b.Ref);
            Assert.InRange(b.ArmorClass, 5, 25);
            Assert.True(b.Speeds.ContainsKey("walk"), b.Ref);
            Assert.InRange(b.HitDice.DiceCount * 1.0, 1, 60);
        });
    }

    [Fact]
    public void Initiative_2014_IsTheDexterityModifier()
    {
        Assert.All(Blocks(SrdEdition.Edition2014), b => Assert.Equal(b.Modifier(DslValues.Abilities.Dex), b.InitiativeBonus));
    }

    [Fact]
    public void Legendary_Uses_AreThreeAndFourInLairExactlyWhenThe2024DataHasInLairXp()
    {
        foreach (var block in Blocks(SrdEdition.Edition2014).Concat(Blocks(SrdEdition.Edition2024)).Where(b => b.Legendary is not null))
        {
            Assert.Equal(3, block.Legendary!.Uses);
            Assert.Equal(block.XpInLair is not null ? 4 : null, block.Legendary.UsesInLair);
            Assert.All(block.Legendary.Actions, a => Assert.Equal(StatBlockValues.ActionSlots.Legendary, a.Slot));
        }

        Assert.Equal(32, Blocks(SrdEdition.Edition2014).Count(b => b.Legendary is not null));
        Assert.Equal(32, Blocks(SrdEdition.Edition2024).Count(b => b.Legendary is not null));
    }

    /// <summary>
    /// Legendary Resistance's in-lair uses are kept only when they differ from the daily uses, as
    /// <see cref="StatBlock.LegendaryResistanceInLair"/> promises ("null when the same"). No vendored record repeats its
    /// daily count as the lair count, so the 2024 unicorn's record (3/Day, no lair count) is given one.
    /// </summary>
    [Theory]
    [InlineData(3, null)]
    [InlineData(4, 4)]
    public void LegendaryResistance_InLairUses_AreKeptOnlyWhenTheyDiffer(int inLair, int? expected)
    {
        var unicorn = CorrectedSrd.Shipped.Monster(SrdEdition.Edition2024, "unicorn");
        var record = JsonNode.Parse(unicorn.Json)!;
        var trait = record["special_abilities"]!.AsArray().Single(t => (string?)t!["name"] == "Legendary Resistance")!;
        trait["usage"]!["times_in_lair"] = inLair;
        var document = new SrdDocument { Edition = unicorn.Edition, Kind = unicorn.Kind, Slug = unicorn.Slug, Name = unicorn.Name, Json = record.ToJsonString() };

        var block = CorrectedSrd.Shipped.Normalizer.Normalize(document, CorrectedSrd.Shipped);

        Assert.Equal((3, expected), (block.LegendaryResistance, block.LegendaryResistanceInLair));
    }

    /// <summary>A shipped monster's record with one edit, normalized: for rules no vendored record exercises.</summary>
    private static StatBlock NormalizeEdited(string edition, string slug, Action<JsonNode> edit)
    {
        var monster = CorrectedSrd.Shipped.Monster(edition, slug);
        var record = JsonNode.Parse(monster.Json)!;
        edit(record);
        var document = new SrdDocument { Edition = monster.Edition, Kind = monster.Kind, Slug = monster.Slug, Name = monster.Name, Json = record.ToJsonString() };
        return CorrectedSrd.Shipped.Normalizer.Normalize(document, CorrectedSrd.Shipped);
    }

    private static JsonNode Hellfire(JsonNode pitFiend) => pitFiend["actions"]!.AsArray().Single(a => (string?)a!["name"] == "Hellfire Spellcasting")!;

    /// <summary>
    /// A spellcasting ability the normalizer cannot read is never silently Intelligence: the fallback is named in an
    /// unparsed warning. (No vendored block has one: the pit fiend's is given "luck".)
    /// </summary>
    [Fact]
    public void Spellcasting_UnknownAbility_FallsBackToIntelligenceWithAWarning()
    {
        var block = NormalizeEdited(SrdEdition.Edition2024, "pit-fiend", r => Hellfire(r)["spellcasting"]!["ability"]!["index"] = "luck");

        Assert.Contains(block.Warnings, w => w.Code == StatBlockValues.WarningCodes.Unparsed && w.Where == "Hellfire Spellcasting" && w.Message.Contains("\"luck\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Uses per day on a spellcasting action cannot be one shared count (the engine counts per action): a block whose
    /// several combat spells, or whose cast-twice routine, would share them says so. (No vendored block does: the pit
    /// fiend's Hellfire Spellcasting is given "1/Day" for its recharge.)
    /// </summary>
    [Fact]
    public void Spellcasting_PerDayUsesOverSeveralSpells_AreWarnedAsCountedPerSpell()
    {
        var block = NormalizeEdited(SrdEdition.Edition2024, "pit-fiend", r => Hellfire(r)["usage"] = new JsonObject { ["type"] = "per day", ["times"] = 1 });

        Assert.Contains(block.Warnings, w => w.Code == StatBlockValues.WarningCodes.Approximated && w.Where == "Hellfire Spellcasting" && w.Message.Contains("one count for all its spells", StringComparison.Ordinal));
        Assert.Null(block.FindAction("Fireball")!.Usage.Pool);
    }

    /// <summary>
    /// The immunity clause on an attack's save rider sets <see cref="ActionEffect.ImmuneAfterSuccess"/>, as on a save
    /// action. (No vendored attack has one the simulator builds: the 2014 goblin's Scimitar is given a frightening rider.)
    /// </summary>
    [Fact]
    public void ImmuneAfterSuccess_OnASaveRider_IsSetFromTheText()
    {
        var block = NormalizeEdited(SrdEdition.Edition2014, "goblin", r =>
        {
            var scimitar = r["actions"]!.AsArray().Single(a => (string?)a!["name"] == "Scimitar")!;
            scimitar["desc"] = "Melee Weapon Attack: +4 to hit, reach 5 ft., one target. Hit: 5 (1d6 + 2) slashing damage, and the target must " +
                               "succeed on a DC 11 Wisdom saving throw or be frightened for 1 minute. A creature can repeat the saving throw at the " +
                               "end of each of its turns, ending the effect on itself on a success. If a creature's saving throw is successful or the " +
                               "effect ends for it, the creature is immune to this goblin's Scimitar for the next 24 hours.";
        });

        var rider = Assert.Single(block.FindAction("Scimitar")!.OnHit);
        Assert.Equal((StatBlockValues.EffectKinds.Save, StatBlockValues.Conditions.Frightened, true), (rider.Kind, rider.Condition!.Condition, rider.ImmuneAfterSuccess));
    }

    /// <summary>
    /// The immunity must name a word of the action's name: "immune to this goblin's curse" after a Scimitar's rider is
    /// immunity to something else, so the rider is saved against every time. No vendored block pins this: the 2024
    /// lycanthrope bites that say "immune to this werewolf's curse" never build their rider (the curse is not
    /// simulated), so the goblin's Scimitar is given one that the simulator does build.
    /// </summary>
    [Fact]
    public void ImmuneAfterSuccess_ToSomethingTheActionIsNotNamedFor_IsNotSet()
    {
        var block = NormalizeEdited(SrdEdition.Edition2014, "goblin", r =>
        {
            var scimitar = r["actions"]!.AsArray().Single(a => (string?)a!["name"] == "Scimitar")!;
            scimitar["desc"] = "Melee Weapon Attack: +4 to hit, reach 5 ft., one target. Hit: 5 (1d6 + 2) slashing damage, and the target must " +
                               "succeed on a DC 11 Wisdom saving throw or be frightened for 1 minute. A creature can repeat the saving throw at the " +
                               "end of each of its turns, ending the effect on itself on a success. If a creature's saving throw is successful or the " +
                               "effect ends for it, the creature is immune to this goblin's curse for the next 24 hours.";
        });

        var rider = Assert.Single(block.FindAction("Scimitar")!.OnHit);
        Assert.Equal((StatBlockValues.EffectKinds.Save, StatBlockValues.Conditions.Frightened, false), (rider.Kind, rider.Condition!.Condition, rider.ImmuneAfterSuccess));
    }

    /// <summary>
    /// A save rider that deals damage now and more every turn, with no condition for the per-turn damage to ride on,
    /// keeps the damage it can simulate and names the per-turn damage it drops in a not_modelled warning (never a
    /// silent loss). No vendored attack has one: the goblin's Scimitar is given it.
    /// </summary>
    [Fact]
    public void OngoingDamage_OnASaveRiderWithNoCondition_IsWarnedAsNotModelled()
    {
        var block = NormalizeEdited(SrdEdition.Edition2014, "goblin", r =>
        {
            var scimitar = r["actions"]!.AsArray().Single(a => (string?)a!["name"] == "Scimitar")!;
            scimitar["desc"] = "Melee Weapon Attack: +4 to hit, reach 5 ft., one target. Hit: 5 (1d6 + 2) slashing damage, and the target must " +
                               "succeed on a DC 11 Constitution saving throw or take 7 (2d6) necrotic damage. The target also takes 3 (1d6) necrotic " +
                               "damage at the start of each of its turns for 1 minute.";
        });

        var rider = Assert.Single(block.FindAction("Scimitar")!.OnHit);
        Assert.Equal((StatBlockValues.EffectKinds.Save, "2d6 necrotic", (ConditionEffect?)null), (rider.Kind, string.Join(" + ", rider.Damage.Select(d => $"{d.Dice} {d.DamageType}")), rider.Condition));
        Assert.Contains(block.Warnings, w => w.Code == StatBlockValues.WarningCodes.NotModelled && w.Where == "Scimitar" && w.Message.Contains("1d6 necrotic", StringComparison.Ordinal));
    }

    /// <summary>
    /// A conditional sentence too long to quote whole keeps its start (the test) and its end (what it does), cut
    /// between them, never after its first characters: the effect a warning exists to report comes last. No vendored
    /// conditional sentence is over the 240 characters quoted whole, so the goblin's Scimitar is given one.
    /// </summary>
    [Fact]
    public void ConditionalRun_ASentenceTooLongToQuoteWhole_KeepsItsTestAndTheEffectAtItsEnd()
    {
        var block = NormalizeEdited(SrdEdition.Edition2014, "goblin", r =>
        {
            var scimitar = r["actions"]!.AsArray().Single(a => (string?)a!["name"] == "Scimitar")!;
            scimitar["desc"] = "Melee Weapon Attack: +4 to hit, reach 5 ft., one target. Hit: 5 (1d6 + 2) slashing damage. If the goblin moved at " +
                               "least 20 feet straight toward the target immediately before the hit, the target stands on mud, loose scree, shallow " +
                               "water, thick undergrowth or any other ground the goblin knows well from its warren, and the target cannot see the " +
                               "goblin, the target takes an extra 7 (2d6) slashing damage and is knocked prone.";
        });

        var warning = Assert.Single(block.Warnings, w => w.Code == StatBlockValues.WarningCodes.NotModelled && w.Where == "Scimitar");
        Assert.StartsWith("Not simulated: \"If the goblin moved at least 20 feet", warning.Message, StringComparison.Ordinal);
        Assert.Contains(" … ", warning.Message, StringComparison.Ordinal);
        Assert.EndsWith("the target takes an extra 7 (2d6) slashing damage and is knocked prone.\"", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Every multiattack option type in both editions' data is one of <see cref="MultiattackOptionTypes"/>: a new one would be read as a single action.</summary>
    [Theory]
    [MemberData(nameof(Editions))]
    public void MultiattackOptions_EveryOptionType_IsAKnownValue(string edition)
    {
        var types = new List<string>();
        void Collect(JsonNode? option)
        {
            types.Add((string?)option!["option_type"] ?? "(none)");
            foreach (var item in option["items"]?.AsArray() ?? [])
            {
                Collect(item);
            }
        }

        foreach (var monster in CorrectedSrd.Shipped.Monsters(edition))
        {
            foreach (var action in JsonNode.Parse(monster.Json)!["actions"]?.AsArray() ?? [])
            {
                foreach (var option in action!["action_options"]?["from"]?["options"]?.AsArray() ?? [])
                {
                    Collect(option);
                }
            }
        }

        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.Contains(t, new[] { MultiattackOptionTypes.Action, MultiattackOptionTypes.Multiple }));
    }

    [Fact]
    public void LegendaryCosts_2014_ComeFromTheNames()
    {
        // One entry per legendary action of the data: an "X or Y" action split into alternatives ("Chomp (Bite)",
        // "Chomp (Swallow)") is counted once.
        var actions = Blocks(SrdEdition.Edition2014)
            .SelectMany(b => (b.Legendary?.Actions ?? []).Select(a => (b.Ref, Name: AlternativeSuffix().Replace(a.Name, string.Empty), a.LegendaryCost)))
            .Distinct()
            .ToList();

        // "(Costs 2 Actions)" ×35 and "(Costs 3 Actions)" ×6 in the data.
        Assert.Equal(35, actions.Count(a => a.LegendaryCost == 2));
        Assert.Equal(6, actions.Count(a => a.LegendaryCost == 3));
        Assert.DoesNotContain(actions, a => a.Name.Contains("Costs", StringComparison.Ordinal));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@" \([^)]*\)$")]
    private static partial System.Text.RegularExpressions.Regex AlternativeSuffix();

    [Fact]
    public void OncePerRound_2024_IsEveryLegendaryActionThatSaysSo()
    {
        var legendary = Blocks(SrdEdition.Edition2024).SelectMany(b => b.Legendary?.Actions ?? []).ToList();

        Assert.All(legendary, a => Assert.Equal(ProseText.IsOncePerRound(ProseText.Normalize(a.Text)), a.OncePerRound));
        Assert.Equal(43, legendary.Count(a => a.OncePerRound));
        Assert.All(Blocks(SrdEdition.Edition2014).SelectMany(b => b.Legendary?.Actions ?? []), a => Assert.False(a.OncePerRound));
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Spells_EveryOne_IsASpellWithALevelAndAUsage(string edition)
    {
        Assert.All(Blocks(edition).SelectMany(b => b.Spells), s =>
        {
            Assert.True(s.IsSpell);
            Assert.True(s.Magical);
            Assert.NotNull(s.SpellLevel);
            Assert.Contains(s.Kind, new[] { StatBlockValues.ActionKinds.Attack, StatBlockValues.ActionKinds.Save, StatBlockValues.ActionKinds.AutoHit, StatBlockValues.ActionKinds.Heal });
        });
        Assert.All(Blocks(edition).SelectMany(b => b.Spells.Where(s => s.Usage.Kind == StatBlockValues.UsageKinds.Pool).Select(s => (b, s))), x =>
            Assert.True(x.b.SpellSlots.ContainsKey(int.Parse(x.s.Usage.Pool!["slot:".Length..], System.Globalization.CultureInfo.InvariantCulture)), x.b.Ref));
    }

    [Fact]
    public void Normalize_NotAMonster_Throws()
    {
        var spell = CorrectedSrd.Shipped.Get(SrdEdition.Edition2024, SrdKinds.Spell, "fireball")!;

        Assert.Throws<ArgumentException>(() => CorrectedSrd.Shipped.Normalizer.Normalize(spell, CorrectedSrd.Shipped));
    }

    [Fact]
    public void Normalize_UnreadableMonsterJson_ThrowsInvalidData()
    {
        var broken = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "broken", Name = "Broken", Json = """{"index": "broken"}""" };

        Assert.Throws<InvalidDataException>(() => CorrectedSrd.Shipped.Normalizer.Normalize(broken, CorrectedSrd.Shipped));
    }
}

/// <summary>
/// Invariant: the documents <see cref="CorrectedSrd"/> builds for the tests are the ones srd.db serves. Every monster of
/// both editions, read from a freshly built index, normalizes to exactly the stat block the tests' document gives, so
/// every pinned number above is pinned against production.
/// </summary>
public sealed class CorrectedSrdTests : IClassFixture<SrdIndexFixture>
{
    private readonly SrdIndexFixture _fixture;

    public CorrectedSrdTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void IndexDocuments_EveryMonster_NormalizesAsTheTestDocumentDoes(string edition)
    {
        var shipped = CorrectedSrd.Shipped;
        var mismatches = new List<string>();
        foreach (var test in shipped.Monsters(edition))
        {
            var indexed = _fixture.Index.Get(edition, SrdKinds.Monster, test.Slug);
            Assert.NotNull(indexed);
            var fromIndex = StatBlockJson.Canonical(shipped.Normalizer.Normalize(indexed!, _fixture.Index));
            var fromTest = StatBlockJson.Canonical(shipped.Normalizer.Normalize(test, shipped));
            if (fromIndex != fromTest)
            {
                mismatches.Add(test.Ref.ToString());
            }
        }

        Assert.Empty(mismatches);
    }
}
