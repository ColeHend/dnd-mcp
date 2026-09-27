using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: a spell renders its full description unchanged, with each SRD's header lines and higher-level heading,
/// and the structured damage/healing/save lines shown only where the data has them, exactly as stored.
///
/// <para>
/// The pinned lines are hand-checked against the vendored records. They cover the shapes that differ by edition
/// (2014 damage arrays with slot and character-level tables, 2024 single damage objects keyed "0" for cantrips),
/// upstream's odd dice strings ("1d8 + MOD", "4d6 OR 5d6", flat numbers), the nine 2024 spells whose components were
/// scraped into the range, and 2014 descriptions that store one table row or list item per array element.
/// </para>
/// </summary>
public sealed partial class SpellMarkdownTests
{
    private static VendoredSrdLookup Lookup => VendoredSrdLookup.Instance;

    [Theory]
    // Fireball, 2014: subclasses, the slot table and the save. No area after a non-Self range (SRD 5.1 prints none
    // there; the description states it).
    [InlineData("2014/spell/fireball", "*Level 3 Evocation*")]
    [InlineData("2014/spell/fireball", "**Casting Time** 1 action")]
    [InlineData("2014/spell/fireball", "**Range** 150 feet")]
    [InlineData("2014/spell/fireball", "**Components** V, S, M (A tiny ball of bat guano and sulfur.)")]
    [InlineData("2014/spell/fireball", "**Duration** Instantaneous")]
    [InlineData("2014/spell/fireball", "**Classes** Sorcerer (`2014/class/sorcerer`), Wizard (`2014/class/wizard`)")]
    [InlineData("2014/spell/fireball", "**Subclasses** Lore (`2014/subclass/lore`), Fiend (`2014/subclass/fiend`)")]
    [InlineData("2014/spell/fireball", "The fire spreads around corners. It ignites flammable objects in the area that aren't being worn or carried.")]
    [InlineData("2014/spell/fireball", "***At Higher Levels.*** When you cast this spell using a spell slot of 4th level or higher, the damage increases by 1d6 for each slot level above 3rd.")]
    [InlineData("2014/spell/fireball", "**Damage** Fire (`2014/damage-type/fire`) — 8d6 (slot 3), 9d6 (4), 10d6 (5), 11d6 (6), 12d6 (7), 13d6 (8), 14d6 (9)")]
    [InlineData("2014/spell/fireball", "**Save** Dexterity, half on success")]
    // Fireball, 2024: 2024 wording, a newline-separated second paragraph, the 2024 upcast heading, base dice only.
    [InlineData("2024/spell/fireball", "*Level 3 Evocation*")]
    [InlineData("2024/spell/fireball", "**Casting Time** Action")]
    [InlineData("2024/spell/fireball", "**Range** 150 feet")]
    [InlineData("2024/spell/fireball", "**Components** V, S, M (a ball of bat guano and sulfur)")]
    [InlineData("2024/spell/fireball", "Flammable objects in the area that aren't being worn or carried start burning.")]
    [InlineData("2024/spell/fireball", "***Using a Higher-Level Spell Slot.*** The damage increases by 1d6 for each spell slot level above 3.")]
    [InlineData("2024/spell/fireball", "**Damage** Fire (`2024/damage-type/fire`) — 8d6")]
    // Fire bolt: cantrip subtitle, 2014 character-level scaling, 2024 Cantrip Upgrade kept inside the description.
    [InlineData("2014/spell/fire-bolt", "*Evocation cantrip*")]
    [InlineData("2014/spell/fire-bolt", "**Damage** Fire (`2014/damage-type/fire`) — 1d10 (level 1), 2d10 (5), 3d10 (11), 4d10 (17)")]
    [InlineData("2014/spell/fire-bolt", "**Attack** ranged spell attack")]
    [InlineData("2024/spell/fire-bolt", "*Evocation cantrip*")]
    [InlineData("2024/spell/fire-bolt", "Cantrip Upgrade. The damage increases by 1d10 when you reach levels 5 (2d10), 11 (3d10), and 17 (4d10).")]
    [InlineData("2024/spell/fire-bolt", "**Damage** Fire (`2024/damage-type/fire`) — 1d10")]
    [InlineData("2024/spell/fire-bolt", "**Attack** ranged spell attack")]
    // The 2014 table is shown as stored even where it says less than the prose (the beams).
    [InlineData("2014/spell/eldritch-blast", "**Damage** Force (`2014/damage-type/force`) — 1d10 (level 1), 1d10 (5), 1d10 (11), 1d10 (17)")]
    // Rituals and concentration.
    [InlineData("2014/spell/detect-magic", "*Level 1 Divination (ritual)*")]
    [InlineData("2014/spell/detect-magic", "**Range** Self (30-foot sphere)")]
    [InlineData("2014/spell/detect-magic", "**Duration** Concentration, up to 10 minutes")]
    [InlineData("2024/spell/detect-magic", "*Level 1 Divination (ritual)*")]
    [InlineData("2024/spell/detect-magic", "**Duration** Concentration, up to 10 minutes")]
    [InlineData("2014/spell/bless", "**Duration** Concentration, up to 1 minute")]
    [InlineData("2024/spell/bless", "**Duration** Concentration, up to 1 minute")]
    [InlineData("2014/spell/hold-person", "**Save** Wisdom")]
    // "Up to" without the concentration flag stays as stored; a duration that already says so is not doubled.
    [InlineData("2024/spell/thaumaturgy", "**Duration** Up to 1 minute")]
    [InlineData("2024/spell/protection-from-evil-and-good", "**Duration** Concentration up to 10 minutes")]
    // Magic missile and cure wounds: the 2014 structured tables, odd dice strings and all.
    [InlineData("2014/spell/magic-missile", "**Damage** Force (`2014/damage-type/force`) — 3d4 + 3 (slot 1), 4d4 + 4 (2), 5d4 + 5 (3), 6d4 + 6 (4), 7d4 + 7 (5), 8d4 + 8 (6), 9d4 + 9 (7), 10d4 + 10 (8), 11d4 + 11 (9)")]
    [InlineData("2024/spell/magic-missile", "***Using a Higher-Level Spell Slot.*** The spell creates one more dart for each spell slot level above 1.")]
    [InlineData("2014/spell/cure-wounds", "**Healing** 1d8 + MOD (slot 1), 2d8 + MOD (2), 3d8 + MOD (3), 4d8 + MOD (4), 5d8 + MOD (5), 6d8 + MOD (6), 7d8 + MOD (7), 8d8 + MOD (8), 9d8 + MOD (9)")]
    [InlineData("2014/spell/heal", "**Healing** 70 (slot 6), 80 (7), 90 (8), 100 (9)")]
    // False life's table is temporary hit points, which are not healing (they can't bring a creature back from 0).
    [InlineData("2014/spell/false-life", "**Temporary Hit Points** 1d4 + 4 (slot 1), 1d4 + 9 (2), 1d4 + 14 (3), 1d4 + 19 (4), 1d4 + 24 (5), 1d4 + 29 (6), 1d4 + 34 (7), 1d4 + 39 (8), 1d4 + 44 (9)")]
    [InlineData("2014/spell/flame-strike", "**Damage** Fire (`2014/damage-type/fire`) — 4d6 (slot 5), 4d6 OR 5d6 (6), 4d6 OR 6d6 (7), 4d6 OR 7d6 (8), 4d6 OR 8d6 (9)")]
    [InlineData("2014/spell/flame-strike", "**Damage** Radiant (`2014/damage-type/radiant`) — 4d6 (slot 5), 4d6 OR 5d6 (6), 4d6 OR 6d6 (7), 4d6 OR 7d6 (8), 4d6 OR 8d6 (9)")]
    [InlineData("2014/spell/meteor-swarm", "**Damage** Bludgeoning (`2014/damage-type/bludgeoning`) — 20d6")]
    // A damage entry with no damage type is shown without one when the text calls its dice damage.
    [InlineData("2014/spell/prismatic-spray", "**Damage** 10d6")]
    // SRD 5.1 prints an area in the Range line only for Self-range spells, and those match the text.
    [InlineData("2014/spell/lightning-bolt", "**Range** Self (100-foot line)")]
    [InlineData("2014/spell/burning-hands", "**Range** Self (15-foot cone)")]
    // Upstream's area objects for other spells contradict the text (flame strike's is its height, forbiddance's is a
    // floor area in square feet), so the range stands alone.
    [InlineData("2014/spell/flame-strike", "**Range** 60 feet")]
    [InlineData("2014/spell/forbiddance", "**Range** Touch")]
    [InlineData("2014/spell/guards-and-wards", "**Range** Touch")]
    [InlineData("2014/spell/move-earth", "**Range** 120 feet")]
    [InlineData("2014/spell/fire-storm", "**Range** 150 feet")]
    [InlineData("2014/spell/sunburst", "**Range** 150 feet")]
    [InlineData("2014/spell/teleportation-circle", "**Range** 10 feet")]
    [InlineData("2014/spell/mirage-arcane", "**Range** Sight")]
    // Components scraped into the range upstream, moved back.
    [InlineData("2024/spell/guidance", "**Range** Touch")]
    [InlineData("2024/spell/guidance", "**Components** V, S")]
    [InlineData("2024/spell/guidance", "**Duration** Concentration, up to 1 minute")]
    [InlineData("2024/spell/power-word-kill", "**Range** 60 feet")]
    [InlineData("2024/spell/power-word-kill", "**Components** V")]
    [InlineData("2024/spell/shield", "**Casting Time** Reaction, which you take when you are hit by an attack roll or targeted by the Magic Missile spell")]
    public void Body_RealSpell_HasExactLine(string reference, string expectedLine)
    {
        AssertHasLine(Body(reference), expectedLine);
    }

    [Theory]
    // 2024 has no subclasses, dc or area of effect; nothing may stand in for them.
    [InlineData("2024/spell/fireball", "**Subclasses**")]
    [InlineData("2024/spell/fireball", "**Save**")]
    [InlineData("2024/spell/fireball", "(20-foot")]
    // 2024 cantrips keep their upgrade in the description, not under an upcast heading.
    [InlineData("2024/spell/fire-bolt", "***Using a Higher-Level Spell Slot.***")]
    [InlineData("2024/spell/fire-bolt", "(slot")]
    // 2024 magic missile and cure wounds have no damage or healing data at all.
    [InlineData("2024/spell/magic-missile", "**Damage**")]
    [InlineData("2024/spell/cure-wounds", "**Healing**")]
    // A 2014 spell with no damage, save or attack gets none of those lines.
    [InlineData("2014/spell/bless", "**Damage**")]
    [InlineData("2014/spell/bless", "**Save**")]
    [InlineData("2014/spell/bless", "**Attack**")]
    // Not a concentration spell; the ritual note only when the flag is set.
    [InlineData("2024/spell/thaumaturgy", "Concentration")]
    [InlineData("2014/spell/fireball", "(ritual)")]
    // The repaired range must not keep the glued-on components.
    [InlineData("2024/spell/guidance", "Component:")]
    // Sleep's 5d8 is the hit points of creatures it affects, not damage; aid raises the hit point maximum, which is
    // not healing. The prose above states both; a structured line would state the wrong mechanic.
    [InlineData("2014/spell/sleep", "**Damage**")]
    [InlineData("2014/spell/aid", "**Healing**")]
    [InlineData("2014/spell/aid", "**Temporary Hit Points**")]
    [InlineData("2014/spell/false-life", "**Healing**")]
    public void Body_RealSpell_HasNoLineTheDataDoesNotSupport(string reference, string unexpected)
    {
        var body = Body(reference);

        Assert.False(body.Contains(unexpected, StringComparison.Ordinal), $"Did not expect \"{unexpected}\" in:\n{body}");
    }

    [Fact]
    public void Body_2014TableStoredOneRowPerElement_StaysOneMarkdownTableUnderItsHeading()
    {
        // Blank lines between rows would leave five one-row fragments that no longer parse as a table.
        const string expected = "##### Animated Object Statistics\n\n" +
                                "| Size | HP | AC | Attack | Str | Dex |\n" +
                                "|---|---|---|---|---|---|\n" +
                                "| Tiny | 20 | 18 | +8 to hit, 1d4 + 4 damage | 4 | 18 |\n" +
                                "| Small | 25 | 16 | +6 to hit, 1d8 + 2 damage | 6 | 14 |\n" +
                                "| Medium | 40 | 13 | +5 to hit, 2d6 + 1 damage | 10 | 12 |\n" +
                                "| Large | 50 | 10 | +6 to hit, 2d10 + 2 damage | 14 | 10 |\n" +
                                "| Huge | 80 | 10 | +8 to hit, 2d12 + 4 damage | 18 | 6 |\n\n" +
                                "An animated object is a construct";

        Assert.Contains(expected, Body("2014/spell/animate-objects"), StringComparison.Ordinal);
    }

    [Fact]
    public void Body_2014ListStoredOneItemPerElement_StaysOneTightList()
    {
        const string expected = "The GM chooses from the following possible omens:\n\n" +
                                "- Weal, for good results\n" +
                                "- Woe, for bad results\n" +
                                "- Weal and woe, for both good and bad results\n" +
                                "- Nothing, for results that aren't especially good or bad\n\n" +
                                "The spell doesn't take into account";

        Assert.Contains(expected, Body("2014/spell/augury"), StringComparison.Ordinal);
    }

    [Fact]
    public void Body_Spell_OrdersSubtitleHeaderDescriptionUpcastThenMechanics()
    {
        var body = Body("2014/spell/fireball");

        var order = new[]
        {
            "*Level 3 Evocation*",
            "**Casting Time** ",
            "**Subclasses** ",
            "A bright streak flashes",
            "***At Higher Levels.***",
            "**Damage** ",
            "**Save** ",
        }.Select(marker => body.IndexOf(marker, StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order().ToList(), order);
        Assert.StartsWith("*Level 3 Evocation*\n\n**Casting Time** ", body, StringComparison.Ordinal);
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
    public void Body_EverySpell_ShowsEveryDescriptionLineAndClassRefAndConcentration(string edition)
    {
        foreach (var doc in Lookup.OfKind(edition, SrdKinds.Spell))
        {
            var body = Body(doc);
            var lines = body.Split('\n').ToHashSet(StringComparer.Ordinal);

            foreach (var line in doc.Root.Description().SelectMany(p => p.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Assert.True(lines.Contains(line), $"{doc.Ref}: description line not shown as its own line: \"{line}\"");
            }

            foreach (var reference in doc.Root.Arr("classes").Select(c => SrdRef.FromApiUrl(c.Str("url"))))
            {
                Assert.True(reference is not null && body.Contains($"(`{reference}`)", StringComparison.Ordinal),
                    $"{doc.Ref}: class {reference} not linked.");
            }

            var duration = body.Split('\n').Single(l => l.StartsWith("**Duration** ", StringComparison.Ordinal));
            if (doc.Root.Bool("concentration") == true)
            {
                Assert.StartsWith("**Duration** Concentration", duration, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Body_Every2014Spell_ShowsAnAreaInTheRangeLineOnlyForSelfRangeSpells()
    {
        var withArea = 0;
        foreach (var doc in Lookup.OfKind(SrdEdition.Edition2014, SrdKinds.Spell))
        {
            var range = Body(doc).Split('\n').Single(l => l.StartsWith("**Range** ", StringComparison.Ordinal));
            var self = doc.Root.Str("range")!.StartsWith("Self", StringComparison.Ordinal);
            if (self && doc.Root.Obj("area_of_effect") is not null)
            {
                Assert.Matches(@"^\*\*Range\*\* Self \(\d+-foot (?:cone|cube|cylinder|line|sphere)\)$", range);
                withArea++;
            }
            else
            {
                Assert.True(range == $"**Range** {doc.Root.Str("range")}", $"{doc.Ref}: {range}");
            }
        }

        // The 19 Self-range spells with an area; the 69 other spells with one get none.
        Assert.Equal(19, withArea);
    }

    [Theory]
    // No type, and the text never calls the dice damage: not shown (sleep's hit-point pool).
    [InlineData("Roll 5d8; the total is how many hit points of creatures this spell can affect.", null)]
    // No type, but the text says the dice are damage: shown.
    [InlineData("Each creature takes 10d6 fire damage on a failed save.", "**Damage** 10d6")]
    public void Body_UntypedDamageEntry_IsShownOnlyWhenTheTextCallsItsDiceDamage(string description, string? expected)
    {
        var json = $"{{\"level\":1,\"desc\":[\"{description}\"],\"damage\":[{{\"damage_at_slot_level\":{{\"1\":\"{(expected is null ? "5d8" : "10d6")}\"}}}}]}}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2014, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

        var damage = SpellMarkdown.Body(doc, Lookup).Split('\n').Where(l => l.StartsWith("**Damage**", StringComparison.Ordinal)).ToList();

        Assert.Equal(expected is null ? [] : [expected], damage);
    }

    [Theory]
    [InlineData("A creature you touch regains a number of hit points equal to 1d8.", "**Healing** 1d8")]
    [InlineData("You restore up to 700 hit points.", "**Healing** 1d8")]
    [InlineData("You gain 1d8 temporary hit points for the duration.", "**Temporary Hit Points** 1d8")]
    // Neither regained nor temporary hit points (aid's maximum): no structured line; the text says what it is.
    [InlineData("Each target's hit point maximum and current hit points increase by 5.", null)]
    public void Body_HealTable_IsLabelledByWhatTheTextSaysItGives(string description, string? expected)
    {
        var json = $"{{\"level\":1,\"desc\":[\"{description}\"],\"heal_at_slot_level\":{{\"1\":\"1d8\"}}}}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2014, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

        var body = SpellMarkdown.Body(doc, Lookup);
        var lines = body.Split('\n').Where(l => l.StartsWith("**Healing**", StringComparison.Ordinal) ||
                                                l.StartsWith("**Temporary Hit Points**", StringComparison.Ordinal)).ToList();

        Assert.Equal(expected is null ? [] : [expected], lines);
    }

    [Fact]
    public void Body_ScalingTableOutOfOrder_IsShownInNumericOrder()
    {
        // JSON object order is not guaranteed; sorted as text, "11" and "17" would land before "5".
        const string json = "{\"level\":0,\"desc\":[\"The target takes 1d10 fire damage.\"],\"damage\":[{\"damage_at_character_level\":{\"17\":\"4d10\",\"5\":\"2d10\",\"11\":\"3d10\",\"1\":\"1d10\"}}]}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2014, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

        AssertHasLine(SpellMarkdown.Body(doc, Lookup), "**Damage** 1d10 (level 1), 2d10 (5), 3d10 (11), 4d10 (17)");
    }

    [Fact]
    public void Body_SingleScalingEntryAboveTheSpellsLevel_KeepsItsSlot()
    {
        // Only an entry at the spell's own level is "just the dice"; any other slot must say which.
        const string json = "{\"level\":1,\"desc\":[\"The target regains hit points.\"],\"heal_at_slot_level\":{\"3\":\"2d8\"}}";
        var doc = new SrdDocument { Edition = SrdEdition.Edition2014, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

        AssertHasLine(SpellMarkdown.Body(doc, Lookup), "**Healing** 2d8 (slot 3)");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"level\":\"3\",\"school\":\"Evocation\",\"components\":\"V\",\"range\":7,\"damage\":\"8d6\",\"dc\":{\"dc_type\":\"dex\"},\"higher_level\":5,\"desc\":[1,\"text\"],\"classes\":{}}")]
    [InlineData("{\"level\":2,\"damage\":[3,{\"damage_at_slot_level\":{\"x\":\"1d4\",\"2\":4}}],\"heal_at_slot_level\":[],\"area_of_effect\":{\"type\":\"cone\"},\"concentration\":true}")]
    public void Body_MalformedSpellRecord_RendersWithoutThrowing(string json)
    {
        var doc = new SrdDocument { Edition = SrdEdition.Edition2024, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

        _ = SpellMarkdown.Body(doc, Lookup);
    }

    [Theory]
    // Each description deals dice of more damage types than the data's damage entries name (2024 keeps one entry per
    // spell): meteor swarm 20d6 Fire and 20d6 Bludgeoning under "Fire — 20d6", prismatic spray five colours under "Acid".
    // The typed line answered "how much damage" with part of it, and beside 2014's two lines read as a dropped type.
    [InlineData("2024/spell/meteor-swarm")]
    [InlineData("2024/spell/flame-strike")]
    [InlineData("2024/spell/ice-storm")]
    [InlineData("2024/spell/ice-knife")]
    [InlineData("2024/spell/prismatic-spray")]
    [InlineData("2024/spell/prismatic-wall")]
    [InlineData("2024/spell/spirit-guardians")]
    [InlineData("2024/spell/fire-shield")]
    [InlineData("2024/spell/storm-of-vengeance")]
    [InlineData("2024/spell/wall-of-thorns")]
    [InlineData("2014/spell/fire-shield")]
    [InlineData("2014/spell/storm-of-vengeance")]
    [InlineData("2014/spell/wall-of-thorns")]
    public void Body_TypedDamageEntriesNamingFewerTypesThanTheDescription_AreLeftOut(string reference)
    {
        var body = Body(reference);

        Assert.False(TypedDamageLine().IsMatch(body), $"{reference} still has a typed Damage line:\n{body}");
    }

    [Theory]
    // Where the entries name every type the description deals, each is shown.
    [InlineData("2014/spell/meteor-swarm", "**Damage** Fire (`2014/damage-type/fire`) — 20d6")]
    [InlineData("2014/spell/meteor-swarm", "**Damage** Bludgeoning (`2014/damage-type/bludgeoning`) — 20d6")]
    [InlineData("2024/spell/fireball", "**Damage** Fire (`2024/damage-type/fire`) — 8d6")]
    // An untyped entry names no type, so it claims nothing the description contradicts.
    [InlineData("2014/spell/prismatic-spray", "**Damage** 10d6")]
    public void Body_DamageEntriesThatAgreeWithTheDescription_AreShown(string reference, string line)
    {
        AssertHasLine(Body(reference), line);
    }

    [Theory]
    [MemberData(nameof(Editions))]
    public void Body_EverySpell_TypedDamageLinesNameEveryTypeTheDescriptionDealsDiceOf(string edition)
    {
        var typedSpells = 0;
        foreach (var doc in Lookup.OfKind(edition, SrdKinds.Spell))
        {
            var body = Body(doc);
            var shown = TypedDamageLine().Matches(body).Select(m => m.Groups["type"].Value.ToLowerInvariant()).ToHashSet();
            if (shown.Count == 0)
            {
                continue;
            }

            typedSpells++;
            var description = string.Join("\n", doc.Root.Description());
            var dealt = DiceOfAType().Matches(description).Select(m => m.Groups["type"].Value.ToLowerInvariant()).ToHashSet();
            Assert.True(dealt.IsSubsetOf(shown), $"{doc.Ref}: the description deals {string.Join(", ", dealt)}; the Damage lines say {string.Join(", ", shown)}.");
        }

        // The rule must leave the agreeing lines alone: most damaging spells keep theirs.
        Assert.True(typedSpells > 50, $"Only {typedSpells} {edition} spells kept a typed Damage line.");
    }

    // "**Damage** Fire (`2024/damage-type/fire`) — 20d6": a typed damage line and its type.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\*\*Damage\*\* (?<type>[A-Za-z]+) \(`20\d\d/damage-type/", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex TypedDamageLine();

    // "20d6 Fire damage", "2d8 cold damage", "1d4 + 1 Force damage".
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\d+d\d+(?: ?[+-] ?\d+)? (?<type>acid|bludgeoning|cold|fire|force|lightning|necrotic|piercing|poison|psychic|radiant|slashing|thunder) damage",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DiceOfAType();

    private static string Body(string reference) => Body(Lookup.Require(reference));

    private static string Body(SrdDocument doc) => SrdMarkdown.Body(doc, Lookup);

    private static void AssertHasLine(string body, string expectedLine) =>
        Assert.True(body.Split('\n').Contains(expectedLine), $"Expected the line\n{expectedLine}\nin:\n{body}");
}
