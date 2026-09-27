using System.Text.RegularExpressions;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: an equipment, category, magic item, weapon property, weapon mastery or poison body states each field the
/// SRD record carries, in the SRD's own notation ("15 + Dex modifier (max 2)", "+2" for a shield, "20/60 ft. (thrown)"),
/// shows a ref for everything it links to, and renders upstream's prose with its tables and paragraphs intact.
///
/// <para>
/// Every expected line below was checked by hand against the vendored JSON. Lines are compared whole, so a wrong
/// label, a lost ref, a changed separator or a stray "0 gp" fails the test rather than slipping past a substring match.
/// </para>
/// </summary>
public sealed class EquipmentMarkdownTests
{
    private static readonly string[] OwnedKinds =
    [
        SrdKinds.Equipment, SrdKinds.EquipmentCategory, SrdKinds.MagicItem, SrdKinds.WeaponProperty,
        SrdKinds.WeaponMastery, SrdKinds.Poison,
    ];

    [Theory]
    [InlineData("2014/equipment/longsword",
        "**Category** Weapon (`2014/equipment-category/weapon`), Martial Weapons (`2014/equipment-category/martial-weapons`), Melee Weapons (`2014/equipment-category/melee-weapons`), Martial Melee Weapons (`2014/equipment-category/martial-melee-weapons`)")]
    [InlineData("2014/equipment/longsword", "**Damage** 1d8 slashing (`2014/damage-type/slashing`), or 1d10 two-handed")]
    [InlineData("2014/equipment/longsword", "**Properties** Versatile (`2014/weapon-property/versatile`)")]
    [InlineData("2014/equipment/longsword", "**Cost** 15 gp")]
    [InlineData("2014/equipment/longsword", "**Weight** 3 lb.")]
    [InlineData("2024/equipment/longsword",
        "**Category** Martial Melee Weapons (`2024/equipment-category/martial-melee-weapons`), Martial Weapons (`2024/equipment-category/martial-weapons`), Melee Weapons (`2024/equipment-category/melee-weapons`), Weapons (`2024/equipment-category/weapons`)")]
    [InlineData("2024/equipment/longsword", "**Damage** 1d8 Slashing (`2024/damage-type/slashing`), or 1d10 two-handed")]
    [InlineData("2024/equipment/longsword", "**Properties** Versatile (`2024/weapon-property/versatile`)")]
    [InlineData("2024/equipment/longsword", "**Mastery** Sap (`2024/weapon-mastery/sap`)")]
    [InlineData("2024/equipment/longsword", "**Cost** 15 gp")]
    [InlineData("2014/equipment/longbow", "**Range** 150/600 ft.")]
    [InlineData("2014/equipment/dagger", "**Range** 20/60 ft. (thrown)")]
    [InlineData("2014/equipment/dart", "**Range** 20/60 ft. (thrown)")]
    [InlineData("2014/equipment/dart", "**Cost** 5 cp")]
    [InlineData("2014/equipment/dart", "**Weight** 1/4 lb.")]
    [InlineData("2014/equipment/net", "**Range** 5/15 ft. (thrown)")]
    [InlineData("2024/equipment/javelin", "**Range** 30/120 ft. (thrown)")]
    [InlineData("2024/equipment/blowgun", "**Damage** 1 Piercing (`2024/damage-type/piercing`)")]
    [InlineData("2024/equipment/blowgun", "**Range** 25/100 ft.")]
    [InlineData("2024/equipment/blowgun", "**Ammunition** Needles (`2024/equipment/needles`)")]
    [InlineData("2024/equipment/lance", "**Notes** Two-handed unless mounted")]
    [InlineData("2024/equipment/lance",
        "**Properties** Heavy (`2024/weapon-property/heavy`), Reach (`2024/weapon-property/reach`), Two-Handed (`2024/weapon-property/two-handed`)")]
    public void Body_Weapon_StatesEachFieldAsALine(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, Lines(Body(reference)));
    }

    // Upstream gives every melee weapon range {normal: 5}, reach weapons included; a "Range 5 ft." line on a glaive would
    // contradict its Reach property. The morningstar has no range at all.
    [Theory]
    [InlineData("2014/equipment/longsword")]
    [InlineData("2014/equipment/glaive")]
    [InlineData("2024/equipment/glaive")]
    [InlineData("2024/equipment/morningstar")]
    public void Body_MeleeWeaponWithoutALongRange_HasNoRangeLine(string reference)
    {
        Assert.DoesNotContain(Lines(Body(reference)), line => line.StartsWith("**Range**", StringComparison.Ordinal));
    }

    // The net deals no damage at all; a weapon without a damage object gets no Damage line rather than an empty one.
    [Fact]
    public void Body_WeaponWithoutDamage_HasNoDamageLine()
    {
        Assert.DoesNotContain(Lines(Body("2014/equipment/net")), line => line.StartsWith("**Damage**", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014/equipment/plate-armor", "**Armor Class** 18")]
    [InlineData("2014/equipment/plate-armor", "**Minimum Strength** 15")]
    [InlineData("2014/equipment/plate-armor", "**Stealth** Disadvantage")]
    [InlineData("2014/equipment/plate-armor", "**Cost** 1,500 gp")]
    [InlineData("2014/equipment/plate-armor", "**Weight** 65 lb.")]
    // 2024 heavy armor carries max_bonus 0 beside dex_bonus false: still a flat 18, not "18 + Dex modifier (max 0)".
    [InlineData("2024/equipment/plate-armor", "**Armor Class** 18")]
    [InlineData("2024/equipment/plate-armor", "**Minimum Strength** 15")]
    [InlineData("2024/equipment/plate-armor", "**Don** 10 minutes · **Doff** 5 minutes")]
    [InlineData("2014/equipment/leather-armor", "**Armor Class** 11 + Dex modifier")]
    [InlineData("2024/equipment/leather-armor", "**Armor Class** 11 + Dex modifier")]
    [InlineData("2014/equipment/half-plate-armor", "**Armor Class** 15 + Dex modifier (max 2)")]
    [InlineData("2024/equipment/half-plate-armor", "**Armor Class** 15 + Dex modifier (max 2)")]
    [InlineData("2024/equipment/half-plate-armor", "**Don** 5 minutes · **Doff** 1 minute")]
    // A shield's base 2 is a bonus to the wearer's AC, not an AC of 2.
    [InlineData("2014/equipment/shield", "**Armor Class** +2")]
    [InlineData("2024/equipment/shield", "**Armor Class** +2")]
    public void Body_Armor_StatesArmorClassAndRequirementsInTheSrdsNotation(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, Lines(Body(reference)));
    }

    // str_minimum 0 and stealth_disadvantage false mean "no requirement" and "no penalty"; neither is worth a line.
    [Theory]
    [InlineData("2014/equipment/shield")]
    [InlineData("2024/equipment/shield")]
    [InlineData("2014/equipment/leather-armor")]
    [InlineData("2024/equipment/studded-leather-armor")]
    public void Body_ArmorWithoutRequirementOrPenalty_HasNoStrengthOrStealthLine(string reference)
    {
        var lines = Lines(Body(reference));

        Assert.DoesNotContain(lines, line => line.StartsWith("**Minimum Strength**", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("**Stealth**", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014/equipment/explorers-pack",
        "**Contents** Backpack (`2014/equipment/backpack`), Bedroll (`2014/equipment/bedroll`), Mess Kit (`2014/equipment/mess-kit`), Tinderbox (`2014/equipment/tinderbox`), 10 × Torch (`2014/equipment/torch`), 10 × Rations (1 day) (`2014/equipment/rations-1-day`), Waterskin (`2014/equipment/waterskin`), Rope, hempen (50 feet) (`2014/equipment/rope-hempen-50-feet`)")]
    [InlineData("2024/equipment/explorers-pack",
        "**Contents** Backpack (`2024/equipment/backpack`), Bedroll (`2024/equipment/bedroll`), 2 × Oil (`2024/equipment/oil`), 10 × Rations (`2024/equipment/rations`), Rope (`2024/equipment/rope`), Tinderbox (`2024/equipment/tinderbox`), 10 × Torch (`2024/equipment/torch`), Waterskin (`2024/equipment/waterskin`)")]
    [InlineData("2024/equipment/explorers-pack", "**Weight** 55 lb.")]
    [InlineData("2024/equipment/explorers-pack",
        "An Explorer’s Pack contains the following items: Backpack, Bedroll, 2 flasks of Oil, 10 days of Rations, Rope, Tinderbox, 10 Torches, and Waterskin.")]
    [InlineData("2014/equipment/warhorse", "**Speed** 60 ft.")]
    [InlineData("2014/equipment/warhorse", "**Carrying Capacity** 540 lb.")]
    [InlineData("2014/equipment/warhorse", "**Cost** 400 gp")]
    [InlineData("2014/equipment/horse-riding", "**Speed** 60 ft.")]
    [InlineData("2014/equipment/galley", "**Speed** 4 mph")]
    [InlineData("2014/equipment/galley", "**Cost** 30,000 gp")]
    [InlineData("2014/equipment/keelboat", "**Speed** 1 mph")]
    [InlineData("2024/equipment/alchemists-supplies", "**Ability** INT (`2024/ability-score/int`)")]
    [InlineData("2024/equipment/alchemists-supplies", "**Utilize** Identify a substance (DC 15); Start a fire (DC 15)")]
    [InlineData("2024/equipment/alchemists-supplies",
        "**Craft** Acid (`2024/equipment/acid`), Alchemist's Fire (`2024/equipment/alchemists-fire`), Component Pouch (`2024/equipment/component-pouch`), Oil (`2024/equipment/oil`), Paper (`2024/equipment/paper`), Perfume (`2024/equipment/perfume`)")]
    [InlineData("2014/equipment/arrow", "**Quantity** 20")]
    [InlineData("2014/equipment/blowgun-needle", "**Quantity** 50")]
    [InlineData("2024/equipment/arrows", "**Quantity** 20")]
    [InlineData("2024/equipment/arrows", "**Storage** Quiver (`2024/equipment/quiver`)")]
    [InlineData("2014/equipment/lance",
        "***Special.*** You have disadvantage when you use a lance to attack a target within 5 feet of you. Also, a lance requires two hands to wield when you aren't mounted.")]
    public void Body_GearPacksMountsAndTools_StateTheirOwnFields(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, Lines(Body(reference)));
    }

    // The priest's pack's alms box costs 0 in the data: it has no price of its own, and "0 gp" would say it is free.
    // A weight of 0 is the SRD's "—".
    [Theory]
    [InlineData("2014/equipment/alms-box", "**Cost**")]
    [InlineData("2014/equipment/vestments", "**Cost**")]
    [InlineData("2014/equipment/playing-card-set", "**Weight**")]
    [InlineData("2014/equipment/warhorse", "**Weight**")]
    public void Body_ZeroOrMissingCostOrWeight_HasNoLineForIt(string reference, string label)
    {
        var body = Body(reference);

        Assert.DoesNotContain(Lines(body), line => line.StartsWith(label, StringComparison.Ordinal));
        Assert.DoesNotContain(" 0 gp", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" 0 lb.", body, StringComparison.Ordinal);
    }

    // The 2024 rope description separates its two paragraphs with "\n " (a newline and a space).
    [Fact]
    public void Body_2024DescriptionWithSingleNewlines_BecomesSeparateParagraphs()
    {
        var body = Body("2024/equipment/rope");

        Assert.Contains("(Athletics) check.\n\nYou can bind an unwilling creature", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/magic-item/flame-tongue", "*Weapon (any sword), rare (requires attunement)*")]
    [InlineData("2014/magic-item/bag-of-holding", "*Wondrous item, uncommon*")]
    [InlineData("2014/magic-item/belt-of-giant-strength", "*Wondrous item, rarity varies (requires attunement)*")]
    // 2024 keeps only the type in desc; rarity and attunement come from their fields, printed as SRD 5.2.1 prints them.
    [InlineData("2024/magic-item/flame-tongue", "*Weapon (Any Melee Weapon), Rare (Requires Attunement)*")]
    [InlineData("2024/magic-item/bag-of-holding", "*Wondrous Item, Uncommon*")]
    [InlineData("2024/magic-item/belt-of-giant-strength", "*Wondrous Item, Rarity Varies (Requires Attunement)*")]
    [InlineData("2024/magic-item/armor-1", "*Armor (Any Light, Medium, or Heavy), Rare*")]
    [InlineData("2024/magic-item/holy-avenger", "*Weapon (Any Simple or Martial), Legendary (Requires Attunement by a Paladin)*")]
    [InlineData("2024/magic-item/wand-of-fireballs", "*Wand, Rare (Requires Attunement by a Spellcaster)*")]
    [InlineData("2024/magic-item/staff-of-healing", "*Staff, Rare (Requires Attunement by a Bard, Cleric, or Druid)*")]
    public void Body_MagicItem_StartsWithTheSrdTypeLine(string reference, string expectedFirstLine)
    {
        Assert.Equal(expectedFirstLine, Lines(Body(reference))[0]);
    }

    // The type line comes from desc[0] (2014) or desc's first line (2024) for every item, and is not repeated as text.
    // A corrected record may carry the SRD's own italic line ("*Wondrous Item, Rare*"); it is shown once, in italics once.
    [Theory]
    [InlineData(SrdEdition.Edition2014)]
    [InlineData(SrdEdition.Edition2024)]
    public void Body_EveryMagicItem_ShowsItsTypeLineOnceAsTheSubtitle(string edition)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.MagicItem))
        {
            var rawTypeLine = edition == SrdEdition.Edition2014
                ? doc.Root.GetProperty("desc")[0].GetString()!.Trim()
                : doc.Root.GetProperty("desc").GetString()!.TrimStart().Split('\n')[0].Trim();
            var typeLine = rawTypeLine.Trim('*', '_');
            var body = Body(doc);

            Assert.True(Lines(body)[0].StartsWith("*" + typeLine, StringComparison.Ordinal), $"{doc.Ref}: first line is \"{Lines(body)[0]}\".");
            Assert.False(Lines(body)[0].StartsWith("**", StringComparison.Ordinal), $"{doc.Ref}: first line is \"{Lines(body)[0]}\".");
            Assert.DoesNotContain(Lines(body).Skip(1), line => line == typeLine || line == rawTypeLine);
        }
    }

    // What a curated correction (content/srd-corrections.json) puts in a 2024 desc: either upstream's shape (the bare type,
    // then text) or the SRD 5.2 markdown's own italic line with rarity and attunement already in it. Either way the item
    // reads as the SRD prints it: the complete line once, never "Rare (Requires Attunement), Rare (Requires Attunement)".
    [Theory]
    [InlineData("""{"desc":"*Wondrous Item, Rarity Varies (Requires Attunement)*\n\nWhile wearing this belt, your Strength changes.\n\n| Belt | Str. | Rarity |\n|---|---|---|\n| Belt of Giant Strength (hill) | 21 | Rare |\n| Belt of Giant Strength (storm) | 29 | Legendary |","rarity":{"name":"Rarity Varies"},"attunement":true}""",
        "*Wondrous Item, Rarity Varies (Requires Attunement)*\n\nWhile wearing this belt, your Strength changes.\n\n| Belt | Str. | Rarity |\n|---|---|---|\n| Belt of Giant Strength (hill) | 21 | Rare |\n| Belt of Giant Strength (storm) | 29 | Legendary |")]
    [InlineData("""{"desc":"Staff, Rare (Requires Attunement by a Druid)\n\nText.","rarity":{"name":"Rare"},"attunement":true,"limited-to":"Druid"}""",
        "*Staff, Rare (Requires Attunement by a Druid)*\n\nText.")]
    [InlineData("""{"desc":"_Potion, Uncommon_\n\nWhen you drink this potion, you gain 10 Temporary Hit Points.","rarity":{"name":"Uncommon"}}""",
        "*Potion, Uncommon*\n\nWhen you drink this potion, you gain 10 Temporary Hit Points.")]
    [InlineData("""{"desc":"**Wondrous Item, Rare**\n\nText.","rarity":{"name":"Rare"}}""", "*Wondrous Item, Rare*\n\nText.")]
    // A line that states attunement is complete even when the rarity field says something else or nothing.
    [InlineData("""{"desc":"Wondrous Item, Artifact (Requires Attunement)\n\nText.","attunement":true}""",
        "*Wondrous Item, Artifact (Requires Attunement)*\n\nText.")]
    // Text that starts with a blank line still has its type line first.
    [InlineData("""{"desc":"\n*Potion, Common*\n\nText.","rarity":{"name":"Common"}}""", "*Potion, Common*\n\nText.")]
    [InlineData("""{"desc":"Wondrous Item\nWhile wearing this belt, your Strength changes.\n| Belt | Str. |\n|---|---|\n| Hill | 21 |\nThe item has no effect.","rarity":{"name":"Rarity Varies"},"attunement":true}""",
        "*Wondrous Item, Rarity Varies (Requires Attunement)*\n\nWhile wearing this belt, your Strength changes.\n\n| Belt | Str. |\n|---|---|\n| Hill | 21 |\n\nThe item has no effect.")]
    public void Body_2024MagicItemWithCorrectedText_ShowsTheCompleteTypeLineOnceAndTheTablesWhole(string json, string expectedBody)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.MagicItem, Slug = "belt-x", Name = "Belt X", Json = json };

        Assert.Equal(expectedBody, SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    [Fact]
    public void Body_2014MagicItemWithText_KeepsEachParagraph()
    {
        var body = Body("2014/magic-item/bag-of-holding");

        Assert.Equal(
            "*Wondrous item, uncommon*\n\n" +
            "**Category** Wondrous Items (`2014/equipment-category/wondrous-items`)\n\n" +
            "This bag has an interior space considerably larger than its outside dimensions, roughly 2 feet in diameter at the mouth and 4 feet deep. The bag can hold up to 500 pounds, not exceeding a volume of 64 cubic feet. The bag weighs 15 pounds, regardless of its contents. Retrieving an item from the bag requires an action.\n\n" +
            "If the bag is overloaded, pierced, or torn, it ruptures and is destroyed, and its contents are scattered in the Astral Plane. If the bag is turned inside out, its contents spill forth, unharmed, but the bag must be put right before it can be used again. Breathing creatures inside the bag can survive up to a number of minutes equal to 10 divided by the number of creatures (minimum 1 minute), after which time they begin to suffocate.\n\n" +
            "Placing a bag of holding inside an extradimensional space created by a Handy Haversack, Portable Hole, or similar item instantly destroys both items and opens a gate to the Astral Plane. The gate originates where the one item was placed inside the other. Any creature within 10 feet of the gate is sucked through it to a random location on the Astral Plane. The gate then closes. The gate is one-way only and can't be reopened.",
            body);
    }

    // Every 2024 magic item pads its line breaks as "  \n "; the padding must not survive into the output.
    [Fact]
    public void Body_2024MagicItem_SplitsPaddedLinesIntoCleanParagraphs()
    {
        var body = Body("2024/magic-item/bag-of-holding");

        Assert.Contains(
            "Retrieving an item from the bag requires a Utilize action.\n\nIf the bag is overloaded, pierced, or torn",
            body, StringComparison.Ordinal);
        Assert.Contains(
            "divided by the number of breathing creatures inside.\n\nPlacing a Bag of Holding inside",
            body, StringComparison.Ordinal);
    }

    // 2014 puts each table row in its own desc element; joined with blank lines they stop being a table.
    [Theory]
    [InlineData("2014/magic-item/belt-of-giant-strength",
        "| Type | Strength | Rarity |\n|---|---|---|\n| Hill Giant | 21 | Rare |\n| Stone Giant / Frost Giant | 23 | Very Rare |\n| Fire Giant | 25 | Very Rare |\n| Cloud Giant | 27 | Legendary |\n| Storm Giant | 29 | Legendary |")]
    [InlineData("2014/magic-item/potion-of-healing",
        "Potions of Healing (table)\n\n| Potion of ... | Rarity | HP Regained |\n|---|---|---|\n| Healing | Common | 2d4 + 2 |")]
    public void Body_2014MagicItemTable_KeepsItsRowsTogether(string reference, string expectedTable)
    {
        Assert.Contains(expectedTable, Body(reference), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/magic-item/belt-of-giant-strength",
        "**Variants** Belt of Hill Giant Strength (`2014/magic-item/belt-of-giant-strength-hill`), Belt of Stone Giant Strength (`2014/magic-item/belt-of-giant-strength-stone`), Belt of Frost Giant Strength (`2014/magic-item/belt-of-giant-strength-frost`), Belt of Fire Giant Strength (`2014/magic-item/belt-of-giant-strength-fire`), Belt of Cloud Giant Strength (`2014/magic-item/belt-of-giant-strength-cloud`), Belt of Storm Giant Strength (`2014/magic-item/belt-of-giant-strength-storm`)")]
    [InlineData("2024/magic-item/armor",
        "**Variants** Armor +1 (`2024/magic-item/armor-1`), Armor +2 (`2024/magic-item/armor-2`), Armor +3 (`2024/magic-item/armor-3`)")]
    [InlineData("2014/magic-item/belt-of-giant-strength-hill",
        "**Variant of** Belt of Giant Strength (`2014/magic-item/belt-of-giant-strength`)")]
    [InlineData("2014/magic-item/potion-of-healing-greater", "**Variant of** Potion of Healing (`2014/magic-item/potion-of-healing`)")]
    [InlineData("2014/magic-item/armor-1", "**Variant of** Armor, +1, +2, or +3 (`2014/magic-item/armor`)")]
    [InlineData("2024/magic-item/armor-1", "**Variant of** Armor (`2024/magic-item/armor`)")]
    [InlineData("2024/magic-item/horn-of-valhalla-1", "**Variant of** Horn of Valhalla (`2024/magic-item/horn-of-valhalla`)")]
    [InlineData("2024/magic-item/wand-of-the-war-mage-2", "**Variant of** Wand of the War Mage (`2024/magic-item/wand-of-the-war-mage`)")]
    [InlineData("2014/magic-item/flame-tongue", "**Category** Weapon (`2014/equipment-category/weapon`)")]
    [InlineData("2024/magic-item/flame-tongue", "**Category** Weapons (`2024/equipment-category/weapons`)")]
    public void Body_MagicItem_LinksCategoryVariantsAndVariantParent(string reference, string expectedLine)
    {
        Assert.Contains(expectedLine, Lines(Body(reference)));
    }

    // "armor" exists and armor-of-resistance's slug starts with it, but armor's variants are armor-1..3: a prefix is only
    // a candidate, and only the candidate's own variants list makes it the parent.
    [Theory]
    [InlineData("2014/magic-item/armor-of-resistance")]
    [InlineData("2024/magic-item/armor-of-resistance")]
    [InlineData("2024/magic-item/crystal-ball-of-telepathy")]
    [InlineData("2024/magic-item/weapon-of-warning")]
    public void Body_MagicItemWhoseSlugPrefixIsAnotherItem_IsNotCalledItsVariant(string reference)
    {
        Assert.DoesNotContain(Lines(Body(reference)), line => line.StartsWith("**Variant of**", StringComparison.Ordinal));
    }

    // Upstream links parents to variants only; every listed variant must get its way back, and nothing else must.
    [Theory]
    [InlineData(SrdEdition.Edition2014, 123)]
    [InlineData(SrdEdition.Edition2024, 19)]
    public void Body_EveryListedVariant_LinksBackToTheParentThatListsIt(string edition, int expectedVariants)
    {
        var items = VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.MagicItem);
        var parentOf = items
            .SelectMany(parent => parent.Root.GetProperty("variants").EnumerateArray()
                .Select(v => (Child: v.GetProperty("index").GetString()!, Parent: parent)))
            .ToDictionary(p => p.Child, p => p.Parent);

        var linked = 0;
        foreach (var doc in items)
        {
            var line = Lines(Body(doc)).SingleOrDefault(l => l.StartsWith("**Variant of**", StringComparison.Ordinal));
            if (parentOf.TryGetValue(doc.Slug, out var parent))
            {
                Assert.Equal($"**Variant of** {parent.Name} (`{parent.Ref}`)", line);
                linked++;
            }
            else
            {
                Assert.Null(line);
            }
        }

        Assert.Equal(expectedVariants, linked);
    }

    [Theory]
    [InlineData("2014/equipment-category/martial-melee-weapons",
        "**Items (18)** Battleaxe (`2014/equipment/battleaxe`), Flail (`2014/equipment/flail`), Glaive (`2014/equipment/glaive`), Greataxe (`2014/equipment/greataxe`), Greatsword (`2014/equipment/greatsword`), Halberd (`2014/equipment/halberd`), Lance (`2014/equipment/lance`), Longsword (`2014/equipment/longsword`), Maul (`2014/equipment/maul`), Morningstar (`2014/equipment/morningstar`), Pike (`2014/equipment/pike`), Rapier (`2014/equipment/rapier`), Scimitar (`2014/equipment/scimitar`), Shortsword (`2014/equipment/shortsword`), Trident (`2014/equipment/trident`), War pick (`2014/equipment/war-pick`), Warhammer (`2014/equipment/warhammer`), Whip (`2014/equipment/whip`)")]
    [InlineData("2024/equipment-category/shields", "**Items (1)** Shield (`2024/equipment/shield`)")]
    public void Body_EquipmentCategory_ListsEveryItemAsARef(string reference, string expectedBody)
    {
        Assert.Equal(expectedBody, Body(reference));
    }

    // Upstream lists 25 mounts-and-vehicles entries twice (every barding, the saddles, cart, wagon…) and four standard-gear
    // entries twice (hourglass, hunting trap, ink bottle, ink pen). The count is a fact the body states, so it is the
    // number of distinct items, and each item is listed once.
    [Theory]
    [InlineData("2014/equipment-category/mounts-and-vehicles", 40)]
    [InlineData("2014/equipment-category/standard-gear", 90)]
    public void Body_EquipmentCategoryListingAnItemTwice_CountsAndListsItOnce(string reference, int distinctItems)
    {
        var body = Body(reference);
        var refs = Regex.Matches(body, "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();

        Assert.StartsWith($"**Items ({distinctItems})** ", body, StringComparison.Ordinal);
        Assert.Equal(distinctItems, refs.Count);
        Assert.Equal(refs.Count, refs.Distinct().Count());
    }

    [Theory]
    [InlineData(SrdEdition.Edition2014)]
    [InlineData(SrdEdition.Edition2024)]
    public void Body_EveryEquipmentCategory_CountsEachDistinctItemOnce(string edition)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, SrdKinds.EquipmentCategory))
        {
            var distinct = doc.Root.GetProperty("equipment").EnumerateArray()
                .Select(e => e.GetProperty("url").GetString())
                .Distinct()
                .Count();
            var body = Body(doc);
            var refs = Regex.Matches(body, "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();

            if (distinct == 0)
            {
                Assert.Equal(SrdMarkdownText.NoDescription, body);
                continue;
            }

            Assert.StartsWith($"**Items ({distinct})** ", body, StringComparison.Ordinal);
            Assert.Equal(distinct, refs.Count);
            Assert.Equal(refs.Count, refs.Distinct().Count());
        }
    }

    // The largest category: one ref per item, magic items included, on one line (2024 Wondrous Items adds a note below it).
    [Theory]
    [InlineData("2014/equipment-category/wondrous-items", 177, "`2014/magic-item/")]
    [InlineData("2024/equipment-category/wondrous-items", 133, "`2024/magic-item/")]
    [InlineData("2014/equipment-category/weapon", 67, "`2014/")]
    public void Body_LargeEquipmentCategory_ShowsEveryItemsRef(string reference, int expectedItems, string refPrefix)
    {
        var items = Body(reference).Split("\n\n")[0];

        Assert.StartsWith($"**Items ({expectedItems})** ", items, StringComparison.Ordinal);
        Assert.Equal(expectedItems, Regex.Count(items, Regex.Escape(refPrefix)));
        Assert.DoesNotContain('\n', items);
    }

    [Theory]
    [InlineData("2024/poison/serpent-venom",
        "**Type** Injury\n**Price per Dose** 200 gp\n\nA creature subjected to Serpent Venom must succeed on a DC 11 Constitution saving throw, taking 10 (3d6) Poison damage on a failed save or half as much damage on a successful one.")]
    [InlineData("2024/poison/assassins-blood",
        "**Type** Ingested\n**Price per Dose** 150 gp\n\nA creature subjected to Assassin's Blood makes a DC 10 Constitution saving throw. On a failed save, the creature takes 6 (1d12) Poison damage and has the Poisoned condition for 24 hours. On a successful save, the creature takes half as much damage only.")]
    [InlineData("2024/weapon-mastery/sap",
        "If you hit a creature with this weapon, that creature has Disadvantage on its next attack roll before the start of your next turn.")]
    [InlineData("2014/weapon-property/finesse",
        "When making an attack with a finesse weapon, you use your choice of your Strength or Dexterity modifier for the attack and damage rolls. You must use the same modifier for both rolls.")]
    [InlineData("2024/weapon-property/finesse",
        "When making an attack with a Finesse weapon, use your choice of your Strength or Dexterity modifier for the attack and damage rolls. You must use the same modifier for both rolls.")]
    public void Body_PoisonMasteryAndProperty_IsItsFieldsThenItsText(string reference, string expectedBody)
    {
        Assert.Equal(expectedBody, Body(reference));
    }

    [Fact]
    public void Body_2014WeaponPropertyWithTwoParagraphs_KeepsThemApart()
    {
        var body = Body("2014/weapon-property/ammunition");

        Assert.Contains("(you need a free hand to load a one-handed weapon).\n\nAt the end of the battle", body, StringComparison.Ordinal);
    }

    // No table row of any record ends up separated from the next row by a blank line, and no line keeps upstream's
    // leading/trailing padding; either would change how the text reads.
    [Theory]
    [MemberData(nameof(OwnedEditionKinds))]
    public void Body_EveryDocumentOfOwnedKind_KeepsTablesWholeAndLinesUnpadded(string edition, string kind)
    {
        foreach (var doc in VendoredSrdLookup.Instance.OfKind(edition, kind))
        {
            var body = Body(doc);

            Assert.False(Regex.IsMatch(body, @"\|[ \t]*\n[ \t]*\n[ \t]*\|"), $"{doc.Ref}: a table is split by a blank line.");
            Assert.DoesNotContain(Lines(body), line => line.Length > 0 && (char.IsWhiteSpace(line[0]) || char.IsWhiteSpace(line[^1])));
        }
    }

    public static TheoryData<string, string> OwnedEditionKinds()
    {
        var data = new TheoryData<string, string>();
        foreach (var edition in SrdEdition.All)
        {
            foreach (var kind in OwnedKinds.Where(k => SrdKinds.ExistsIn(k, edition)))
            {
                data.Add(edition, kind);
            }
        }

        return data;
    }

    // Synthetic records with missing or mistyped fields: each field's line is dropped, the body is never empty, and
    // nothing throws (a formatter exception reaches the model as the SDK's bare generic error).
    [Theory]
    [InlineData(SrdKinds.Equipment, """{"index":"x","name":"X"}""", SrdMarkdownText.NoDescription)]
    [InlineData(SrdKinds.Equipment, """{"cost":"15 gp","weight":"3","armor_class":{"base":"18"},"damage":{"damage_dice":7},"properties":"versatile","range":[5],"speed":{"quantity":"fast"},"contents":[{"quantity":2}],"utilize":[{"dc":{"dc_value":15}}]}""", SrdMarkdownText.NoDescription)]
    [InlineData(SrdKinds.Equipment, """{"cost":{"quantity":2},"weight":0.5,"damage":{"damage_dice":"1d6"},"armor_class":{"base":14,"dex_bonus":true,"max_bonus":0},"speed":{"quantity":30}}""",
        "**Damage** 1d6\n**Armor Class** 14 + Dex modifier\n**Speed** 30\n**Cost** 2\n**Weight** 1/2 lb.")]
    // Every vendored item has equipment_categories; the single equipment_category is the fallback for one that does not.
    [InlineData(SrdKinds.Equipment, """{"equipment_category":{"index":"weapon","name":"Weapon","url":"/api/2014/equipment-categories/weapon"},"equipment_categories":[]}""",
        "**Category** Weapon (`2014/equipment-category/weapon`)")]
    [InlineData(SrdKinds.EquipmentCategory, """{"equipment":{}}""", SrdMarkdownText.NoDescription)]
    [InlineData(SrdKinds.MagicItem, """{"desc":5,"variants":"x","rarity":"Rare"}""", SrdMarkdownText.NoDescription)]
    [InlineData(SrdKinds.MagicItem, """{"desc":"Just one line of text","rarity":{"name":"Rare"}}""", "*Rare*\n\nJust one line of text")]
    [InlineData(SrdKinds.MagicItem, """{"desc":[]}""", SrdMarkdownText.NoDescription)]
    [InlineData(SrdKinds.Poison, """{"type":7,"cost":"cheap","description":["a","b"]}""", "a\n\nb")]
    [InlineData(SrdKinds.WeaponMastery, """{"description":null}""", SrdMarkdownText.NoDescription)]
    public void Body_RecordWithMissingOrMistypedFields_DropsThoseLinesWithoutThrowing(string kind, string json, string expectedBody)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = kind, Slug = "x", Name = "X", Json = json };

        Assert.Equal(expectedBody, SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    // limited-to is always paired with attunement in the data; the article follows the noun, and a restriction without
    // attunement is still stated.
    [Theory]
    [InlineData("""{"desc":"Ring  \n Text","rarity":{"name":"Rare"},"attunement":true,"limited-to":"Elf"}""",
        "*Ring, Rare (Requires Attunement by an Elf)*\n\nText")]
    [InlineData("""{"desc":"Ring  \n Text","rarity":{"name":"Rare"},"attunement":false,"limited-to":"Druid"}""",
        "*Ring, Rare*\n\n**Limited to** Druid\n\nText")]
    public void Body_2024MagicItemAttunementRestriction_IsStated(string json, string expectedBody)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.MagicItem, Slug = "ring-x", Name = "Ring X", Json = json };

        Assert.Equal(expectedBody, SrdMarkdown.Body(doc, VendoredSrdLookup.Instance));
    }

    [Theory]
    // Upstream has no 2024 Rods or Scrolls category and files these under Wondrous Items; "Category: Wondrous Items"
    // beside a "Rod, …" type line contradicted it, and compared with 2014's "Rod" read as a rules change.
    [InlineData("2024/magic-item/rod-of-lordly-might", "*Rod, Legendary (Requires Attunement)*")]
    [InlineData("2024/magic-item/immovable-rod", "*Rod, Uncommon*")]
    [InlineData("2024/magic-item/spell-scroll", "*Scroll, Rarity Varies*")]
    public void Body_2024RodOrScrollFiledAsWondrous_HasItsTypeLineAndNoContradictingCategory(string reference, string typeLine)
    {
        var lines = Lines(Body(reference));

        Assert.Equal(typeLine, lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("**Category**", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_2024WondrousItem_StillShowsItsCategory()
    {
        Assert.Contains("**Category** Wondrous Items (`2024/equipment-category/wondrous-items`)", Lines(Body("2024/magic-item/bag-of-holding")));
    }

    [Fact]
    public void Body_2024WondrousItemsCategory_SaysWhichListedItemsAreRodsAndScrolls()
    {
        // The count is a fact the body states; 133 read as the SRD's number of wondrous items, which is 126.
        var body = Body("2024/equipment-category/wondrous-items");

        Assert.Contains(
            "\n\n*Of these, 6 Rods (Immovable Rod, Rod of Absorption, Rod of Alertness, Rod of Lordly Might, Rod of Rulership, Rod " +
            "of Security) and 1 Scroll (Spell Scroll) are filed here by the data; their own type lines, and SRD 5.2.1, put them in " +
            "categories of their own.*",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Of these", Body("2014/equipment-category/wondrous-items"), StringComparison.Ordinal);
    }

    private static string Body(string reference) => Body(VendoredSrdLookup.Instance.Require(reference));

    private static string Body(SrdDocument doc) => SrdMarkdown.Body(doc, VendoredSrdLookup.Instance);

    private static string[] Lines(string body) => body.Split('\n');
}
