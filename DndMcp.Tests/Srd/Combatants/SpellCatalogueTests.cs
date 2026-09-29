using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Invariant: every spell any monster's spellcasting names, in both editions, resolves to a combat profile (attack,
/// save, auto_hit, heal, parry) or is explicitly classified as having no combat effect or as not modelled, and the
/// classification of each is pinned here, so a new spell in the data is a reviewed diff.
/// </summary>
public sealed class SpellCatalogueTests
{
    /// <summary>edition/slug → profile kind.</summary>
    private static readonly SortedDictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["2014/acid-arrow"] = "attack",
        ["2014/animal-messenger"] = "no_combat_effect",
        ["2014/animate-dead"] = "not_modelled",
        ["2014/banishment"] = "not_modelled",
        ["2014/barkskin"] = "no_combat_effect",
        ["2014/bestow-curse"] = "not_modelled",
        ["2014/blade-barrier"] = "save",
        ["2014/bless"] = "not_modelled",
        ["2014/blight"] = "save",
        ["2014/blindness-deafness"] = "save",
        ["2014/blur"] = "not_modelled",
        ["2014/calm-emotions"] = "not_modelled",
        ["2014/charm-person"] = "not_modelled",
        ["2014/clairvoyance"] = "no_combat_effect",
        ["2014/cloudkill"] = "save",
        ["2014/command"] = "not_modelled",
        ["2014/commune"] = "no_combat_effect",
        ["2014/cone-of-cold"] = "save",
        ["2014/confusion"] = "not_modelled",
        ["2014/conjure-elemental"] = "not_modelled",
        ["2014/contagion"] = "not_modelled",
        ["2014/control-weather"] = "no_combat_effect",
        ["2014/counterspell"] = "not_modelled",
        ["2014/create-food-and-water"] = "no_combat_effect",
        ["2014/creation"] = "no_combat_effect",
        ["2014/cure-wounds"] = "heal",
        ["2014/dancing-lights"] = "no_combat_effect",
        ["2014/darkness"] = "no_combat_effect",
        ["2014/detect-evil-and-good"] = "no_combat_effect",
        ["2014/detect-magic"] = "no_combat_effect",
        ["2014/detect-thoughts"] = "no_combat_effect",
        ["2014/dimension-door"] = "no_combat_effect",
        ["2014/disguise-self"] = "no_combat_effect",
        ["2014/disintegrate"] = "save",
        ["2014/dispel-evil-and-good"] = "not_modelled",
        ["2014/dispel-magic"] = "not_modelled",
        ["2014/divination"] = "no_combat_effect",
        ["2014/dominate-monster"] = "not_modelled",
        ["2014/dominate-person"] = "not_modelled",
        ["2014/dream"] = "no_combat_effect",
        ["2014/druidcraft"] = "no_combat_effect",
        ["2014/enlarge-reduce"] = "not_modelled",
        ["2014/entangle"] = "save",
        ["2014/faerie-fire"] = "not_modelled",
        ["2014/feather-fall"] = "no_combat_effect",
        ["2014/finger-of-death"] = "save",
        ["2014/fire-bolt"] = "attack",
        ["2014/fire-shield"] = "not_modelled",
        ["2014/fireball"] = "save",
        ["2014/flame-strike"] = "save",
        ["2014/fly"] = "not_modelled",
        ["2014/fog-cloud"] = "no_combat_effect",
        ["2014/freedom-of-movement"] = "not_modelled",
        ["2014/gaseous-form"] = "no_combat_effect",
        ["2014/geas"] = "no_combat_effect",
        ["2014/globe-of-invulnerability"] = "not_modelled",
        ["2014/goodberry"] = "no_combat_effect",
        ["2014/greater-invisibility"] = "not_modelled",
        ["2014/greater-restoration"] = "not_modelled",
        ["2014/guardian-of-faith"] = "not_modelled",
        ["2014/guiding-bolt"] = "attack",
        ["2014/harm"] = "save",
        ["2014/heat-metal"] = "not_modelled",
        ["2014/heroes-feast"] = "no_combat_effect",
        ["2014/hold-monster"] = "save",
        ["2014/hold-person"] = "save",
        ["2014/ice-storm"] = "save",
        ["2014/identify"] = "no_combat_effect",
        ["2014/inflict-wounds"] = "attack",
        ["2014/insect-plague"] = "save",
        ["2014/invisibility"] = "not_modelled",
        ["2014/legend-lore"] = "no_combat_effect",
        ["2014/lesser-restoration"] = "not_modelled",
        ["2014/levitate"] = "not_modelled",
        ["2014/light"] = "no_combat_effect",
        ["2014/lightning-bolt"] = "save",
        ["2014/locate-object"] = "no_combat_effect",
        ["2014/longstrider"] = "no_combat_effect",
        ["2014/mage-armor"] = "no_combat_effect",
        ["2014/mage-hand"] = "no_combat_effect",
        ["2014/magic-missile"] = "auto_hit",
        ["2014/major-image"] = "no_combat_effect",
        ["2014/mending"] = "no_combat_effect",
        ["2014/mind-blank"] = "not_modelled",
        ["2014/minor-illusion"] = "no_combat_effect",
        ["2014/mirror-image"] = "not_modelled",
        ["2014/misty-step"] = "no_combat_effect",
        ["2014/nondetection"] = "no_combat_effect",
        ["2014/pass-without-trace"] = "no_combat_effect",
        ["2014/plane-shift"] = "not_modelled",
        ["2014/power-word-kill"] = "auto_hit",
        ["2014/power-word-stun"] = "not_modelled",
        ["2014/prestidigitation"] = "no_combat_effect",
        ["2014/produce-flame"] = "attack",
        ["2014/protection-from-poison"] = "not_modelled",
        ["2014/raise-dead"] = "no_combat_effect",
        ["2014/ray-of-enfeeblement"] = "not_modelled",
        ["2014/ray-of-frost"] = "attack",
        ["2014/remove-curse"] = "no_combat_effect",
        ["2014/resurrection"] = "no_combat_effect",
        ["2014/sacred-flame"] = "save",
        ["2014/sanctuary"] = "not_modelled",
        ["2014/scrying"] = "no_combat_effect",
        ["2014/shield"] = "parry",
        ["2014/shield-of-faith"] = "not_modelled",
        ["2014/shillelagh"] = "no_combat_effect",
        ["2014/shocking-grasp"] = "attack",
        ["2014/silence"] = "not_modelled",
        ["2014/sleep"] = "not_modelled",
        ["2014/spare-the-dying"] = "no_combat_effect",
        ["2014/speak-with-animals"] = "no_combat_effect",
        ["2014/spirit-guardians"] = "save",
        ["2014/spiritual-weapon"] = "attack",
        ["2014/stoneskin"] = "not_modelled",
        ["2014/suggestion"] = "not_modelled",
        ["2014/telekinesis"] = "not_modelled",
        ["2014/teleport"] = "no_combat_effect",
        ["2014/thaumaturgy"] = "no_combat_effect",
        ["2014/thunderwave"] = "save",
        ["2014/time-stop"] = "not_modelled",
        ["2014/tongues"] = "no_combat_effect",
        ["2014/true-seeing"] = "not_modelled",
        ["2014/vicious-mockery"] = "save",
        ["2014/wall-of-fire"] = "save",
        ["2014/wall-of-force"] = "not_modelled",
        ["2014/water-breathing"] = "no_combat_effect",
        ["2014/wind-walk"] = "no_combat_effect",
        ["2014/zone-of-truth"] = "no_combat_effect",
        ["2024/acid-arrow"] = "attack",
        ["2024/animal-friendship"] = "no_combat_effect",
        ["2024/animal-messenger"] = "no_combat_effect",
        ["2024/animate-dead"] = "not_modelled",
        ["2024/augury"] = "no_combat_effect",
        ["2024/bless"] = "not_modelled",
        ["2024/calm-emotions"] = "not_modelled",
        ["2024/chain-lightning"] = "save",
        ["2024/charm-monster"] = "not_modelled",
        ["2024/charm-person"] = "not_modelled",
        ["2024/clairvoyance"] = "no_combat_effect",
        ["2024/command"] = "not_modelled",
        ["2024/commune"] = "no_combat_effect",
        ["2024/cone-of-cold"] = "save",
        ["2024/confusion"] = "not_modelled",
        ["2024/control-water"] = "not_modelled",
        ["2024/control-weather"] = "no_combat_effect",
        ["2024/counterspell"] = "not_modelled",
        ["2024/create-food-and-water"] = "no_combat_effect",
        ["2024/create-undead"] = "not_modelled",
        ["2024/creation"] = "no_combat_effect",
        ["2024/cure-wounds"] = "heal",
        ["2024/dancing-lights"] = "no_combat_effect",
        ["2024/darkness"] = "no_combat_effect",
        ["2024/detect-evil-and-good"] = "no_combat_effect",
        ["2024/detect-magic"] = "no_combat_effect",
        ["2024/detect-thoughts"] = "no_combat_effect",
        ["2024/dimension-door"] = "no_combat_effect",
        ["2024/disguise-self"] = "no_combat_effect",
        ["2024/dispel-evil-and-good"] = "not_modelled",
        ["2024/dispel-magic"] = "not_modelled",
        ["2024/dominate-person"] = "not_modelled",
        ["2024/dream"] = "no_combat_effect",
        ["2024/druidcraft"] = "no_combat_effect",
        ["2024/elementalism"] = "no_combat_effect",
        ["2024/entangle"] = "save",
        ["2024/etherealness"] = "no_combat_effect",
        ["2024/faerie-fire"] = "not_modelled",
        ["2024/fear"] = "not_modelled",
        ["2024/find-familiar"] = "no_combat_effect",
        ["2024/finger-of-death"] = "save",
        ["2024/fireball"] = "save",
        ["2024/flame-strike"] = "save",
        ["2024/fly"] = "not_modelled",
        ["2024/fog-cloud"] = "no_combat_effect",
        ["2024/gaseous-form"] = "no_combat_effect",
        ["2024/geas"] = "no_combat_effect",
        ["2024/greater-restoration"] = "not_modelled",
        ["2024/guiding-bolt"] = "attack",
        ["2024/harm"] = "save",
        ["2024/healing-word"] = "heal",
        ["2024/heroes-feast"] = "no_combat_effect",
        ["2024/hold-monster"] = "save",
        ["2024/hold-person"] = "save",
        ["2024/hypnotic-pattern"] = "not_modelled",
        ["2024/ice-knife"] = "attack",
        ["2024/ice-storm"] = "save",
        ["2024/identify"] = "no_combat_effect",
        ["2024/insect-plague"] = "save",
        ["2024/invisibility"] = "not_modelled",
        ["2024/legend-lore"] = "no_combat_effect",
        ["2024/lesser-restoration"] = "not_modelled",
        ["2024/light"] = "no_combat_effect",
        ["2024/lightning-bolt"] = "save",
        ["2024/locate-object"] = "no_combat_effect",
        ["2024/longstrider"] = "no_combat_effect",
        ["2024/mage-armor"] = "no_combat_effect",
        ["2024/mage-hand"] = "no_combat_effect",
        ["2024/magic-missile"] = "auto_hit",
        ["2024/major-image"] = "no_combat_effect",
        ["2024/mind-blank"] = "not_modelled",
        ["2024/mind-spike"] = "save",
        ["2024/minor-illusion"] = "no_combat_effect",
        ["2024/mirror-image"] = "not_modelled",
        ["2024/misty-step"] = "no_combat_effect",
        ["2024/modify-memory"] = "no_combat_effect",
        ["2024/moonbeam"] = "save",
        ["2024/pass-without-trace"] = "no_combat_effect",
        ["2024/phantasmal-killer"] = "save",
        ["2024/plane-shift"] = "not_modelled",
        ["2024/power-word-kill"] = "auto_hit",
        ["2024/power-word-stun"] = "not_modelled",
        ["2024/prestidigitation"] = "no_combat_effect",
        ["2024/project-image"] = "no_combat_effect",
        ["2024/raise-dead"] = "no_combat_effect",
        ["2024/ray-of-sickness"] = "attack",
        ["2024/remove-curse"] = "no_combat_effect",
        ["2024/resurrection"] = "no_combat_effect",
        ["2024/sanctuary"] = "not_modelled",
        ["2024/scorching-ray"] = "attack",
        ["2024/scrying"] = "no_combat_effect",
        ["2024/sending"] = "no_combat_effect",
        ["2024/shapechange"] = "not_modelled",
        ["2024/shatter"] = "save",
        ["2024/shield"] = "parry",
        ["2024/sleep"] = "not_modelled",
        ["2024/slow"] = "not_modelled",
        ["2024/speak-with-animals"] = "no_combat_effect",
        ["2024/speak-with-dead"] = "no_combat_effect",
        ["2024/spirit-guardians"] = "save",
        ["2024/spiritual-weapon"] = "attack",
        ["2024/telekinesis"] = "not_modelled",
        ["2024/teleport"] = "no_combat_effect",
        ["2024/thaumaturgy"] = "no_combat_effect",
        ["2024/thunderwave"] = "save",
        ["2024/tongues"] = "no_combat_effect",
        ["2024/true-seeing"] = "not_modelled",
        ["2024/unseen-servant"] = "no_combat_effect",
        ["2024/vitriolic-sphere"] = "save",
        ["2024/wall-of-fire"] = "save",
        ["2024/wall-of-ice"] = "save",
        ["2024/water-breathing"] = "no_combat_effect",
        ["2024/web"] = "save",
        ["2024/wind-walk"] = "no_combat_effect",
        ["2024/word-of-recall"] = "no_combat_effect",
        ["2024/zone-of-truth"] = "no_combat_effect",
    };

    private static IEnumerable<(string Edition, string Slug)> Referenced()
    {
        foreach (var edition in SrdEdition.All)
        {
            foreach (var doc in CorrectedSrd.Shipped.Monsters(edition))
            {
                var root = doc.Root;
                foreach (var list in new[] { "special_abilities", "actions", "bonus_actions", "reactions", "legendary_actions" })
                {
                    if (!root.TryGetProperty(list, out var entries))
                    {
                        continue;
                    }

                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("spellcasting", out var spellcasting))
                        {
                            continue;
                        }

                        foreach (var spell in spellcasting.GetProperty("spells").EnumerateArray())
                        {
                            yield return (edition, spell.TryGetProperty("index", out var index)
                                ? index.GetString()!
                                : spell.GetProperty("url").GetString()!.TrimEnd('/').Split('/')[^1]);
                        }
                    }
                }
            }
        }
    }

    private static SpellProfile Profile(string edition, string slug) =>
        CorrectedSrd.Shipped.Normalizer.Profile(CorrectedSrd.Shipped.Get(edition, SrdKinds.Spell, slug) ??
                                                throw new Xunit.Sdk.XunitException($"No {edition} spell {slug}."));

    [Fact]
    public void EveryReferencedSpell_BothEditions_HasItsCataloguedKind()
    {
        var actual = Referenced().Distinct().ToDictionary(s => $"{s.Edition}/{s.Slug}", s => Profile(s.Edition, s.Slug).Kind);

        var diff = actual.Keys.Union(Expected.Keys)
            .Where(k => Expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Select(k => $"[\"{k}\"] expected {Expected.GetValueOrDefault(k) ?? "(absent)"}, found {actual.GetValueOrDefault(k) ?? "(absent)"}")
            .ToList();
        Assert.True(diff.Count == 0, string.Join("\n", diff));
    }

    [Fact]
    public void EveryReferencedSpell_IsClassifiedNotLeftOver()
    {
        var unclassified = Referenced().Distinct()
            .Select(s => Profile(s.Edition, s.Slug))
            .Where(p => !p.IsCombat && (p.Reason is null || p.Reason.StartsWith("not classified", StringComparison.Ordinal)))
            .Select(p => p.Ref)
            .ToList();

        Assert.Empty(unclassified);
    }

    /// <summary>The common PC combat spells have a combat profile in both editions, for party archetypes and builds later.</summary>
    [Theory]
    [InlineData("fire-bolt", "attack")]
    [InlineData("sacred-flame", "save")]
    [InlineData("eldritch-blast", "attack")]
    [InlineData("magic-missile", "auto_hit")]
    [InlineData("scorching-ray", "attack")]
    [InlineData("fireball", "save")]
    [InlineData("lightning-bolt", "save")]
    [InlineData("cone-of-cold", "save")]
    [InlineData("guiding-bolt", "attack")]
    [InlineData("hold-person", "save")]
    [InlineData("cure-wounds", "heal")]
    [InlineData("healing-word", "heal")]
    [InlineData("spiritual-weapon", "attack")]
    public void PcCombatSpells_BothEditions_HaveACombatProfile(string slug, string kind)
    {
        Assert.Equal(kind, Profile("2014", slug).Kind);
        Assert.Equal(kind, Profile("2024", slug).Kind);
    }

    [Fact]
    public void InflictWounds_IsAnAttackIn2014AndASaveIn2024()
    {
        Assert.Equal(("attack", "3d10 necrotic"), (Profile("2014", "inflict-wounds").Kind, $"{Profile("2014", "inflict-wounds").Damage[0].Dice} {Profile("2014", "inflict-wounds").Damage[0].DamageType}"));
        Assert.Equal(("save", "con", "half"), (Profile("2024", "inflict-wounds").Kind, Profile("2024", "inflict-wounds").SaveAbility, Profile("2024", "inflict-wounds").OnSuccess));
    }

    [Fact]
    public void Catalogues_DoNotOverlapAndNameRealSpells()
    {
        Assert.Empty(SpellNormalizer.NoCombatEffect.Keys.Intersect(SpellNormalizer.NotModelled.Keys));
        var missing = SpellNormalizer.NoCombatEffect.Keys.Concat(SpellNormalizer.NotModelled.Keys)
            .Where(slug => SrdEdition.All.All(e => CorrectedSrd.Shipped.Get(e, SrdKinds.Spell, slug) is null))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Normalize_NotASpell_Throws()
    {
        Assert.Throws<ArgumentException>(() => SpellNormalizer.Normalize(CorrectedSrd.Shipped.Monster("2024", "goblin-warrior"), SpellOverlay.Empty));
    }

    [Fact]
    public void Cast_2024Cantrip_ScalesItsDiceByCasterLevel()
    {
        var profile = Profile("2024", "fire-bolt");

        var at4 = SpellNormalizer.Cast(profile, "Fire Bolt", 0, 4, 13, 5, 3, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);
        var at11 = SpellNormalizer.Cast(profile, "Fire Bolt", 0, 11, 13, 5, 3, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);

        Assert.Equal(("1d10", "3d10"), (at4.Damage[0].Dice.Text, at11.Damage[0].Dice.Text));
    }

    [Theory]
    [InlineData(4, "1d10")]
    [InlineData(5, "2d10")]
    [InlineData(16, "3d10")]
    [InlineData(17, "4d10")]
    public void Cast_Cantrip_StepsUpAtFiveElevenSeventeen(int casterLevel, string dice) =>
        Assert.Equal(dice, SpellNormalizer.Cast(Profile("2024", "fire-bolt"), "Fire Bolt", 0, casterLevel, 13, 5, 3, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill).Damage[0].Dice.Text);

    [Fact]
    public void Cast_EldritchBlast_MakesOneAttackPerBeam()
    {
        var at17 = SpellNormalizer.Cast(Profile("2014", "eldritch-blast"), "Eldritch Blast", 0, 17, 15, 7, 4, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);

        Assert.Equal((4, "1d10"), (at17.AttackRolls, at17.Damage[0].Dice.Text));
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Cast_SpiritualWeapon_AddsTheCastersModifierToItsDamage(string edition)
    {
        var weapon = SpellNormalizer.Cast(Profile(edition, "spiritual-weapon"), "Spiritual Weapon", 2, 1, 15, 7, 4, StatBlockValues.ActionSlots.BonusAction, UsageSpec.AtWill);
        Assert.Equal("1d8+4", weapon.Damage[0].Dice.Text);
    }

    [Fact]
    public void Cast_UpcastSpells_AddTheirDiceAndTargets()
    {
        var fireball = SpellNormalizer.Cast(Profile("2024", "fireball"), "Fireball", 5, 1, 15, 7, 4, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);
        var holdPerson = SpellNormalizer.Cast(Profile("2024", "hold-person"), "Hold Person", 4, 1, 15, 7, 4, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);
        var cure = SpellNormalizer.Cast(Profile("2024", "cure-wounds"), "Cure Wounds", 2, 1, 15, 7, 4, StatBlockValues.ActionSlots.Action, UsageSpec.AtWill);

        Assert.Equal("10d6", fireball.Damage[0].Dice.Text);
        Assert.Equal((3, StatBlockValues.Durations.SaveEnds, 15), (holdPerson.Targets, holdPerson.Condition!.Duration, holdPerson.Condition.SaveEnds!.Dc));
        Assert.Equal("4d8+4", cure.Healing!.Text);
    }
}
