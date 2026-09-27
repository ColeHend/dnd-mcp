using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Scans every record the server serves (the vendored 5e-database records of both editions and the 2024 Rules Glossary,
/// with <c>content/srd-corrections.json</c> applied) for the ways upstream damaged text. 2024 records came through PDF
/// extraction: words and table cells run together, words split at old line breaks, paragraphs glued at a period or
/// broken mid-sentence, tables broken into one-word lines or with rows run together and captions glued to the header,
/// flattened stat blocks, stray letters, text that breaks off mid-sentence or resumes mid-sentence, comma noise in spell
/// materials, packs that count "flasks of Oil" twice, and category lists that disagree with the items they list. 2014
/// records were partly back-translated from another language (Hold Monster's "a saving throw of Wisdom", "a level 6 or
/// higher location"), truncated (Conjure Animals' "with a 7th-level."), or mistyped ("1dl0", "o f"), and their
/// higher-level rules name the wrong slot (Dominate Beast's "9th level spell slot" for a 4th-level spell).
///
/// <para>
/// Why it exists: every answer is labelled "SRD 5.2.1" or "SRD 5.1", so a damaged record is relayed as fact (the 2024
/// Potion of Heroism carried the Gaseous Form potion's effect; the Martial Melee Weapons list had no Longsword; 2014
/// Dominate Beast's higher-level rule was wrong). The corrections overlay fixes what was found; this scan is what finds
/// the next one. A re-vendor that brings new damage fails here with the record and the offending text, instead of
/// shipping quietly. The SRD markdown itself is not in the repository, so these are signatures of damage, each
/// checked to occur nowhere in the SRD's own text.
/// </para>
/// <para>
/// What breaks it: new damage upstream (add a correction, copied verbatim from the SRD 5.2 markdown for 2024 or the
/// SRD 5.1 markdown for 2014, or allow-list it with the reason it stays), or a correction removed or edited so that
/// the damage it fixed comes back. Every
/// allow-list entry must still be flagged (<see cref="AllowList_EveryEntry_IsStillFlagged"/>), so the list shrinks when
/// upstream fixes something rather than going stale. Each signature is also shown to flag the damage it was written
/// for in the raw upstream data (<see cref="Signature_RawUpstreamData_FlagsTheDamageItWasWrittenFor"/>), so a regex
/// that silently stops matching fails too. What content/srd-corrections.md lists as left as upstream wrote it is pinned
/// as still served that way (<see cref="Served_DocumentedUpstreamWording_IsStillServed"/>), so that list cannot go
/// stale either.
/// </para>
/// </summary>
public sealed partial class SrdDataQualityTests
{
    // Records whose text is left as upstream wrote it, per check, and why. Keys are "check|edition/kind/slug".
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        // "5-footwide Line" / "30foot radius": one lost hyphen inside monster action text. The overlay replaces whole
        // top-level properties, so fixing it would copy each monster's entire actions array for one hyphen, and every
        // one of these dragons would then show a "corrected" label for a cosmetic slip. The words read correctly.
        ["glued-foot-compound|2024/monster/behir"] = "5-footwide",
        ["glued-foot-compound|2024/monster/black-dragon-wyrmling"] = "5-footwide",
        ["glued-foot-compound|2024/monster/young-black-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/adult-black-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/ancient-black-dragon"] = "10-footwide",
        ["glued-foot-compound|2024/monster/blue-dragon-wyrmling"] = "5-footwide",
        ["glued-foot-compound|2024/monster/young-blue-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/adult-blue-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/brass-dragon-wyrmling"] = "5-footwide",
        ["glued-foot-compound|2024/monster/young-brass-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/adult-brass-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/ancient-brass-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/bronze-dragon-wyrmling"] = "5-footwide",
        ["glued-foot-compound|2024/monster/young-bronze-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/adult-bronze-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/copper-dragon-wyrmling"] = "5-footwide",
        ["glued-foot-compound|2024/monster/young-copper-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/adult-copper-dragon"] = "5-footwide",
        ["glued-foot-compound|2024/monster/ancient-copper-dragon"] = "10-footwide",
        ["glued-foot-compound|2024/monster/fire-elemental"] = "30foot radius (Illumination)",
        ["glued-foot-compound|2024/monster/nightmare"] = "10foot radius (Illumination)",
        ["glued-foot-compound|2024/monster/pit-fiend"] = "20foot Emanation (Fear Aura)",

        // The SRD itself ends Telekinesis mid-sentence ("such as manipulating a simple tool,"): SRD 5.2 and the 5.2.1
        // PDF both stop there, so upstream is faithful.
        ["ends-mid-sentence|2024/spell/telekinesis"] = "the SRD's own text breaks off",
        // Only the closing period is missing ("…for a total of 6 hours"); every word is the SRD's.
        ["ends-mid-sentence|2024/equipment/oil"] = "final period missing",

        // A paragraph broken at an old line wrap mid-sentence ("On a failed save,\na creature has…"). Every word is
        // intact, and SrdProse rejoins a line that starts in lower case (FIR-5), so the body reads as one paragraph.
        ["line-starts-lower-case|2024/feature/berserker-intimidating-presence"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/feature/cleric-channel-divinity"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/feature/fighter-weapon-mastery"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/feature/monk-monks-focus"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/feature/monk-self-restoration"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/subclass/oath-of-devotion"] = "mid-sentence line wrap",
        ["line-starts-lower-case|2024/rule/long-jump"] = "mid-sentence line wrap",

        // Spellbook is not an SRD 5.2 item (upstream added it), so the SRD cannot say which side is right; its record
        // claims Adventuring Gear and the Adventuring Gear list omits it.
        ["category-membership|2024/equipment/spellbook"] = "not an SRD item",

        // "If it can't use ShapeShift": the SRD's "Shape-Shift" with its hyphen lost, inside one special_abilities entry
        // of each vampire form. The same trade-off as the footwide dragons: fixing it copies the whole array for a
        // cosmetic slip, and the word still reads as the Shape-Shift trait.
        ["camel-case-in-token|2024/monster/vampire-vampire"] = "ShapeShift",
        ["camel-case-in-token|2024/monster/vampire-bat"] = "ShapeShift",
        ["camel-case-in-token|2024/monster/vampire-mist"] = "ShapeShift",

        // SRD 5.2 files Rods and Scrolls as magic-item categories of their own ("a rod can be used as an Arcane Focus"),
        // but upstream 2024 has no rods or scrolls equipment-category record, so these claim Wondrous Items. The overlay
        // cannot create a category record, and pointing them at one that does not exist would print a dead link. Each
        // item's own type line ("Rod, Legendary") still says what it is.
        ["item-type-vs-category|2024/magic-item/immovable-rod"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/rod-of-absorption"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/rod-of-alertness"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/rod-of-lordly-might"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/rod-of-rulership"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/rod-of-security"] = "Rod filed under Wondrous Items",
        ["item-type-vs-category|2024/magic-item/spell-scroll"] = "Scroll filed under Wondrous Items",

        // 2014. "The GM rolls a d 100": one space inside a die in the sentence before Reincarnate's race table. Fixing it
        // would copy the whole table-bearing description (and invent a caption layout the SRD 5.1 markdown writes as
        // "**Table- Reincarnate Race**") for a space; the die still reads as a d100.
        ["dice-typo|2014/spell/reincarnate"] = "d 100",
        // Words split at an old line break, every letter intact ("fast- talk", "two- handed"; the SRD 5.1 markdown itself
        // writes "two- handed" and "weapons -"). Cosmetic, inside long rule, skill and feature texts.
        ["split-at-line-break|2014/skill/deception"] = "fast- talk",
        ["split-at-line-break|2014/rule/charisma"] = "fast- talk",
        ["split-at-line-break|2014/magic-item/marvelous-pigments"] = "weapons- that",
        ["split-at-line-break|2014/feature/martial-arts"] = "two- handed",
        // "a spell slot of 3nd level": the SRD 5.1 text's own typo, which upstream copies faithfully.
        ["wrong-ordinal-suffix|2014/spell/animal-messenger"] = "the SRD's own 3nd",
        // The SRD 5.1 text itself writes "mid-air" in Telekinesis (and "midair" elsewhere).
        ["line-break-hyphen|2014/spell/telekinesis"] = "mid-air is the SRD's",
        // The SRD's own higher-level rule starts above the next slot level: the conjure spells scale at 5th/6th/9th,
        // Flame Blade and Magic Weapon at 4th, Hunter's Mark at 3rd, Geas at 7th, Major Image at 6th.
        ["higher-level-first-slot|2014/spell/conjure-animals"] = "SRD scaling starts at 5th",
        ["higher-level-first-slot|2014/spell/conjure-celestial"] = "SRD scaling starts at 9th",
        ["higher-level-first-slot|2014/spell/conjure-minor-elementals"] = "SRD scaling starts at 6th",
        ["higher-level-first-slot|2014/spell/conjure-woodland-beings"] = "SRD scaling starts at 6th",
        ["higher-level-first-slot|2014/spell/flame-blade"] = "SRD scaling starts at 4th",
        ["higher-level-first-slot|2014/spell/geas"] = "SRD scaling starts at 7th",
        ["higher-level-first-slot|2014/spell/hunters-mark"] = "SRD scaling starts at 3rd",
        ["higher-level-first-slot|2014/spell/magic-weapon"] = "SRD scaling starts at 4th",
        ["higher-level-first-slot|2014/spell/major-image"] = "SRD scaling starts at 6th",
    };

    // Kinds whose text properties are SRD prose (paragraphs, tables, lists), as opposed to labels and names.
    private static readonly Dictionary<string, string[]> ProseProperties = new(StringComparer.Ordinal)
    {
        [SrdKinds.Spell] = ["description", "higher_level"],
        [SrdKinds.MagicItem] = ["desc"],
        [SrdKinds.Feature] = ["description"],
        [SrdKinds.Feat] = ["description"],
        [SrdKinds.Trait] = ["description"],
        [SrdKinds.Condition] = ["description"],
        [SrdKinds.WeaponProperty] = ["description"],
        [SrdKinds.WeaponMastery] = ["description"],
        [SrdKinds.Equipment] = ["description"],
        [SrdKinds.Poison] = ["description"],
        [SrdKinds.Subclass] = ["description"],
        [SrdKinds.Rule] = ["description"],
    };

    public static TheoryData<string> Checks =>
    [
        "glued-sentences", "glued-camel-case", "glued-number-word", "glued-foot-compound", "split-at-line-break",
        "garbled-stat-block", "stray-letter-line", "ends-mid-sentence", "line-starts-lower-case", "table-in-word-lines",
        "item-body-too-short", "item-body-starts-with-a-number", "item-type-line", "pack-flask-and-oil", "category-membership",
        "spell-material-noise", "table-rows-run-together", "caption-glued-to-header", "breaks-mid-sentence",
        "camel-case-in-token", "line-break-hyphen", "item-type-vs-category", "back-translation", "split-letters",
        "dice-typo", "truncated-level-phrase", "wrong-ordinal-suffix",
    ];

    // The 2014 records are stored as paragraph arrays, so the line-based checks above do not apply; these are the
    // signatures of the damage the 2014 audit found (RV-FC-N3), plus the string-level signatures shared with 2024.
    public static TheoryData<string> Checks2014 =>
    [
        "back-translation", "split-letters", "dice-typo", "truncated-level-phrase", "wrong-ordinal-suffix",
        "higher-level-phrasing", "higher-level-first-slot", "glued-sentences", "glued-camel-case", "glued-number-word", "glued-foot-compound",
        "split-at-line-break", "garbled-stat-block", "camel-case-in-token", "line-break-hyphen",
    ];

    [Theory]
    [MemberData(nameof(Checks))]
    public void Records2024_AfterCorrections_ShowNoUnexplainedDamage(string check) =>
        AssertNoUnexplainedDamage(check, SrdEdition.Edition2024, "the SRD 5.2 markdown");

    [Theory]
    [MemberData(nameof(Checks2014))]
    public void Records2014_AfterCorrections_ShowNoUnexplainedDamage(string check) =>
        AssertNoUnexplainedDamage(check, SrdEdition.Edition2014, "the SRD 5.1 markdown");

    // Each allow-list entry must still be needed: when upstream or a correction fixes the record, the entry goes.
    [Fact]
    public void AllowList_EveryEntry_IsStillFlagged()
    {
        var stale = AllowList.Keys
            .Where(entry =>
            {
                var (check, reference) = (entry[..entry.IndexOf('|')], entry[(entry.IndexOf('|') + 1)..]);
                var records = CorrectedSrdContent.Records(reference[..reference.IndexOf('/')], corrected: true);
                return !Flagged(check, records).Any(hit => hit.Ref.ToString() == reference);
            })
            .ToList();

        Assert.True(stale.Count == 0, "Allow-list entries no longer flagged (remove them):\n" + string.Join("\n", stale));
    }

    private static void AssertNoUnexplainedDamage(string check, string edition, string source)
    {
        var flagged = Flagged(check, CorrectedSrdContent.Records(edition, corrected: true))
            .Where(hit => !AllowList.ContainsKey($"{check}|{hit.Ref}"))
            .ToList();

        Assert.True(flagged.Count == 0,
            $"{check}: {flagged.Count} {edition} record(s) look damaged. Correct them in content/srd-corrections.json " +
            $"(text copied verbatim from {source}) or allow-list them here with the reason:\n" +
            string.Join("\n", flagged.Select(h => $"  {h.Ref}: {h.Evidence}")));
    }

    // The signatures have teeth: run over the RAW upstream records, each flags the damage it was written for. Every row
    // is a record the corrections overlay fixes, so these fail if a signature stops matching what it was built to find.
    [Theory]
    [InlineData("glued-sentences", "2024/magic-item/broom-of-flying")]                 // "pounds.The broom"
    [InlineData("glued-sentences", "2024/magic-item/belt-of-giant-strength")]          // "score.BeltStr.Rarity…"
    [InlineData("glued-camel-case", "2024/magic-item/potions-of-healing")]             // "PotionHP RegainedRarityPotion…"
    [InlineData("glued-number-word", "2024/magic-item/armor-of-resistance")]           // "1Acid6Necrotic2Cold…"
    [InlineData("glued-number-word", "2024/magic-item/sword-of-wounding")]             // "DC 15Constitution"
    [InlineData("glued-number-word", "2024/magic-item/staff-of-frost")]                // "5Hold Monster"
    [InlineData("glued-foot-compound", "2024/spell/grease")]                           // "10foot square"
    [InlineData("glued-foot-compound", "2024/equipment/ball-bearings")]                // "10-footsquare"
    [InlineData("split-at-line-break", "2024/feature/barbarian-unarmored-defense")]    // "Ar- mor"
    [InlineData("split-at-line-break", "2024/spell/reincarnate")]                      // "re- calls"
    [InlineData("garbled-stat-block", "2024/spell/summon-dragon")]                     // "MOD SAVE … WiS … chA"
    [InlineData("stray-letter-line", "2024/rule/breaking-objects")]                    // a line holding only "k"
    [InlineData("ends-mid-sentence", "2024/spell/control-weather")]                    // "…for the new"
    [InlineData("split-at-line-break", "2024/spell/control-water")]                    // "whirl- pool", beside Control Weather's spliced text
    [InlineData("line-starts-lower-case", "2024/feature/wizard-spellcasting")]         // "another Wizard cantrip…"
    [InlineData("table-in-word-lines", "2024/magic-item/bag-of-tricks")]               // one word per line
    [InlineData("item-body-too-short", "2024/magic-item/potion-of-gaseous-form")]      // "Potion of Healing (supreme)"
    [InlineData("item-body-starts-with-a-number", "2024/magic-item/potion-of-heroism")] // "10d4 + 20 Very Rare …"
    [InlineData("pack-flask-and-oil", "2024/equipment/burglars-pack")]                 // 7 Flasks and 7 Oil
    [InlineData("category-membership", "2024/equipment/hide-armor")]                   // claims Light, listed Medium
    [InlineData("category-membership", "2024/equipment/longsword")]                    // missing from its category
    [InlineData("category-membership", "2024/equipment/forgery-kit")]                  // missing from Tools
    [InlineData("category-membership", "2024/equipment/disguise-kit")]                 // listed in Tools, claims Gear
    [InlineData("spell-material-noise", "2024/spell/arcane-lock")]                     // "(gold dust…", shown as "M ((…"
    [InlineData("spell-material-noise", "2024/spell/forcecage")]                       // "1,500+ GP,, which"
    [InlineData("spell-material-noise", "2024/spell/imprisonment")]                    // "worth, 5,000+ GP"
    [InlineData("spell-material-noise", "2024/spell/summon-dragon")]                   // "the image of a, dragon"
    [InlineData("table-rows-run-together", "2024/feature/draconic-sorcery-draconic-spells")] // "5 | Fear, Fly 7 | Arcane Eye…"
    [InlineData("caption-glued-to-header", "2024/feature/fiend-patron-fiend-spells")]  // "Fiend Spells Warlock Level | Spells"
    [InlineData("caption-glued-to-header", "2024/feature/sorcerer-font-of-magic")]     // "Creating Spell Slots Spell Slot Level | …"
    [InlineData("breaks-mid-sentence", "2024/feature/cleric-divine-intervention")]    // "doesn't require a" / "Reaction to cast"
    [InlineData("camel-case-in-token", "2024/magic-item/belt-of-giant-strength")]      // "BeltStr"
    [InlineData("line-break-hyphen", "2024/trait/fiendish-legacy")]                    // "spell-casting"
    [InlineData("line-break-hyphen", "2024/magic-item/dimensional-shackles")]          // "suc-cessful"
    [InlineData("back-translation", "2014/spell/hold-monster")]                        // "a saving throw of Wisdom"
    [InlineData("back-translation", "2014/spell/fire-shield")]                         // "2d8 points of fire damage"
    [InlineData("back-translation", "2014/spell/water-breathing")]                     // "until the end of its term"
    [InlineData("back-translation", "2014/spell/see-invisibility")]                    // "see through Ethereal"
    [InlineData("split-letters", "2014/spell/wall-of-fire")]                           // "within 10 feet o f that side"
    [InlineData("dice-typo", "2014/spell/moonbeam")]                                   // "1dl0"
    [InlineData("dice-typo", "2014/spell/symbol")]                                     // "10d 10"
    [InlineData("truncated-level-phrase", "2014/spell/conjure-animals")]               // "three times as many with a 7th-level."
    [InlineData("higher-level-phrasing", "2014/spell/shatter")]                        // "a 3 or higher level spell slot"
    [InlineData("higher-level-phrasing", "2014/spell/hold-monster")]                   // "a level 6 or higher location"
    [InlineData("higher-level-first-slot", "2014/spell/dominate-beast")]               // a 4th-level spell's "9th level spell slot"
    [InlineData("line-break-hyphen", "2014/spell/shatter")]                            // "A non-magical item"
    public void Signature_RawUpstreamData_FlagsTheDamageItWasWrittenFor(string check, string reference)
    {
        var edition = reference[..reference.IndexOf('/')];
        var raw = Flagged(check, CorrectedSrdContent.Records(edition, corrected: false)).Select(h => h.Ref.ToString()).ToHashSet();
        var corrected = Flagged(check, CorrectedSrdContent.Records(edition, corrected: true)).Select(h => h.Ref.ToString()).ToHashSet();

        Assert.Contains(reference, raw);
        Assert.DoesNotContain(reference, corrected);
    }

    // content/srd-corrections.md lists what is deliberately left as upstream wrote it. Each listed wording is pinned as
    // still served, so the list is true: when a re-vendor changes one, this fails and the list (and any correction the
    // change makes possible) is revisited. The 2014 rows are differences shaped like PHB errata, where the SRD 5.1
    // markdown's wording and upstream's are each a coherent rule and no SRD 5.1 PDF is at hand to say which one the
    // SRD printed; the 2024 rows are cosmetic slips inside structured records.
    [Theory]
    [InlineData("2014/spell/contagion", "you afflict the creature with a disease of your choice")]
    [InlineData("2014/spell/sleet-storm", "If a creature is concentrating in the spell's area")]
    [InlineData("2014/spell/acid-splash", "Choose one creature within range, or choose two creatures within range")]
    [InlineData("2014/spell/polymorph", "A shapechanger automatically succeeds on this saving throw.")]
    [InlineData("2014/spell/true-polymorph", "the transformation becomes permanent")]
    [InlineData("2014/spell/disintegrate", "If this damage reduces the target to 0 hit points, it is disintegrated.")]
    [InlineData("2014/spell/phantasmal-killer", "At the start of each of the target's turns")]
    [InlineData("2014/spell/weird", "At the start of each of the frightened creature's turns")]
    [InlineData("2014/spell/color-spray", "is blinded until the spell ends")]
    [InlineData("2014/spell/call-lightning", "a point you can see 100 feet directly above you")]
    [InlineData("2014/spell/sanctuary", "If the warded creature makes an attack or casts a spell that affects an enemy creature")]
    [InlineData("2014/spell/glyph-of-warding", "If you choose an object, that object must remain in its place")]
    [InlineData("2014/spell/prismatic-wall", "A rod of cancellation destroys a prismatic wall")]
    [InlineData("2014/spell/simulacrum", "the illusion uses all the statistics of the creature it duplicates.")]
    [InlineData("2014/spell/moonbeam", "you can use an action to move the beam 60 feet in any direction")]
    [InlineData("2014/spell/levitate", "One creature or object of your choice")]
    [InlineData("2014/spell/unseen-servant", "an invisible, mindless, shapeless force")]
    [InlineData("2014/spell/heroes-feast", "Up to twelve other creatures can partake of the feast.")]
    [InlineData("2014/spell/find-steed", "the steed takes on a form that you choose, such as a warhorse")]
    [InlineData("2014/spell/slow", "makes another wisdom saving throw at the end of its turn")]
    [InlineData("2014/spell/mending", "such as a broken key, a torn cloak")]
    [InlineData("2014/magic-item/bag-of-tricks", "The creature vanishes at the next dawn or when it is reduced to 0 hit points.")]
    [InlineData("2014/monster/doppelganger", "In the first round of combat, the doppelganger has advantage")]
    [InlineData("2014/monster/shadow", "Its stealth bonus is also improved to +6.")]
    [InlineData("2014/condition/exhaustion", "provided that the creature has also ingested some food and drink.")]
    [InlineData("2024/class/cleric", "for which oyu have spell slots")]
    public void Served_DocumentedUpstreamWording_IsStillServed(string reference, string text)
    {
        Assert.Contains(text, ServedText(reference), StringComparison.Ordinal);
    }

    // Gaps listed as left as upstream wrote it: SRD text the served record lacks.
    [Theory]
    [InlineData("2014/magic-item/deck-of-many-things", "Reaping Scythe")] // the Skull card's Avatar of Death stat block
    [InlineData("2014/condition/exhaustion", "raised from the dead")]      // the SRD 5.1 markdown's closing sentence, shaped like errata
    public void Served_DocumentedGap_IsStillMissing(string reference, string absent)
    {
        Assert.DoesNotContain(absent, ServedText(reference), StringComparison.Ordinal);
    }

    // Animal Friendship has no higher-level text upstream, and the SRD 5.1 markdown's own is damaged ("you can affect one
    // additional beast t level above 1st"), so there is no verbatim text to add.
    [Fact]
    public void Served_AnimalFriendship2014_StillHasNoHigherLevelText()
    {
        Assert.False(Served("2014/spell/animal-friendship").TryGetProperty("higher_level", out _));
    }

    private static string ServedText(string reference) => string.Join("\n", StringValues(Served(reference)));

    private static JsonElement Served(string reference)
    {
        var parts = reference.Split('/');
        var record = CorrectedSrdContent.Records(parts[0], corrected: true)
            .SingleOrDefault(r => r.Ref == new SrdRef(parts[0], parts[1], parts[2]));
        Assert.True(record is not null, $"{reference} is not in the vendored content.");
        return record!.Json;
    }

    // Membership is checked against the item side too: every magic armor and weapon claims the Armor or Weapons
    // category, and the corrected lists hold them (the 2014 lists always did).
    [Theory]
    [InlineData("armor", "/api/2024/magic-items/armor-1")]
    [InlineData("armor", "/api/2024/magic-items/shield")]
    [InlineData("armor", "/api/2024/equipment/shield")]
    [InlineData("weapons", "/api/2024/magic-items/vorpal-sword")]
    [InlineData("weapons", "/api/2024/equipment/longsword")]
    [InlineData("martial-melee-weapons", "/api/2024/equipment/longsword")]
    [InlineData("tools", "/api/2024/equipment/forgery-kit")]
    [InlineData("equipment-packs", "/api/2024/equipment/entertainers-pack")]
    [InlineData("adventuring-gear", "/api/2024/equipment/explorers-pack")]
    public void Category2024_AfterCorrections_ListsTheItem(string category, string url)
    {
        var record = CorrectedSrdContent.Records(SrdEdition.Edition2024, corrected: true)
            .Single(r => r.Ref == new SrdRef(SrdEdition.Edition2024, SrdKinds.EquipmentCategory, category));

        Assert.Contains(url, record.Json.GetProperty("equipment").EnumerateArray().Select(e => e.GetProperty("url").GetString()));
    }

    private static IEnumerable<Hit> Flagged(string check, IReadOnlyList<SrdRecord> records) => check switch
    {
        "glued-sentences" => Strings(records, GluedSentences()),
        "glued-camel-case" => Strings(records, GluedCamelCase()),
        "glued-number-word" => Strings(records, GluedNumberWord()),
        "glued-foot-compound" => Strings(records, GluedFootCompound()),
        "split-at-line-break" => Strings(records, SplitAtLineBreak()),
        "garbled-stat-block" => Strings(records, GarbledStatBlock()),
        "stray-letter-line" => Prose(records, lines => lines.Count > 1 ? lines.FirstOrDefault(l => l.Length == 1 && char.IsLetter(l[0])) : null),
        "ends-mid-sentence" => Prose(records, EndsMidSentence),
        "line-starts-lower-case" => Prose(records, lines => lines.FirstOrDefault(l => !IsBlockLine(l) && char.IsLower(l[0]))),
        "table-in-word-lines" => Prose(records, WordLines),
        "item-body-too-short" => MagicItemBodies(records, body => body.Sum(WordCount) < 8 ? string.Join(" / ", body) : null),
        "item-body-starts-with-a-number" => MagicItemBodies(records, body => body.Count > 0 && char.IsAsciiDigit(body[0][0]) ? body[0] : null),
        "item-type-line" => ItemTypeLines(records),
        "pack-flask-and-oil" => PacksWithFlaskAndOil(records),
        "category-membership" => CategoryMembership(records),
        "spell-material-noise" => records.Where(r => r.Ref.Kind == SrdKinds.Spell && r.Json.TryGetProperty("material", out _))
            .Select(r => (r.Ref, Material: r.Json.GetProperty("material").GetString()!))
            .Where(x => MaterialNoise().IsMatch(x.Material))
            .Select(x => new Hit(x.Ref, $"material \"{x.Material}\"")),
        "table-rows-run-together" => Prose(records, lines => lines.FirstOrDefault(l => !l.StartsWith('|') && RowsRunTogether().IsMatch(l))),
        "caption-glued-to-header" => Prose(records, lines => lines.FirstOrDefault(l => CaptionGluedToHeader().IsMatch(l))),
        "breaks-mid-sentence" => Prose(records, BreaksMidSentence),
        "camel-case-in-token" => Strings(records, CamelCaseInToken()),
        "line-break-hyphen" => LineBreakHyphens(records),
        "item-type-vs-category" => ItemTypeVersusCategory(records),
        "back-translation" => Strings(records, BackTranslation()),
        "split-letters" => Strings(records, SplitLetters()),
        "dice-typo" => Strings(records, DiceTypo()),
        "truncated-level-phrase" => Strings(records, TruncatedLevelPhrase()),
        "wrong-ordinal-suffix" => Strings(records, WrongOrdinalSuffix()),
        "higher-level-phrasing" => HigherLevelTexts(records)
            .Where(x => !Ordinal().IsMatch(x.Text))
            .Select(x => new Hit(x.Ref, $"higher_level names no slot level: \"{Shorten(x.Text)}\"")),
        "higher-level-first-slot" => HigherLevelTexts(records)
            .Select(x => (x.Ref, x.Text, x.Level, First: Ordinal().Match(x.Text) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0))
            .Where(x => x.First != 0 && x.First != x.Level + 1)
            .Select(x => new Hit(x.Ref, $"a level {x.Level} spell's higher-level text starts at a level {x.First} slot: \"{Shorten(x.Text)}\"")),
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };

    // A prose line of six or more words that ends mid-sentence (a lower-case word or a compound such as "30-foot", no
    // closing punctuation) and is followed by a text line starting in upper case: a paragraph broken mid-sentence
    // before a capitalised game term ("…that doesn't require a" / "Reaction to cast."). SrdProse rejoins a break only
    // when the next line starts in lower case (that case is "line-starts-lower-case"), so this one renders as two
    // paragraphs. Labelled stat-block lines ("**AC** 10 + 1 per spell level") are not prose.
    private static string? BreaksMidSentence(IReadOnlyList<string> lines)
    {
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var (line, next) = (lines[i], lines[i + 1]);
            if (!IsBlockLine(line) && !line.StartsWith("**", StringComparison.Ordinal) && WordCount(line) >= 6 &&
                EndsMidSentence().IsMatch(line) && !IsBlockLine(next) && !next.StartsWith("**", StringComparison.Ordinal) &&
                char.IsUpper(next[0]))
            {
                return $"{line[Math.Max(0, line.Length - 40)..]} / {next}";
            }
        }

        return null;
    }

    // A hyphenated word ("spell-casting", "suc-cessful") whose unhyphenated form is the edition's usual spelling (at least
    // three times, and more than twice as often): a line-break hyphen kept without the space SplitAtLineBreak needs.
    private static IEnumerable<Hit> LineBreakHyphens(IReadOnlyList<SrdRecord> records)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in records.SelectMany(r => StringValues(r.Json)))
        {
            foreach (Match word in Word().Matches(text))
            {
                var key = word.Value.ToLowerInvariant();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var record in records)
        {
            var hyphenated = StringValues(record.Json)
                .SelectMany(text => Word().Matches(text).Select(m => m.Value))
                .FirstOrDefault(word =>
                {
                    var lower = word.ToLowerInvariant();
                    var joined = lower.Replace("-", "", StringComparison.Ordinal);
                    return lower.Count(c => c == '-') == 1 && counts.GetValueOrDefault(joined) >= 3 &&
                           counts.GetValueOrDefault(joined) > 2 * counts[lower];
                });
            if (hyphenated is not null)
            {
                yield return new Hit(record.Ref, $"\"{hyphenated}\" where the data otherwise writes \"{hyphenated.Replace("-", "", StringComparison.Ordinal)}\"");
            }
        }
    }

    // A 2024 magic item's type line ("Rod, Legendary", "Potion, Rare") names its SRD category; the category record it
    // claims should be that one.
    private static IEnumerable<Hit> ItemTypeVersusCategory(IReadOnlyList<SrdRecord> records) =>
        records.Where(r => r.Ref.Kind == SrdKinds.MagicItem && r.Ref.Edition == SrdEdition.Edition2024)
            .Select(r => (r.Ref, Type: ItemType().Match(Lines(r.Json.GetProperty("desc").GetString()!).FirstOrDefault() ?? "").Value,
                Category: r.Json.TryGetProperty("equipment_category", out var c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("index").GetString() : null))
            .Where(x => x.Type.Length > 0 && ItemTypeCategory.GetValueOrDefault(x.Type) != x.Category)
            .Select(x => new Hit(x.Ref, $"type line \"{x.Type}\" but category \"{x.Category}\""));

    private static readonly Dictionary<string, string?> ItemTypeCategory = new(StringComparer.Ordinal)
    {
        ["Armor"] = "armor", ["Potion"] = "potions", ["Ring"] = "rings", ["Rod"] = null, ["Scroll"] = null,
        ["Staff"] = "staffs", ["Wand"] = "wands", ["Weapon"] = "weapons", ["Wondrous Item"] = "wondrous-items",
    };

    // A 2014 spell's higher-level text (an array of paragraphs) and its level.
    private static IEnumerable<(SrdRef Ref, string Text, int Level)> HigherLevelTexts(IReadOnlyList<SrdRecord> records) =>
        records.Where(r => r.Ref.Kind == SrdKinds.Spell && r.Json.TryGetProperty("higher_level", out var h) && h.ValueKind == JsonValueKind.Array && h.GetArrayLength() > 0)
            .Select(r => (r.Ref, string.Join(" ", r.Json.GetProperty("higher_level").EnumerateArray().Select(p => p.GetString())), r.Json.GetProperty("level").GetInt32()));

    // Every string value of every record (names, descriptions, action text), except identity fields.
    private static IEnumerable<Hit> Strings(IReadOnlyList<SrdRecord> records, Regex signature)
    {
        foreach (var record in records)
        {
            foreach (var text in StringValues(record.Json))
            {
                if (signature.Match(text) is { Success: true } match)
                {
                    yield return new Hit(record.Ref, Around(text, match.Index, match.Length));
                    break;
                }
            }
        }
    }

    private static IEnumerable<Hit> Prose(IReadOnlyList<SrdRecord> records, Func<IReadOnlyList<string>, string?> check)
    {
        foreach (var record in records)
        {
            if (!ProseProperties.TryGetValue(record.Ref.Kind, out var properties))
            {
                continue;
            }

            foreach (var property in properties)
            {
                if (!record.Json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var lines = Lines(value.GetString()!);
                if (record.Ref.Kind == SrdKinds.MagicItem)
                {
                    lines = lines.Skip(1).ToList(); // the item type line ("Potion", "Wondrous Item")
                }

                if (lines.Count > 0 && check(lines) is { } evidence)
                {
                    yield return new Hit(record.Ref, $"{property}: \"{Shorten(evidence)}\"");
                    break;
                }
            }
        }
    }

    // The last line is prose of six or more words that ends with a letter or a comma: the text broke off.
    private static string? EndsMidSentence(IReadOnlyList<string> lines)
    {
        var last = lines[^1];
        return !IsBlockLine(last) && WordCount(last) >= 6 && (char.IsLetter(last[^1]) || last[^1] == ',') ? last : null;
    }

    // Six or more consecutive short lines outside a table: a table split into one cell per line (Bag of Tricks).
    private static string? WordLines(IReadOnlyList<string> lines)
    {
        var run = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            run = !lines[i].StartsWith('|') && WordCount(lines[i]) <= 2 ? run + 1 : 0;
            if (run >= 6)
            {
                return string.Join(" / ", lines.Skip(i - 5).Take(6));
            }
        }

        return null;
    }

    private static IEnumerable<Hit> MagicItemBodies(IReadOnlyList<SrdRecord> records, Func<IReadOnlyList<string>, string?> check) =>
        records.Where(r => r.Ref.Kind == SrdKinds.MagicItem)
            .Select(r => (r.Ref, Evidence: check(Lines(r.Json.GetProperty("desc").GetString()!).Skip(1).ToList())))
            .Where(x => x.Evidence is not null)
            .Select(x => new Hit(x.Ref, x.Evidence!));

    // The first line of a 2024 magic item's desc is its item type; EquipmentMarkdown builds the italic type line from it.
    private static IEnumerable<Hit> ItemTypeLines(IReadOnlyList<SrdRecord> records) =>
        records.Where(r => r.Ref.Kind == SrdKinds.MagicItem)
            .Select(r => (r.Ref, First: Lines(r.Json.GetProperty("desc").GetString()!).FirstOrDefault() ?? ""))
            .Where(x => !ItemType().IsMatch(x.First))
            .Select(x => new Hit(x.Ref, $"first line \"{Shorten(x.First)}\""));

    // "7 flasks of Oil" is seven Oil, which is sold by the flask; upstream parsed it into seven Flasks as well.
    private static IEnumerable<Hit> PacksWithFlaskAndOil(IReadOnlyList<SrdRecord> records) =>
        records.Where(r => r.Ref.Kind == SrdKinds.Equipment && r.Json.TryGetProperty("contents", out _))
            .Where(r => r.Json.GetProperty("contents").EnumerateArray()
                .Select(c => c.GetProperty("item").GetProperty("index").GetString())
                .ToHashSet() is var items && items.Contains("flask") && items.Contains("oil"))
            .Select(r => new Hit(r.Ref, "contents hold both Flask and Oil"));

    // Both directions: an item that claims a category the category does not list, and a listed item that does not
    // claim it. The item is reported, since the item is what a correction or allow-list entry names.
    private static IEnumerable<Hit> CategoryMembership(IReadOnlyList<SrdRecord> records)
    {
        var listed = records.Where(r => r.Ref.Kind == SrdKinds.EquipmentCategory)
            .ToDictionary(r => r.Ref.Slug, r => r.Json.GetProperty("equipment").EnumerateArray()
                .Select(e => e.GetProperty("url").GetString()!).ToHashSet(StringComparer.Ordinal));

        foreach (var item in records.Where(r => r.Ref.Kind is SrdKinds.Equipment or SrdKinds.MagicItem))
        {
            var claimed = Claimed(item.Json);
            var url = item.Json.GetProperty("url").GetString()!;
            var missing = claimed.Where(c => listed.TryGetValue(c, out var members) && !members.Contains(url)).ToList();
            var unclaimed = listed.Where(l => l.Value.Contains(url) && !claimed.Contains(l.Key)).Select(l => l.Key).ToList();
            if (missing.Count > 0 || unclaimed.Count > 0)
            {
                yield return new Hit(item.Ref, $"claims but not listed by [{string.Join(", ", missing)}]; listed by but does not claim [{string.Join(", ", unclaimed)}]");
            }
        }
    }

    private static HashSet<string> Claimed(JsonElement item)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (item.TryGetProperty("equipment_categories", out var many))
        {
            claimed.UnionWith(many.EnumerateArray().Select(c => c.GetProperty("index").GetString()!));
        }

        if (item.TryGetProperty("equipment_category", out var one) && one.ValueKind == JsonValueKind.Object)
        {
            claimed.Add(one.GetProperty("index").GetString()!);
        }

        return claimed;
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var text in StringValues(item))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "index" or "url" or "image")
                    {
                        continue;
                    }

                    foreach (var text in StringValues(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    // Table rows (markdown "| a | b |", or upstream's "3 / Blur, …" and "9 | Legend Lore, …"), list items and
    // headings are not prose lines: they need not end with a period or start a sentence.
    private static bool IsBlockLine(string line) => BlockLine().IsMatch(line);

    private static int WordCount(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Around(string text, int index, int length)
    {
        var from = Math.Max(0, index - 25);
        var to = Math.Min(text.Length, index + length + 15);
        return "\"…" + text[from..to].ReplaceLineEndings(" ") + "…\"";
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    // "pounds.The broom", "score.BeltStr": a paragraph or table glued on at a period.
    [GeneratedRegex(@"[a-z]{2}\.[A-Z][a-z]")]
    private static partial Regex GluedSentences();

    // "1d10Damage Type1d10Damage", "RaritySpell": table cells run into one word.
    [GeneratedRegex(@"[a-z\)][A-Z][a-z]+[A-Z]|\b[a-z]{3,}[A-Z][a-z]{2,}")]
    private static partial Regex GluedCamelCase();

    // "DC 15Constitution", "5Hold Monster", "(hill)21Rare": a number glued to the next word or cell.
    [GeneratedRegex(@"\b\d+[A-Z][a-z]+\b|\d[A-Z][a-z]+\d")]
    private static partial Regex GluedNumberWord();

    // "10foot square", "10-footsquare", "5-footwide": a compound whose hyphen was lost at a line break.
    [GeneratedRegex(@"\b\d+foot\b|\bfoot(?:wide|long|high|tall|square|deep|radius)\b")]
    private static partial Regex GluedFootCompound();

    // "Ar- mor", "re- calls": a word split at an old line break. "shin- and waist-deep" is the SRD's own hyphen.
    [GeneratedRegex(@"\b[A-Za-z]{2,}- (?!and\b|or\b|to\b)[a-z]{2,}")]
    private static partial Regex SplitAtLineBreak();

    // A stat block flattened into prose: its column headers and mangled ability names ("WiS", "chA").
    [GeneratedRegex(@"MOD SAVE|\bWiS\b|\bchA\b")]
    private static partial Regex GarbledStatBlock();

    // A spell's material text with PDF comma noise: a leading "(" from "M (", a doubled comma, or a comma after
    // "worth", after "25+", or after a word that cannot end a list item ("the, spell", "which, the", "a, dragon").
    [GeneratedRegex(@"^\(|,,|\bworth,|\+,|\b(?:a|the|which|as), ")]
    private static partial Regex MaterialNoise();

    [GeneratedRegex(@"^(\||[-*+•] |\d+[.)] |#|>|[\w–-]+ [|/] )")]
    private static partial Regex BlockLine();

    // "5 | Fear, Fly 7 | Arcane Eye, Charm Monster": two table rows on one line.
    [GeneratedRegex(@"(?:^|\s)\d{1,2} ?[|/] ?[^|/]+? \d{1,2} ?[|/] ")]
    private static partial Regex RowsRunTogether();

    // "Fiend Spells Warlock Level | Spells": a table's caption glued to its header row.
    [GeneratedRegex(@"^(?:[A-Z][\w'’.]* +){3,}Level ?[|/]")]
    private static partial Regex CaptionGluedToHeader();

    [GeneratedRegex(@"(?:[a-z]|\d+-[a-z]+)$")]
    private static partial Regex EndsMidSentence();

    // "ShapeShift", "BeltStr": two capitalised words run into one token.
    [GeneratedRegex(@"\b[A-Z][a-z]+[A-Z][a-z]+\b")]
    private static partial Regex CamelCaseInToken();

    [GeneratedRegex(@"\b[A-Za-z]{3,}(?:-[A-Za-z]{3,})*\b")]
    private static partial Regex Word();

    // Back-translated 2014 text: English the SRD never uses, each phrase checked to occur nowhere in the SRD 5.1
    // markdown ("a saving throw of Wisdom", "a level 6 or higher location" for a 6th-level slot, "until the end of
    // its term", "2d8 points of fire damage", "see through Ethereal").
    [GeneratedRegex(@"\bsaving throw of (?:Strength|Dexterity|Constitution|Intelligence|Wisdom|Charisma)\b|\blevel \d+ or higher location\b|\bof location\b|\bend of its term\b|\bdepending on the model\b|\bpoints of (?:acid|cold|fire|force|lightning|necrotic|poison|psychic|radiant|thunder|bludgeoning|piercing|slashing) damage\b|\bsee through Ethereal\b")]
    private static partial Regex BackTranslation();

    // "o f", "t he": a word split into letters.
    [GeneratedRegex(@"\b(?:o f|t he|a nd|th e)\b")]
    private static partial Regex SplitLetters();

    // "1dl0", "10d 10", "a d 100": a die expression mistyped.
    [GeneratedRegex(@"\b\d*dl\d|\b\d+d \d+\b|\bd \d{2,3}\b")]
    private static partial Regex DiceTypo();

    // "three times as many with a 7th-level.": an ordinal level adjective with its noun cut off.
    [GeneratedRegex(@"\b\d(?:st|nd|rd|th)-level[.,;:]")]
    private static partial Regex TruncatedLevelPhrase();

    [GeneratedRegex(@"\b([1-9])(?:st|nd|rd|th)\b")]
    private static partial Regex Ordinal();

    // "3nd", "2th": an ordinal with the wrong suffix.
    [GeneratedRegex(@"\b(?:1(?:nd|rd|th)|2(?:st|rd|th)|3(?:st|nd|th)|[4-9](?:st|nd|rd))\b")]
    private static partial Regex WrongOrdinalSuffix();

    [GeneratedRegex(@"^(Armor|Potion|Ring|Rod|Scroll|Staff|Wand|Weapon|Wondrous Item)\b")]
    private static partial Regex ItemType();

    private sealed record Hit(SrdRef Ref, string Evidence);
}

/// <summary>
/// Every record of an edition as the server sees it: the vendored 5e-database records and, for 2024, the Rules Glossary
/// entries (<c>2024/rule/{SrdSlug.FromName(name)}</c>), each with its <c>content/srd-corrections.json</c> entry applied
/// or not. Read from the test output's <c>content/</c>, which the project copies from the repository, so it is the
/// shipped data and the shipped corrections.
/// </summary>
internal static class CorrectedSrdContent
{
    public static string ContentRoot { get; } = Path.Combine(AppContext.BaseDirectory, "content");

    private static readonly Lazy<SrdCorrections> LazyCorrections = new(() => SrdCorrections.Load(ContentRoot));

    private static readonly Dictionary<(string Edition, bool Corrected), Lazy<IReadOnlyList<SrdRecord>>> Loaded = new()
    {
        [(SrdEdition.Edition2014, false)] = new(() => Load(SrdEdition.Edition2014, corrected: false)),
        [(SrdEdition.Edition2014, true)] = new(() => Load(SrdEdition.Edition2014, corrected: true)),
        [(SrdEdition.Edition2024, false)] = new(() => Load(SrdEdition.Edition2024, corrected: false)),
        [(SrdEdition.Edition2024, true)] = new(() => Load(SrdEdition.Edition2024, corrected: true)),
    };

    public static SrdCorrections Corrections => LazyCorrections.Value;

    public static IReadOnlyList<SrdRecord> Records(string edition, bool corrected) => Loaded[(edition, corrected)].Value;

    /// <summary>The vendored (uncorrected) record at <paramref name="reference"/>, or null when there is none.</summary>
    public static JsonElement? Vendored(SrdRef reference)
    {
        if (reference.Kind == SrdKinds.Rule && reference.Edition == SrdEdition.Edition2024)
        {
            return Glossary().Cast<JsonElement?>()
                .FirstOrDefault(g => SrdSlug.FromName(g!.Value.GetProperty("name").GetString()!) == reference.Slug);
        }

        if (SrdKinds.Find(reference.Kind)?.FileFor(reference.Edition) is not { } file)
        {
            return null;
        }

        return SrdTestContent.Raw(reference.Edition, file).EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(r => r!.Value.GetProperty("index").GetString() == reference.Slug);
    }

    private static IReadOnlyList<SrdRecord> Load(string edition, bool corrected)
    {
        var records = new List<SrdRecord>();
        foreach (var kind in SrdKinds.All)
        {
            if (kind.FileFor(edition) is not { } file)
            {
                continue;
            }

            foreach (var record in SrdTestContent.Raw(edition, file).EnumerateArray())
            {
                var reference = new SrdRef(edition, kind.Name, record.GetProperty("index").GetString()!);
                records.Add(new SrdRecord(reference, corrected ? Apply(reference, record) : record));
            }
        }

        if (edition != SrdEdition.Edition2024)
        {
            return records;
        }

        foreach (var entry in Glossary())
        {
            var reference = new SrdRef(SrdEdition.Edition2024, SrdKinds.Rule, SrdSlug.FromName(entry.GetProperty("name").GetString()!));
            records.Add(new SrdRecord(reference, corrected ? Apply(reference, entry) : entry));
        }

        return records;
    }

    private static JsonElement Apply(SrdRef reference, JsonElement record) =>
        Corrections.Apply(reference, record) is { } json ? JsonDocument.Parse(json).RootElement.Clone() : record;

    private static IEnumerable<JsonElement> Glossary()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContentRoot, SrdKinds.RulesGlossary2024FileName)));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}

internal sealed record SrdRecord(SrdRef Ref, JsonElement Json);
