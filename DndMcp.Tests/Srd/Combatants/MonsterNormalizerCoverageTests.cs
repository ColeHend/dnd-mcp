using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
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
    [InlineData("2014", StatBlockValues.WarningCodes.NotModelled, 212)]
    [InlineData("2014", StatBlockValues.WarningCodes.Approximated, 95)]
    [InlineData("2014", StatBlockValues.WarningCodes.UnresolvedReference, 3)]
    [InlineData("2014", StatBlockValues.WarningCodes.DataConflict, 0)]
    [InlineData("2014", StatBlockValues.WarningCodes.Unparsed, 0)]
    [InlineData("2024", StatBlockValues.WarningCodes.NotModelled, 186)]
    [InlineData("2024", StatBlockValues.WarningCodes.Approximated, 113)]
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
