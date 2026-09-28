using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// Invariant: every trait name either edition uses has exactly the kind(s) listed here. A new trait (a re-vendor, a
/// correction) or a changed classification is a visible, reviewed diff of this table, never a silent change in what
/// the simulator does. Spellcasting traits are not in it: their spells are read into <see cref="StatBlock.Spells"/>
/// (<see cref="SpellCatalogueTests"/>).
/// </summary>
public sealed class TraitCatalogueTests
{
    /// <summary>Catalogue name (parenthetical usage stripped) → kind(s), "+"-joined when one trait yields two entries or an edition differs.</summary>
    private static readonly SortedDictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["Abduct"] = "no_combat_effect",
        ["Aberrant Ground"] = "not_modelled",
        ["Acid Absorption"] = "not_modelled",
        ["Adhesive"] = "not_modelled",
        ["Aggressive"] = "not_modelled",
        ["Agile"] = "no_combat_effect",
        ["Air Form"] = "no_combat_effect",
        ["Ambusher"] = "not_modelled",
        ["Amorphous"] = "no_combat_effect",
        ["Amphibious"] = "no_combat_effect",
        ["Angelic Weapons"] = "magic_weapons",
        ["Antimagic Susceptibility"] = "no_combat_effect",
        ["Assassinate"] = "not_modelled",
        ["Aura of Authority"] = "not_modelled",
        ["Aversion of Fire"] = "not_modelled",
        ["Aversion to Fire"] = "not_modelled",
        ["Barbed Hide"] = "not_modelled",
        ["Beast of Burden"] = "no_combat_effect",
        ["Berserk"] = "not_modelled",
        ["Blind Senses"] = "no_combat_effect",
        ["Blood Frenzy"] = "blood_frenzy",
        ["Bloodied Frenzy"] = "advantage_while_bloodied",
        ["Bloodied Fury"] = "advantage_while_bloodied",
        ["Blurred Form"] = "not_modelled",
        ["Bound"] = "no_combat_effect",
        ["Brave"] = "not_modelled",
        ["Brute"] = "no_combat_effect",
        ["Celestial Restoration"] = "no_combat_effect",
        ["Charge"] = "not_modelled",
        ["Confer Fire Resistance"] = "no_combat_effect",
        ["Consume Life"] = "not_modelled",
        ["Corrode Metal"] = "not_modelled",
        ["Corrosive Form"] = "not_modelled+retaliation_damage",
        ["Coven Magic"] = "not_modelled",
        ["Cunning Action"] = "not_modelled",
        ["Damage Transfer"] = "not_modelled",
        ["Dark Devotion"] = "not_modelled",
        ["Death Burst"] = "death_burst",
        ["Death Throes"] = "death_burst",
        ["Demonic Restoration"] = "no_combat_effect",
        ["Devil's Sight"] = "no_combat_effect",
        ["Diabolical Restoration"] = "no_combat_effect",
        ["Divine Awareness"] = "no_combat_effect",
        ["Divine Eminence"] = "not_modelled",
        ["Draconic Origin"] = "no_combat_effect",
        ["Duergar Resilience"] = "not_modelled",
        ["Earth Glide"] = "no_combat_effect",
        ["Echolocation"] = "no_combat_effect",
        ["Eldritch Restoration"] = "no_combat_effect",
        ["Elemental Demise"] = "no_combat_effect",
        ["Elemental Restoration"] = "no_combat_effect",
        ["Ephemeral"] = "no_combat_effect",
        ["Ethereal Jaunt"] = "no_combat_effect",
        ["Ethereal Sight"] = "no_combat_effect",
        ["Evasion"] = "evasion",
        ["Exalted Restoration"] = "no_combat_effect",
        ["False Appearance"] = "no_combat_effect",
        ["Faultless Tracker"] = "no_combat_effect",
        ["Fear Aura"] = "aura_damage",
        ["Fey Ancestry"] = "not_modelled",
        ["Fiendish Restoration"] = "no_combat_effect",
        ["Fire Absorption"] = "not_modelled",
        ["Fire Aura"] = "aura_damage+retaliation_damage",
        ["Fire Form"] = "retaliation_damage",
        ["Flyby"] = "no_combat_effect",
        ["Forbiddance"] = "no_combat_effect",
        ["Freedom of Movement"] = "not_modelled",
        ["Freeze"] = "no_combat_effect",
        ["Gibbering"] = "not_modelled",
        ["Gnome Cunning"] = "not_modelled",
        ["Grappler"] = "not_modelled",
        ["Grasping Tendrils"] = "not_modelled",
        ["Greater Magic Resistance"] = "magic_resistance",
        ["Heat Aura"] = "aura_damage",
        ["Heated Body"] = "retaliation_damage",
        ["Heated Weapons"] = "no_combat_effect",
        ["Hellish Rejuvenation"] = "no_combat_effect",
        ["Hellish Restoration"] = "no_combat_effect",
        ["Hellish Weapons"] = "magic_weapons",
        ["Hold Breath"] = "no_combat_effect",
        ["Horrific Appearance"] = "not_modelled",
        ["Ice Walk"] = "no_combat_effect",
        ["Ignited Illumination"] = "no_combat_effect",
        ["Illumination"] = "no_combat_effect",
        ["Immutable Form"] = "no_combat_effect",
        ["Incorporeal Movement"] = "no_combat_effect",
        ["Incubus Form"] = "no_combat_effect",
        ["Inscrutable"] = "no_combat_effect",
        ["Invisibility"] = "not_modelled",
        ["Iron Scent"] = "no_combat_effect",
        ["Jumper"] = "no_combat_effect",
        ["Keen Hearing"] = "no_combat_effect",
        ["Keen Hearing and Sight"] = "no_combat_effect",
        ["Keen Hearing and Smell"] = "no_combat_effect",
        ["Keen Senses"] = "no_combat_effect",
        ["Keen Sight"] = "no_combat_effect",
        ["Keen Sight and Smell"] = "no_combat_effect",
        ["Keen Smell"] = "no_combat_effect",
        ["Labyrinthine Recall"] = "no_combat_effect",
        ["Legendary Resistance"] = "legendary_resistance",
        ["Light Sensitivity"] = "no_combat_effect",
        ["Lightning Absorption"] = "not_modelled",
        ["Limited Amphibiousness"] = "no_combat_effect",
        ["Limited Magic Immunity"] = "magic_resistance",
        ["Limited Telepathy"] = "no_combat_effect",
        ["Loathsome Limbs"] = "not_modelled",
        ["Magic Resistance"] = "magic_resistance",
        ["Magic Rope"] = "no_combat_effect",
        ["Magic Weapons"] = "magic_weapons",
        ["Martial Advantage"] = "martial_advantage",
        ["Mimicry"] = "no_combat_effect",
        ["Misty Escape"] = "not_modelled",
        ["Mucous Cloud"] = "no_combat_effect",
        ["Mucus Cloud"] = "no_combat_effect",
        ["Multiple Heads"] = "not_modelled",
        ["Night Hag Items"] = "no_combat_effect",
        ["Nimble Escape"] = "not_modelled",
        ["Ooze Cube"] = "no_combat_effect",
        ["Pack Tactics"] = "pack_tactics",
        ["Petrifying Gaze"] = "not_modelled",
        ["Pounce"] = "not_modelled",
        ["Probing Telepathy"] = "no_combat_effect",
        ["Rampage"] = "not_modelled",
        ["Reactive"] = "not_modelled",
        ["Reactive Heads"] = "no_combat_effect",
        ["Reckless"] = "reckless",
        ["Reflective Carapace"] = "not_modelled",
        ["Regeneration"] = "regeneration",
        ["Rejuvenation"] = "no_combat_effect",
        ["Relentless"] = "relentless",
        ["Running Leap"] = "no_combat_effect",
        ["Running Water"] = "no_combat_effect",
        ["Rust Metal"] = "not_modelled",
        ["Sense Magic"] = "no_combat_effect",
        ["Shadow Stealth"] = "no_combat_effect",
        ["Shapechanger"] = "no_combat_effect",
        ["Shark Telepathy"] = "no_combat_effect",
        ["Shielded Mind"] = "no_combat_effect",
        ["Siege Monster"] = "no_combat_effect",
        ["Sneak Attack"] = "sneak_attack",
        ["Snow Camouflage"] = "no_combat_effect",
        ["Soul Bag"] = "no_combat_effect",
        ["Speak with Beasts and Plants"] = "no_combat_effect",
        ["Spell Storing"] = "not_modelled",
        ["Spider Climb"] = "no_combat_effect",
        ["Spirit Jar"] = "no_combat_effect",
        ["Stake to the Heart"] = "no_combat_effect",
        ["Standing Leap"] = "no_combat_effect",
        ["Steadfast"] = "not_modelled",
        ["Stench"] = "aura_damage",
        ["Stone Camouflage"] = "no_combat_effect",
        ["Succubus Form"] = "no_combat_effect",
        ["Sunlight"] = "no_combat_effect",
        ["Sunlight Sensitivity"] = "no_combat_effect",
        ["Sunlight Weakness"] = "no_combat_effect",
        ["Sure-Footed"] = "not_modelled",
        ["Surprise Attack"] = "not_modelled",
        ["Swarm"] = "no_combat_effect",
        ["Tail Spike Regrowth"] = "no_combat_effect",
        ["Telepathic Bond"] = "no_combat_effect",
        ["Training"] = "no_combat_effect",
        ["Trampling Charge"] = "not_modelled",
        ["Transparent"] = "no_combat_effect",
        ["Treasure Sense"] = "no_combat_effect",
        ["Tree Stride"] = "no_combat_effect",
        ["Troll Spawn"] = "no_combat_effect",
        ["Tunneler"] = "no_combat_effect",
        ["Turn Defiance"] = "no_combat_effect",
        ["Turn Resistance"] = "no_combat_effect",
        ["Two Heads"] = "not_modelled",
        ["Two-Headed"] = "not_modelled",
        ["Undead Fortitude"] = "undead_fortitude",
        ["Undead Restoration"] = "no_combat_effect",
        ["Underwater Camouflage"] = "no_combat_effect",
        ["Vampire Weakness"] = "no_combat_effect",
        ["Vampire Weaknesses"] = "no_combat_effect",
        ["Vampiric Connection"] = "no_combat_effect",
        ["Variable Illumination"] = "no_combat_effect",
        ["Vile Appearance"] = "not_modelled",
        ["Wakeful"] = "no_combat_effect",
        ["Water Breathing"] = "no_combat_effect",
        ["Water Form"] = "no_combat_effect",
        ["Water Susceptibility"] = "no_combat_effect",
        ["Web Sense"] = "no_combat_effect",
        ["Web Walker"] = "no_combat_effect",
        ["Wishes"] = "no_combat_effect",
    };

    [Fact]
    public void EveryTraitName_BothEditions_HasItsCataloguedKinds()
    {
        var actual = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var block in SrdEdition.All.SelectMany(CorrectedSrd.Shipped.StatBlocks))
        {
            foreach (var trait in block.Traits)
            {
                var name = ProseText.StripParentheticals(trait.Name);
                if (!actual.TryGetValue(name, out var kinds))
                {
                    actual[name] = kinds = new SortedSet<string>(StringComparer.Ordinal);
                }

                kinds.Add(trait.Kind);
            }
        }

        var diff = actual.Keys.Union(Expected.Keys)
            .Where(n => !Expected.TryGetValue(n, out var e) || !actual.TryGetValue(n, out var a) || e != string.Join("+", a))
            .Select(n => $"[\"{n}\"] expected {Expected.GetValueOrDefault(n) ?? "(absent)"}, found {(actual.TryGetValue(n, out var k) ? string.Join("+", k) : "(absent)")}")
            .ToList();
        Assert.True(diff.Count == 0, string.Join("\n", diff));
    }

    [Fact]
    public void Catalogue_Lists_DoNotOverlapAndNameOnlyTraitsTheDataHas()
    {
        var none = TraitCatalogue.NoCombatEffect.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notModelled = TraitCatalogue.NotModelled.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var implemented = TraitCatalogue.Implemented.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(none.Intersect(notModelled, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(none.Intersect(implemented, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(notModelled.Intersect(implemented, StringComparer.OrdinalIgnoreCase));
        var dead = none.Concat(notModelled).Concat(implemented).Where(n => !Expected.ContainsKey(n)).ToList();
        Assert.True(dead.Count == 0, "Catalogue entries no trait uses: " + string.Join(", ", dead));
    }

    [Fact]
    public void NoCombatEffect_EveryTrait_IsNamedInANoteOfItsMonster()
    {
        foreach (var block in SrdEdition.All.SelectMany(CorrectedSrd.Shipped.StatBlocks))
        {
            Assert.All(
                block.Traits.Where(t => t.Kind == StatBlockValues.TraitKinds.NoCombatEffect),
                t => Assert.Contains(block.Notes, n => n.StartsWith($"Trait {ProseText.StripParentheticals(t.Name)}:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Classify_UnknownTrait_IsNotModelledWithAWarning()
    {
        var log = new NormalizationLog();
        var trait = new RecordAction { Name = "Glimmering Hide", Desc = "The creature glimmers.", Damage = [], DamageChoices = [] };

        var result = Assert.Single(TraitCatalogue.Classify(trait, log));

        Assert.Equal(StatBlockValues.TraitKinds.NotModelled, result.Kind);
        Assert.Equal(StatBlockValues.WarningCodes.NotModelled, Assert.Single(log.Warnings).Code);
    }
}
