using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: a monster renders as the stat block its SRD prints, from either edition's data shape, with nothing the
/// data holds dropped and nothing it lacks invented.
///
/// <para>
/// The exact lines are hand-checked against the vendored records and the SRD's own stat blocks. They pin the parts a
/// formatter gets quietly wrong: usage stripped from names (a breath weapon shown without its recharge reads as usable
/// every turn), multi-entry 2014 armor class, the qualifier punctuation in damage resistances, 2024 spell lists that
/// live only in structured data, and gear that upstream stores as a plain string. The data-wide tests hold every
/// record to the same rules, so a formatter change that fixes one monster and breaks another fails here.
/// </para>
/// </summary>
public sealed partial class MonsterMarkdownTests
{
    private static VendoredSrdLookup Lookup => VendoredSrdLookup.Instance;

    [Theory]
    // 2014 adult red dragon: every header line.
    [InlineData("2014/monster/adult-red-dragon", "*Huge dragon, chaotic evil*")]
    [InlineData("2014/monster/adult-red-dragon", "**Armor Class** 19 (natural armor)")]
    [InlineData("2014/monster/adult-red-dragon", "**Hit Points** 256 (19d12 + 133)")]
    [InlineData("2014/monster/adult-red-dragon", "**Speed** 40 ft., climb 40 ft., fly 80 ft.")]
    [InlineData("2014/monster/adult-red-dragon", "| 27 (+8) | 10 (+0) | 25 (+7) | 16 (+3) | 13 (+1) | 21 (+5) |")]
    [InlineData("2014/monster/adult-red-dragon", "**Saving Throws** Dex +6, Con +13, Wis +7, Cha +11")]
    [InlineData("2014/monster/adult-red-dragon", "**Skills** Perception +13, Stealth +6")]
    [InlineData("2014/monster/adult-red-dragon", "**Damage Immunities** fire")]
    [InlineData("2014/monster/adult-red-dragon", "**Senses** blindsight 60 ft., darkvision 120 ft., passive Perception 23")]
    [InlineData("2014/monster/adult-red-dragon", "**Languages** Common, Draconic")]
    [InlineData("2014/monster/adult-red-dragon", "**Challenge** 17 (18,000 XP) · **Proficiency Bonus** +6")]
    [InlineData("2014/monster/adult-red-dragon", "***Legendary Resistance (3/Day).*** If the dragon fails a saving throw, it can choose to succeed instead.")]
    [InlineData("2014/monster/adult-red-dragon", "***Fire Breath (Recharge 5–6).*** The dragon exhales fire in a 60-foot cone. Each creature in that area must make a DC 21 Dexterity saving throw, taking 63 (18d6) fire damage on a failed save, or half as much damage on a successful one.")]
    // 2024 adult red dragon: Charisma 23, lair XP and lair uses, and the spell list that exists only as data.
    [InlineData("2024/monster/adult-red-dragon", "**Armor Class** 19")]
    [InlineData("2024/monster/adult-red-dragon", "**Hit Points** 256 (19d12 + 133)")]
    [InlineData("2024/monster/adult-red-dragon", "| 27 (+8) | 10 (+0) | 25 (+7) | 16 (+3) | 13 (+1) | 23 (+6) |")]
    [InlineData("2024/monster/adult-red-dragon", "**Saving Throws** Dex +6, Wis +7")]
    // The 2024 data stores darkvision first; both SRDs print senses alphabetically.
    [InlineData("2024/monster/adult-red-dragon", "**Senses** blindsight 60 ft., darkvision 120 ft., passive Perception 23")]
    [InlineData("2024/monster/adult-red-dragon", "**Challenge** 17 (18,000 XP; 20,000 XP in lair) · **Proficiency Bonus** +6")]
    [InlineData("2024/monster/adult-red-dragon", "***Legendary Resistance (3/Day, or 4/Day in Lair).*** If the dragon fails a saving throw, it can choose to succeed instead.")]
    [InlineData("2024/monster/adult-red-dragon", "***Fire Breath (Recharge 5–6).*** Dexterity Saving Throw: DC 21, each creature in a 60-foot Cone. Failure: 59 (17d6) Fire damage. Success: Half damage.")]
    [InlineData("2024/monster/adult-red-dragon", "- **At Will:** Command (level 2 version) (`2024/spell/command`), Detect Magic (`2024/spell/detect-magic`), Scorching Ray (`2024/spell/scorching-ray`)")]
    [InlineData("2024/monster/adult-red-dragon", "- **1/Day Each:** Fireball (`2024/spell/fireball`)")]
    [InlineData("2024/monster/adult-red-dragon", "***Pounce.*** The dragon moves up to half its Speed, and it makes one Rend attack.")]
    // Goblin (2014) and its 2024 counterpart: armor refs, fractional CR, true minus signs, gear.
    [InlineData("2014/monster/goblin", "*Small humanoid (goblinoid), neutral evil*")]
    [InlineData("2014/monster/goblin", "**Armor Class** 15 (Leather Armor (`2014/equipment/leather-armor`), Shield (`2014/equipment/shield`))")]
    [InlineData("2014/monster/goblin", "**Hit Points** 7 (2d6)")]
    [InlineData("2014/monster/goblin", "| 8 (−1) | 14 (+2) | 10 (+0) | 10 (+0) | 8 (−1) | 8 (−1) |")]
    [InlineData("2014/monster/goblin", "**Skills** Stealth +6")]
    [InlineData("2014/monster/goblin", "**Senses** darkvision 60 ft., passive Perception 9")]
    [InlineData("2014/monster/goblin", "**Challenge** 1/4 (50 XP) · **Proficiency Bonus** +2")]
    [InlineData("2024/monster/goblin-warrior", "*Small fey, chaotic neutral*")]
    [InlineData("2024/monster/goblin-warrior", "**Hit Points** 10 (3d6)")]
    [InlineData("2024/monster/goblin-warrior", "**Gear** Leather Armor (`2024/equipment/leather-armor`), Scimitar (`2024/equipment/scimitar`), Shield (`2024/equipment/shield`), Shortbow (`2024/equipment/shortbow`)")]
    // Gear is a plain string upstream: counted plurals resolve to the singular item, unknown names stay plain.
    [InlineData("2024/monster/ogre", "**Gear** Greatclub (`2024/equipment/greatclub`), Javelins (3) (`2024/equipment/javelin`)")]
    [InlineData("2024/monster/cultist-fanatic", "**Gear** Holy Symbol, Leather Armor (`2024/equipment/leather-armor`)")]
    // The seven 2014 monsters with more than one armor class entry, and free-text armor.
    [InlineData("2014/monster/archmage", "**Armor Class** 12, 15 with Mage Armor (`2014/spell/mage-armor`)")]
    [InlineData("2014/monster/druid", "**Armor Class** 11, 16 with Barkskin (`2014/spell/barkskin`)")]
    [InlineData("2014/monster/ankheg", "**Armor Class** 14 (natural armor), 11 while Prone (`2014/condition/prone`)")]
    // Upstream splits SRD 5.1's "17 (natural armor, shield)" into a natural entry and a shield-only armor entry; the
    // shield is standard kit, so the SRD's one AC is shown, not "15 (natural armor), 17 with Shield".
    [InlineData("2014/monster/azer", "**Armor Class** 17 (natural armor, Shield (`2014/equipment/shield`))")]
    [InlineData("2014/monster/lizardfolk", "**Armor Class** 15 (natural armor, Shield (`2014/equipment/shield`))")]
    [InlineData("2014/monster/frost-giant", "**Armor Class** 15 (patchwork armor)")]
    [InlineData("2024/monster/assassin", "**Armor Class** 16 (Studded Leather Armor (`2024/equipment/studded-leather-armor`))")]
    // Hit dice spaced the 2024 way in both editions, minus kept pasteable into dice_roll.
    [InlineData("2014/monster/bat", "**Hit Points** 1 (1d4 - 1)")]
    [InlineData("2024/monster/kobold-warrior", "**Hit Points** 7 (3d6 - 3)")]
    // Speed: walk first; hover belongs to fly, even with no walking speed at all.
    [InlineData("2014/monster/swarm-of-bats", "**Speed** 0 ft., fly 30 ft.")]
    [InlineData("2014/monster/air-elemental", "**Speed** fly 90 ft. (hover)")]
    [InlineData("2024/monster/vampire-mist", "**Speed** 5 ft., fly 20 ft. (hover)")]
    [InlineData("2014/monster/quipper", "**Speed** swim 40 ft.")]
    // A swarm: its type as the data spells it, resistances, linked condition immunities, no languages.
    [InlineData("2014/monster/swarm-of-bats", "*Medium swarm of Tiny beasts, unaligned*")]
    [InlineData("2014/monster/swarm-of-bats", "**Damage Resistances** bludgeoning, piercing, slashing")]
    [InlineData("2014/monster/swarm-of-bats", "**Condition Immunities** Charmed (`2014/condition/charmed`), Frightened (`2014/condition/frightened`), Grappled (`2014/condition/grappled`), Paralyzed (`2014/condition/paralyzed`), Petrified (`2014/condition/petrified`), Prone (`2014/condition/prone`), Restrained (`2014/condition/restrained`), Stunned (`2014/condition/stunned`)")]
    [InlineData("2014/monster/swarm-of-bats", "**Languages** —")]
    // A qualified resistance is set off with a semicolon, never merged into the plain list.
    [InlineData("2014/monster/balor", "**Damage Resistances** cold, lightning; bludgeoning, piercing, and slashing from nonmagical weapons")]
    [InlineData("2014/monster/archmage", "**Damage Resistances** damage from spells; bludgeoning, piercing, and slashing from nonmagical attacks (from stoneskin)")]
    [InlineData("2014/monster/werewolf-human", "**Damage Immunities** bludgeoning, piercing, and slashing from nonmagical weapons that aren't silvered")]
    // Shapechangers: subtype, the other forms (whose names contain commas) linked.
    [InlineData("2014/monster/vampire-vampire", "*Medium undead (shapechanger), lawful evil*")]
    [InlineData("2014/monster/vampire-vampire", "**Other forms** Vampire, Bat Form (`2014/monster/vampire-bat`); Vampire, Mist Form (`2014/monster/vampire-mist`)")]
    [InlineData("2024/monster/werewolf-wolf", "**Other forms** Werewolf, Human Form (`2024/monster/werewolf-human`); Werewolf, Hybrid Form (`2024/monster/werewolf-hybrid`)")]
    // Hydra in both editions: "Number of Heads" multiattack prose, empty 2014 languages vs 2024 "None".
    [InlineData("2014/monster/hydra", "**Hit Points** 172 (15d12 + 75)")]
    [InlineData("2014/monster/hydra", "**Languages** —")]
    [InlineData("2014/monster/hydra", "***Multiattack.*** The hydra makes as many bite attacks as it has heads.")]
    [InlineData("2024/monster/hydra", "**Hit Points** 184 (16d12 + 80)")]
    [InlineData("2024/monster/hydra", "**Languages** None")]
    [InlineData("2024/monster/hydra", "***Multiattack.*** The hydra makes as many Bite attacks as it has heads.")]
    [InlineData("2024/monster/hydra", "**Condition Immunities** Blinded (`2024/condition/blinded`), Charmed (`2024/condition/charmed`), Deafened (`2024/condition/deafened`), Frightened (`2024/condition/frightened`), Stunned (`2024/condition/stunned`), Unconscious (`2024/condition/unconscious`)")]
    // 2024 condition immunity notes stay next to the condition they qualify.
    [InlineData("2024/monster/archmage", "**Condition Immunities** Charmed (with Mind Blank) (`2024/condition/charmed`)")]
    // Spell lists: 2014 names them in the prose, so only refs are added; a 2024 single-spell entry likewise.
    [InlineData("2014/monster/acolyte", "Spells: Light (`2014/spell/light`), Sacred Flame (`2014/spell/sacred-flame`), Thaumaturgy (`2014/spell/thaumaturgy`), Bless (`2014/spell/bless`), Cure Wounds (`2014/spell/cure-wounds`), Sanctuary (`2014/spell/sanctuary`)")]
    [InlineData("2024/monster/pit-fiend", "Spells: Fireball (level 5 version; Cast twice) (`2024/spell/fireball`), Hold Monster (level 7 version; Can replace one Fireball) (`2024/spell/hold-monster`), Wall of Fire (Can replace one Fireball) (`2024/spell/wall-of-fire`)")]
    [InlineData("2024/monster/archmage", "- **2/Day Each:** Fly (`2024/spell/fly`), Lightning Bolt (level 7 version) (`2024/spell/lightning-bolt`)")]
    [InlineData("2024/monster/archmage", "- **1/Day Each:** Cone of Cold (level 9 version) (`2024/spell/cone-of-cold`), Mind Blank (Cast before combat) (`2024/spell/mind-blank`), Scrying (`2024/spell/scrying`), Teleport (`2024/spell/teleport`)")]
    // Each spell's own note is a rule of this monster's version (a linked base spell says otherwise): the hag's
    // Disguise Self lasts 24 hours, the dragon's Shapechange needs no Concentration, Mage Armor is already in the AC.
    [InlineData("2024/monster/green-hag", "- **At Will:** Dancing Lights (`2024/spell/dancing-lights`), Disguise Self (24-hour duration) (`2024/spell/disguise-self`), Invisibility (Self only, and the hag leaves no tracks while Invisible) (`2024/spell/invisibility`), Minor Illusion (`2024/spell/minor-illusion`), Ray of Sickness (level 3 version) (`2024/spell/ray-of-sickness`)")]
    [InlineData("2024/monster/adult-gold-dragon", "- **At Will:** Detect Magic (`2024/spell/detect-magic`), Guiding Bolt (level 2 version) (`2024/spell/guiding-bolt`), Shapechange (Beast or Humanoid form only, no Temporary Hit Points gained from the spell, and no Concentration or Temporary Hit Points required to maintain the spell) (`2024/spell/shapechange`)")]
    [InlineData("2024/monster/mage", "- **At Will:** Detect Magic (`2024/spell/detect-magic`), Light (`2024/spell/light`), Mage Armor (Included in AC) (`2024/spell/mage-armor`), Mage Hand (`2024/spell/mage-hand`), Prestidigitation (`2024/spell/prestidigitation`)")]
    [InlineData("2024/monster/dryad", "- **At Will:** Animal Friendship (`2024/spell/animal-friendship`), Charm Monster (Lasts 24 hours; ends early if the dryad casts the spell again) (`2024/spell/charm-monster`), Druidcraft (`2024/spell/druidcraft`)")]
    // 2014 notes are in the prose too; the ref line keeps them next to the spell they qualify.
    [InlineData("2014/monster/night-hag", "Spells: Detect Magic (`2014/spell/detect-magic`), Magic Missile (`2014/spell/magic-missile`), Plane Shift (Self Only) (`2014/spell/plane-shift`), Ray of Enfeeblement (`2014/spell/ray-of-enfeeblement`), Sleep (`2014/spell/sleep`)")]
    // 2014 levels are the spells' own; the drider's cantrip stored at level 1 gets no "version".
    [InlineData("2014/monster/drider", "Spells: Dancing Lights (`2014/spell/dancing-lights`), Darkness (`2014/spell/darkness`), Faerie Fire (`2014/spell/faerie-fire`)")]
    public void Body_RealMonster_HasExactLine(string reference, string expectedLine)
    {
        AssertHasLine(Body(reference), expectedLine);
    }

    [Theory]
    [InlineData("2014/monster/adult-red-dragon", "Traits", "***Legendary Resistance (3/Day).*** ")]
    [InlineData("2014/monster/adult-red-dragon", "Legendary Actions", "***Wing Attack (Costs 2 Actions).*** The dragon beats its wings.")]
    [InlineData("2014/monster/ankheg", "Actions", "***Acid Spray (Recharge 6).*** ")]
    [InlineData("2014/monster/air-elemental", "Actions", "***Whirlwind (Recharge 4–6).*** ")]
    [InlineData("2014/monster/wereboar-human", "Traits", "***Relentless (Recharges after a Short or Long Rest).*** ")]
    [InlineData("2014/monster/acolyte", "Traits", "***Spellcasting.*** The acolyte is a 1st-level spellcaster.")]
    [InlineData("2024/monster/blink-dog", "Bonus Actions", "***Teleport (Recharge 4–6).*** ")]
    // SRD 5.2.1 spells it "Recharges after a Long Rest" (2024 Find Steed's Otherworldly Steed), the same as SRD 5.1.
    [InlineData("2024/monster/cloaker", "Bonus Actions", "***Phantasms (Recharges after a Short or Long Rest).*** ")]
    [InlineData("2024/monster/goblin-warrior", "Bonus Actions", "***Nimble Escape.*** The goblin takes the Disengage or Hide action.")]
    [InlineData("2024/monster/night-hag", "Actions", "***Nightmare Haunting (1/Day; Requires Soul Bag).*** ")]
    [InlineData("2024/monster/pit-fiend", "Actions", "***Hellfire Spellcasting (Recharge 4–6).*** ")]
    [InlineData("2024/monster/archmage", "Reactions", "***Protective Magic (3/Day).*** ")]
    [InlineData("2024/monster/adult-red-dragon", "Legendary Actions", "***Commanding Presence.*** ")]
    public void Body_RealMonster_EntryIsInItsSectionWithUsageInItsName(string reference, string section, string entryStart)
    {
        var body = Body(reference);
        var sectionText = Section(body, section);

        Assert.True(sectionText is not null, $"{reference}: no ### {section} section in:\n{body}");
        Assert.True(sectionText.Split('\n').Any(l => l.StartsWith(entryStart, StringComparison.Ordinal)),
            $"{reference}: ### {section} has no entry starting \"{entryStart}\":\n{sectionText}");
    }

    [Fact]
    public void Body_MonsterWithFlavourText_PutsItBetweenSubtitleAndStatBlock()
    {
        var body = Body("2014/monster/acolyte");
        const string flavour = "Acolytes are junior members of a clergy, usually answerable to a priest. They perform a variety of functions in a temple and are granted minor spellcasting power by their deities.";

        var lines = body.Split('\n');
        Assert.Equal("*Medium humanoid (any race), any alignment*", lines[0]);
        Assert.Equal(flavour, lines[2]);
        Assert.StartsWith("**Armor Class** ", lines[4], StringComparison.Ordinal);
    }

    [Theory]
    // 2014 separates an entry's paragraphs with a bare newline, which markdown would run together.
    [InlineData("2014/monster/hydra",
        "***Multiple Heads.*** The hydra has five heads. While it has more than one head, the hydra has advantage on saving throws against being blinded, charmed, deafened, frightened, stunned, and knocked unconscious.\n\n" +
        "Whenever the hydra takes 25 or more damage in a single turn, one of its heads dies. If all its heads die, the hydra dies.\n\n" +
        "At the end of its turn, it grows two heads")]
    // A spell list inside the desc stays one tight list, followed by the refs the desc does not carry.
    [InlineData("2014/monster/acolyte",
        "The acolyte has following cleric spells prepared:\n\n" +
        "- Cantrips (at will): light, sacred flame, thaumaturgy\n" +
        "- 1st level (3 slots): bless, cure wounds, sanctuary\n\n" +
        "Spells: Light (`2014/spell/light`)")]
    public void Body_MultiParagraphEntry_KeepsItsParagraphsAndListsApart(string reference, string expected)
    {
        var body = Body(reference);

        Assert.True(body.Contains(expected, StringComparison.Ordinal), $"Expected\n{expected}\nin:\n{body}");
    }

    [Fact]
    public void Body_MonsterWithEmptyEntryArrays_HasNoEmptySections()
    {
        // goblin-warrior has special_abilities: [] and legendary_actions: [], and no reactions key at all.
        var body = Body("2024/monster/goblin-warrior");

        Assert.DoesNotContain("### Traits", body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Reactions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("### Legendary Actions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("**Saving Throws**", body, StringComparison.Ordinal);
        Assert.DoesNotContain("**Damage", body, StringComparison.Ordinal);
    }

    public static TheoryData<string> Editions()
    {
        var data = new TheoryData<string>();
        foreach (var edition in SrdEdition.All)
        {
            data.Add(edition);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Body_EveryMonster_HasOneSectionPerNonEmptyEntryArrayInStatBlockOrder(string edition)
    {
        foreach (var doc in Lookup.OfKind(edition, SrdKinds.Monster))
        {
            var headings = Body(doc).Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).ToList();
            var expected = MonsterMarkdown.EntrySections
                .Where(s => doc.Root.Arr(s.Property).Count > 0)
                .Select(s => $"### {s.Title}")
                .ToList();

            Assert.True(expected.SequenceEqual(headings),
                $"{doc.Ref}: expected sections [{string.Join(", ", expected)}] but found [{string.Join(", ", headings)}].");
        }
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Body_EveryMonster_ShowsEveryEntryDescLineVerbatimAndEveryLinkedSpellsRef(string edition)
    {
        foreach (var doc in Lookup.OfKind(edition, SrdKinds.Monster))
        {
            var body = Body(doc);
            foreach (var (property, _) in MonsterMarkdown.EntrySections)
            {
                foreach (var entry in doc.Root.Arr(property))
                {
                    // Word for word: every line of the desc, with only line breaks between them changed.
                    foreach (var line in (entry.Str("desc") ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    {
                        Assert.True(body.Contains(line, StringComparison.Ordinal),
                            $"{doc.Ref}: {property} entry \"{entry.Str("name")}\" line not shown verbatim: \"{line}\"");
                    }

                    foreach (var spell in entry.Obj("spellcasting")?.Arr("spells") ?? [])
                    {
                        var reference = SrdRef.FromApiUrl(spell.Str("url"));
                        Assert.True(reference is not null && body.Contains($"(`{reference}`)", StringComparison.Ordinal),
                            $"{doc.Ref}: spell {spell.Str("name")} in \"{entry.Str("name")}\" is not shown with its ref.");
                    }
                }
            }
        }
    }

    [Theory]
    // Every spell note in the data: 11 in 2014, 28 in 2024 (21 of them in "…the following spells:" lists, where the
    // desc says nothing about them). The count proves the loop checked something.
    [InlineData("2014", 11)]
    [InlineData("2024", 28)]
    public void Body_EveryMonsterSpellWithANote_ShowsTheNoteBetweenTheSpellAndItsRef(string edition, int notes)
    {
        // A model follows the ref to the base spell; without the note next to it, the green hag's Disguise Self lasts
        // 1 hour instead of 24 and the dragon's Shapechange needs Concentration.
        var shown = 0;
        foreach (var doc in Lookup.OfKind(edition, SrdKinds.Monster))
        {
            var body = Body(doc);
            foreach (var (property, _) in MonsterMarkdown.EntrySections)
            {
                foreach (var entry in doc.Root.Arr(property))
                {
                    foreach (var spell in entry.Obj("spellcasting")?.Arr("spells") ?? [])
                    {
                        if (spell.Str("notes") is not { Length: > 0 } note)
                        {
                            continue;
                        }

                        var reference = SrdRef.FromApiUrl(spell.Str("url"));
                        var expected = $@"{Regex.Escape(spell.Str("name")!)} \((?:level \d+ version; )?{Regex.Escape(note.Trim())}\) \(`{Regex.Escape($"{reference}")}`\)";
                        Assert.True(reference is not null && Regex.IsMatch(body, expected),
                            $"{doc.Ref}: spell {spell.Str("name")} is not shown as \"Name ({note}) (`ref`)\":\n{body}");
                        shown++;
                    }
                }
            }
        }

        Assert.Equal(notes, shown);
    }

    [Fact]
    public void Body_NotesWithSurroundingWhitespace_AreShownTrimmedAndBlankOnesLeftOut()
    {
        const string json = "{\"condition_immunities\":[{\"index\":\"charmed\",\"name\":\"Charmed\",\"url\":\"/api/2024/conditions/charmed\",\"note\":\"  with Mind Blank \"}]," +
                            "\"actions\":[{\"name\":\"Spellcasting\",\"desc\":\"It casts one of the following spells:\",\"spellcasting\":{\"spells\":[" +
                            "{\"name\":\"Invisibility\",\"level\":2,\"url\":\"/api/2024/spells/invisibility\",\"notes\":\" Self only \",\"usage\":{\"type\":\"at will\"}}," +
                            "{\"name\":\"Light\",\"level\":0,\"url\":\"/api/2024/spells/light\",\"notes\":\"  \",\"usage\":{\"type\":\"at will\"}}]}}]}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = json };

        var body = MonsterMarkdown.Body(doc, Lookup);

        AssertHasLine(body, "**Condition Immunities** Charmed (with Mind Blank) (`2024/condition/charmed`)");
        AssertHasLine(body, "- **At Will:** Invisibility (Self only) (`2024/spell/invisibility`), Light (`2024/spell/light`)");
    }

    [Theory]
    // A shield-only entry after the base armor is the monster's standard kit: one AC, as the SRD prints it.
    [InlineData("[{\"type\":\"dex\",\"value\":12},{\"type\":\"armor\",\"value\":14,\"armor\":[{\"index\":\"shield\",\"name\":\"Shield\",\"url\":\"/api/2014/equipment/shield\"}]}]",
        "**Armor Class** 14 (Shield (`2014/equipment/shield`))")]
    [InlineData("[{\"type\":\"armor\",\"value\":16,\"armor\":[{\"index\":\"chain-shirt\",\"name\":\"Chain Shirt\",\"url\":\"/api/2014/equipment/chain-shirt\"}]},{\"type\":\"armor\",\"value\":18,\"armor\":[{\"index\":\"shield\",\"name\":\"Shield\",\"url\":\"/api/2014/equipment/shield\"}]}]",
        "**Armor Class** 18 (Chain Shirt (`2014/equipment/chain-shirt`), Shield (`2014/equipment/shield`))")]
    // A spell's or condition's AC is an alternative, not kit; so is a second entry that is more than a shield.
    [InlineData("[{\"type\":\"dex\",\"value\":12},{\"type\":\"spell\",\"value\":15,\"spell\":{\"index\":\"mage-armor\",\"name\":\"Mage Armor\",\"url\":\"/api/2014/spells/mage-armor\"}},{\"type\":\"armor\",\"value\":17,\"armor\":[{\"index\":\"shield\",\"name\":\"Shield\",\"url\":\"/api/2014/equipment/shield\"}]}]",
        "**Armor Class** 12, 15 with Mage Armor (`2014/spell/mage-armor`), 17 with Shield (`2014/equipment/shield`)")]
    [InlineData("[{\"type\":\"natural\",\"value\":15},{\"type\":\"armor\",\"value\":18,\"armor\":[{\"index\":\"chain-mail\",\"name\":\"Chain Mail\",\"url\":\"/api/2014/equipment/chain-mail\"},{\"index\":\"shield\",\"name\":\"Shield\",\"url\":\"/api/2014/equipment/shield\"}]}]",
        "**Armor Class** 15 (natural armor), 18 with Chain Mail (`2014/equipment/chain-mail`), Shield (`2014/equipment/shield`)")]
    public void Body_ArmorClassWithAShieldOnlyEntry_MergesItOnlyIntoTheBaseArmor(string armorClass, string expected)
    {
        var doc = new SrdDocument
        {
            Edition = SrdEdition.Edition2014, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = $"{{\"armor_class\":{armorClass}}}",
        };

        AssertHasLine(MonsterMarkdown.Body(doc, Lookup), expected);
    }

    [Theory]
    // Research/PLAN counts: 2014 has 65 recharge-on-roll entries, 2024 has 88 (72 actions, 15 bonus actions,
    // 1 reaction); per-day 40 / 61 (29 of them with a lair count); recharge after rest 12 / 1. Upstream strips all
    // of these from the names, so each is only visible if the formatter puts it back.
    [InlineData("2014", 65, 40, 0, 12)]
    // 61 per-day, as in the raw file, by two corrections that cancel out: srd-corrections.json removes the Octopus's Ink
    // Cloud (1/Day) upstream gave the Mule, and adds the Unicorn's Blessing (3/Day) bonus action upstream dropped.
    [InlineData("2024", 88, 61, 29, 1)]
    public void Body_AllMonsters_RestoreEveryUsageToTheEntryName(string edition, int recharge, int perDay, int inLair, int afterRest)
    {
        var headers = Lookup.OfKind(edition, SrdKinds.Monster)
            .SelectMany(doc => Body(doc).Split('\n'))
            .Where(l => l.StartsWith("***", StringComparison.Ordinal))
            .Select(l => l[..(l.IndexOf(".*** ", StringComparison.Ordinal) is var end and >= 0 ? end : l.Length)])
            .ToList();

        Assert.Equal(recharge, headers.Count(h => RechargeUsage().IsMatch(h)));
        Assert.Equal(perDay, headers.Count(h => PerDayUsage().IsMatch(h)));
        Assert.Equal(inLair, headers.Count(h => h.Contains("/Day in Lair", StringComparison.Ordinal)));
        Assert.Equal(afterRest, headers.Count(h => h.Contains("after a Short or Long Rest)", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("recharge on roll", "{\"type\":\"recharge on roll\",\"dice\":\"1d6\",\"min_value\":5}", "Recharge 5–6")]
    [InlineData("recharge 6", "{\"type\":\"recharge on roll\",\"dice\":\"1d6\",\"min_value\":6}", "Recharge 6")]
    [InlineData("other die", "{\"type\":\"recharge on roll\",\"dice\":\"1d8\",\"min_value\":7}", "Recharge 7–8")]
    [InlineData("per day", "{\"type\":\"per day\",\"times\":3}", "3/Day")]
    [InlineData("per day in lair", "{\"type\":\"per day\",\"times\":3,\"times_in_lair\":4}", "3/Day, or 4/Day in Lair")]
    [InlineData("long rest only", "{\"type\":\"recharge after rest\",\"rest_types\":[\"long\"]}", "Recharges after a Long Rest")]
    // Both SRDs say "Recharges after a …" (5.2.1 in 2024 Find Steed's Otherworldly Steed), so both editions do.
    [InlineData("short or long rest", "{\"type\":\"recharge after rest\",\"rest_types\":[\"short\",\"long\"]}", "Recharges after a Short or Long Rest")]
    [InlineData("at will", "{\"type\":\"at will\"}", null)]
    [InlineData("unknown type", "{\"type\":\"per week\",\"times\":1}", null)]
    [InlineData("missing count", "{\"type\":\"per day\"}", null)]
    public void Usage_Shape_RendersTheSrdWordingOrNothing(string scenario, string usageJson, string? expected)
    {
        using var usage = JsonDocument.Parse(usageJson);

        Assert.True(expected == MonsterMarkdown.Usage(usage.RootElement), scenario);
    }

    [Fact]
    public void Body_EveryQualifiedDamageEntryNextToOthers_IsSetOffWithASemicolon()
    {
        // A comma join would read "cold, bludgeoning, piercing, and slashing from nonmagical weapons" as one list.
        foreach (var doc in Lookup.OfKind(SrdEdition.Edition2014, SrdKinds.Monster))
        {
            foreach (var (property, label) in new[]
                     {
                         ("damage_vulnerabilities", "Damage Vulnerabilities"),
                         ("damage_resistances", "Damage Resistances"),
                         ("damage_immunities", "Damage Immunities"),
                     })
            {
                var items = doc.Root.Arr(property).Select(e => e.GetString()!).ToList();
                if (items.Count < 2 || !items.Any(i => i.Contains(',')))
                {
                    continue;
                }

                var line = Body(doc).Split('\n').Single(l => l.StartsWith($"**{label}** ", StringComparison.Ordinal));
                Assert.Contains("; ", line, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    // Every field missing, and every field the wrong JSON type: a formatter exception would reach the model as the
    // SDK's bare "An error occurred", so odd records must render what they can instead.
    [InlineData("{}")]
    [InlineData("{\"armor_class\":\"19\",\"hit_points\":\"many\",\"speed\":[],\"senses\":3,\"strength\":\"x\",\"proficiencies\":[1,{\"value\":\"2\"}],\"damage_resistances\":\"fire\",\"condition_immunities\":[\"prone\"],\"languages\":7,\"challenge_rating\":\"1\",\"gear\":7,\"forms\":{},\"special_abilities\":[5,{\"name\":3,\"usage\":\"daily\"}],\"actions\":[{\"name\":\"Spellcasting\",\"desc\":\"Casts:\",\"spellcasting\":{\"spells\":[{\"name\":\"Odd\",\"level\":\"9\",\"usage\":{\"type\":\"per day\"}}]}}],\"legendary_actions\":{}}")]
    [InlineData("{\"armor_class\":[{\"type\":\"spell\"},{\"value\":12,\"spell\":{}}],\"speed\":{\"hover\":true},\"actions\":[{\"name\":\"Breath\",\"usage\":{\"type\":\"recharge on roll\",\"dice\":\"d\",\"min_value\":5}}]}")]
    public void Body_MalformedMonsterRecord_RendersWithoutThrowing(string json)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = json };

        var body = MonsterMarkdown.Body(doc, Lookup);

        Assert.Contains("**Languages** —", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_SpellcastingSpellWithoutAUsableUrl_IsShownByNameWithoutARef()
    {
        const string json = "{\"actions\":[{\"name\":\"Spellcasting\",\"desc\":\"It casts one of the following spells:\"," +
                            "\"spellcasting\":{\"spells\":[{\"name\":\"Old Spell\",\"level\":3,\"url\":\"/api/2014/rule-sections/x\",\"usage\":{\"type\":\"at will\"}}," +
                            "{\"name\":\"Fireball\",\"level\":5,\"url\":\"/api/2024/spells/fireball\",\"usage\":{\"type\":\"per day\",\"times\":2}}]}}]}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = json };

        var body = MonsterMarkdown.Body(doc, Lookup);

        AssertHasLine(body, "- **At Will:** Old Spell");
        AssertHasLine(body, "- **2/Day Each:** Fireball (level 5 version) (`2024/spell/fireball`)");
    }

    [Theory]
    // 2024 stores the level a spell is cast at: above the spell's own level, it is an upcast "version".
    [InlineData("2024", "fireball", 5, "Fireball (level 5 version) (`2024/spell/fireball`)")]
    [InlineData("2024", "fireball", 3, "Fireball (`2024/spell/fireball`)")]
    // Cantrips have no versions, whatever level the data stores (the 2014 drider stores Dancing Lights at 1).
    [InlineData("2024", "light", 1, "Light (`2024/spell/light`)")]
    // 2014 levels are the spells' own, never cast levels, so a higher one is a data slip, not an upcast.
    [InlineData("2014", "fireball", 5, "Fireball (`2014/spell/fireball`)")]
    public void Body_SpellcastingSpellLevel_IsAVersionOnlyFor2024UpcastLeveledSpells(string edition, string slug, int level, string expected)
    {
        var json = "{\"actions\":[{\"name\":\"Spellcasting\",\"desc\":\"It casts the following spell:\",\"spellcasting\":{\"spells\":[" +
                   $"{{\"name\":\"{Lookup.Require($"{edition}/spell/{slug}").Name}\",\"level\":{level},\"url\":\"/api/{edition}/spells/{slug}\",\"usage\":{{\"type\":\"at will\"}}}}]}}}}]}}";
        var doc = new SrdDocument { Edition = edition, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = json };

        AssertHasLine(MonsterMarkdown.Body(doc, Lookup), $"- **At Will:** {expected}");
    }

    private static string Body(string reference) => Body(Lookup.Require(reference));

    private static string Body(SrdDocument doc) => SrdMarkdown.Body(doc, Lookup);

    private static void AssertHasLine(string body, string expectedLine) =>
        Assert.True(body.Split('\n').Contains(expectedLine), $"Expected the line\n{expectedLine}\nin:\n{body}");

    // The text of one "### Title" section, up to the next ### heading.
    private static string? Section(string body, string title)
    {
        var start = body.IndexOf($"### {title}\n", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = body.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
        return end < 0 ? body[start..] : body[start..end];
    }

    [GeneratedRegex(@"\((?:[^)]*; )?Recharge \d")]
    private static partial Regex RechargeUsage();

    [GeneratedRegex(@"\(\d+/Day")]
    private static partial Regex PerDayUsage();
}
