using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the shipped <c>content/srd-corrections.json</c>: the curated overlay that replaces provably wrong upstream
/// text with the SRD's own words before srd.db is built.
///
/// <para>
/// Why these tests carry the weight: the index builder skips an entry it cannot apply with only a warning (an installed
/// server keeps serving rules rather than refusing to build over one stale entry), so nothing at runtime stops a
/// correction from silently doing nothing after a re-vendor. Here it fails: every entry must name a record and
/// properties the vendored data has, and must change every property it sets. The pinned facts below are the findings
/// themselves (Potion of Heroism's effect, the Oath of Devotion spells, Hide Armor's category, the Mule's actions); if
/// an edit to the file loses one, the answer the model relays is wrong again, labelled "SRD 5.2.1".
/// </para>
/// <para>
/// The text in the file is copied from the SRD 5.2 markdown (2024 records) or the SRD 5.1 markdown (2014 records), both
/// CC-BY-4.0, with whitespace and markup normalisation only; each place where the printed SRD 5.2.1 differs is listed in
/// the entry's source (content/srd-corrections.md). The file's own rules (a ref corrected once, a correction that sets
/// or adds something, a reason and a source, <c>add</c> only for properties upstream dropped) are pinned with broken
/// entries below, so none of them can be deleted with the suites green.
/// </para>
/// </summary>
public sealed partial class SrdCorrectionsTests
{
    private static SrdCorrections Shipped => CorrectedSrdContent.Corrections;

    [Fact]
    public void Load_ShippedFile_ParsesAndHasTheSha256OfItsBytes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(CorrectedSrdContent.ContentRoot, SrdCorrections.FileName));

        Assert.NotEmpty(Shipped.Entries);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Shipped.Sha256);
    }

    // How many records of each kind the overlay corrects. A change here is a deliberate edit to the file: review it.
    [Theory]
    [InlineData("2024/magic-item", 123)]
    [InlineData("2024/equipment", 44)]
    [InlineData("2024/spell", 49)]
    [InlineData("2024/feature", 24)]
    [InlineData("2024/equipment-category", 6)]
    [InlineData("2024/feat", 2)]
    [InlineData("2024/monster", 3)]
    [InlineData("2024/rule", 1)]
    [InlineData("2024/species", 1)]
    [InlineData("2024/trait", 2)]
    [InlineData("2014/spell", 25)]
    [InlineData("2014/magic-item", 7)]
    [InlineData("2014/monster", 14)]
    public void Entries_PerEditionAndKind_MatchTheReviewedCount(string editionAndKind, int expected)
    {
        Assert.Equal(expected, Shipped.Entries.Count(e => $"{e.Target.Edition}/{e.Target.Kind}" == editionAndKind));
    }

    // GAP-4 / AUD-3: content/srd-corrections.md is the provenance record a reader checks the file against, and it had
    // drifted ("246 entries, all 2024" beside a 2014 entry). Its heading and each edition's kind table must count what
    // the file holds.
    [Fact]
    public void Readme_CountsPerEditionAndKind_MatchTheFile()
    {
        var readme = File.ReadAllText(Path.Combine(CorrectedSrdContent.ContentRoot, "srd-corrections.md"));
        var heading = ReadmeHeading().Match(readme);
        var documented = new Dictionary<string, int>(StringComparer.Ordinal);
        string? edition = null;
        foreach (var line in readme.Split('\n'))
        {
            if (ReadmeEditionHeading().Match(line) is { Success: true } section)
            {
                edition = section.Groups[1].Value;
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                edition = null;
            }
            else if (edition is not null && ReadmeKindRow().Match(line) is { Success: true } row)
            {
                documented[$"{edition}/{row.Groups[1].Value}"] = int.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var actual = Shipped.Entries.GroupBy(e => $"{e.Target.Edition}/{e.Target.Kind}").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.True(heading.Success, "srd-corrections.md has no \"## What is corrected (N entries: A in 2024, B in 2014)\" heading.");
        Assert.Equal(Shipped.Entries.Count, int.Parse(heading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Shipped.Entries.Count(e => e.Target.Edition == SrdEdition.Edition2024), int.Parse(heading.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Shipped.Entries.Count(e => e.Target.Edition == SrdEdition.Edition2014), int.Parse(heading.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(actual.OrderBy(p => p.Key, StringComparer.Ordinal), documented.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Entries_EveryKind_IsOneOfThePinnedKinds()
    {
        string[] pinned =
        [
            "2024/magic-item", "2024/equipment", "2024/spell", "2024/feature", "2024/equipment-category", "2024/feat",
            "2024/monster", "2024/rule", "2024/species", "2024/trait", "2014/spell", "2014/magic-item", "2014/monster",
        ];

        Assert.All(Shipped.Entries, e => Assert.Contains($"{e.Target.Edition}/{e.Target.Kind}", pinned));
    }

    // The builder's contract: a correction replaces properties the record has and adds only properties it lacks. One
    // that names a missing record, sets a missing property or adds an existing one would be skipped at build time with
    // a warning nobody reads.
    [Fact]
    public void Entries_Every_TargetsAnExistingRecordAndChangesEveryPropertyItSetsOrAdds()
    {
        var problems = new List<string>();
        foreach (var entry in Shipped.Entries)
        {
            if (CorrectedSrdContent.Vendored(entry.Target) is not { } record)
            {
                problems.Add($"{entry.Ref}: no such record in the vendored content");
                continue;
            }

            if (entry.Set.Keys.FirstOrDefault(name => !record.TryGetProperty(name, out _)) is { } missing)
            {
                problems.Add($"{entry.Ref}: sets \"{missing}\", which the record does not have");
                continue;
            }

            if (entry.Add.Keys.FirstOrDefault(name => record.TryGetProperty(name, out _)) is { } present)
            {
                problems.Add($"{entry.Ref}: adds \"{present}\", which the record already has");
                continue;
            }

            var corrected = JsonDocument.Parse(Shipped.Apply(entry.Target, record)!).RootElement;
            foreach (var (name, value) in entry.Set)
            {
                if (JsonElement.DeepEquals(record.GetProperty(name), value))
                {
                    problems.Add($"{entry.Ref}: \"{name}\" is set to the value upstream already has");
                }
                else if (!JsonElement.DeepEquals(corrected.GetProperty(name), value))
                {
                    problems.Add($"{entry.Ref}: \"{name}\" is not the corrected value after Apply");
                }
            }

            foreach (var (name, value) in entry.Add)
            {
                if (!corrected.TryGetProperty(name, out var added) || !JsonElement.DeepEquals(added, value))
                {
                    problems.Add($"{entry.Ref}: \"{name}\" is not the added value after Apply");
                }
            }

            // Every other property is upstream's, untouched and in its place; added ones follow, in file order.
            var untouched = record.EnumerateObject().Where(p => !entry.Set.ContainsKey(p.Name)).ToList();
            if (!untouched.All(p => JsonElement.DeepEquals(p.Value, corrected.GetProperty(p.Name))) ||
                !record.EnumerateObject().Select(p => p.Name).Concat(entry.Add.Keys).SequenceEqual(corrected.EnumerateObject().Select(p => p.Name)))
            {
                problems.Add($"{entry.Ref}: Apply changed a property the correction does not set or add");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // The reason is printed on every corrected document ("*Corrected from the upstream data: <reason>*"), so it must
    // say what upstream got wrong, in one sentence; the source must let a reviewer find the replacement text.
    [Fact]
    public void Entries_Every_HasAOneSentenceReasonAndACitedSource()
    {
        var problems = Shipped.Entries
            .SelectMany(e => new[]
            {
                e.Reason.StartsWith("Upstream ", StringComparison.Ordinal) ? null : $"{e.Ref}: reason should start \"Upstream \"",
                e.Reason.EndsWith('.') ? null : $"{e.Ref}: reason should end with a period",
                e.Reason.Contains('\n') || e.Reason.Length > 300 ? $"{e.Ref}: reason should be one short line" : null,
                CitedSource().IsMatch(e.Source) ? null : $"{e.Ref}: source \"{e.Source}\" names no SRD file and heading",
            })
            .OfType<string>()
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // A reason is printed on the document as what upstream got wrong, so every string it quotes must be real: upstream's
    // text (the damage) or the replacement's (the SRD's words). A quote that is neither (AUD-7: "anddoesnt" for upstream's
    // "anddoesn't") misreports the record. Quotes split at "…" are checked piece by piece; "M ((…" quotes how a damaged
    // material used to display, not stored text.
    [Fact]
    public void Entries_EveryQuoteInAReason_IsUpstreamsTextOrTheReplacements()
    {
        var problems = new List<string>();
        foreach (var entry in Shipped.Entries)
        {
            var upstream = Normalise(string.Join("\n", Strings(CorrectedSrdContent.Vendored(entry.Target)!.Value)));
            var replacement = Normalise(string.Join("\n", entry.Set.Values.Concat(entry.Add.Values).SelectMany(Strings)));
            foreach (Match quote in Quote().Matches(entry.Reason))
            {
                foreach (var piece in quote.Groups[1].Value.Split('…', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!piece.StartsWith("M (", StringComparison.Ordinal) &&
                        !upstream.Contains(Normalise(piece), StringComparison.Ordinal) &&
                        !replacement.Contains(Normalise(piece), StringComparison.Ordinal))
                    {
                        problems.Add($"{entry.Ref}: reason quotes \"{piece}\", which is neither upstream's text nor the replacement's");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        static string Normalise(string text) => string.Join(' ', text.Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // SrdProse renders a table only when its rows are consecutive "|" lines with a header and a separator; a row with
    // a different cell count renders as a broken table.
    [Fact]
    public void Entries_EveryTableInCorrectedText_HasAHeaderASeparatorAndEvenRows()
    {
        var problems = new List<string>();
        foreach (var entry in Shipped.Entries)
        {
            foreach (var (name, value) in entry.Set.Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                var lines = value.GetString()!.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith('|') || (i > 0 && lines[i - 1].StartsWith('|')))
                    {
                        continue;
                    }

                    var table = lines.Skip(i).TakeWhile(l => l.StartsWith('|')).ToList();
                    var cells = table.Select(l => l.Trim().Trim('|').Split('|').Length).ToList();
                    if (table.Count < 3 || !TableSeparator().IsMatch(table[1]) || cells.Distinct().Count() != 1 ||
                        !table.All(l => l.EndsWith('|')))
                    {
                        problems.Add($"{entry.Ref} {name}: malformed table starting \"{table[0]}\"");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // EquipmentMarkdown builds a 2024 magic item's italic type line ("Potion, Rare") from the first line of desc, and a
    // variant's second line ("Rare (Silver)") is upstream's; a correction replaces the text after them only.
    // (2014 items differ: their type line is the SRD 5.1 markdown's italic line, and it is what some corrections fix,
    // e.g. Mithral Armor's "but not hide".)
    [Fact]
    public void Entries_EveryMagicItemCorrection2024_KeepsUpstreamsTypeLine()
    {
        var problems = Shipped.Entries
            .Where(e => e.Target.Kind == SrdKinds.MagicItem && e.Target.Edition == SrdEdition.Edition2024)
            .Where(e =>
            {
                var vendored = CorrectedSrdContent.Vendored(e.Target)!.Value.GetProperty("desc").GetString()!.Split('\n')[0].Trim();
                return e.Set["desc"].GetString()!.Split('\n')[0] != vendored;
            })
            .Select(e => e.Ref)
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // The findings, as facts about the corrected record: the SRD text is there and the damage is gone.
    [Theory]
    // FIR-1: text spliced between records, tables run together, the Supreme row, stray fragments.
    [InlineData("2024/magic-item/potion-of-heroism", "desc", "you gain 10 Temporary Hit Points that last for 1 hour", "Gaseous Form")]
    [InlineData("2024/magic-item/potion-of-gaseous-form", "desc", "you gain the effect of the *Gaseous Form* spell for 1 hour", "Potion of Healing")]
    [InlineData("2024/magic-item/potion-of-giant-strength", "desc", "| Potion of Giant Strength (storm) | 29 | Legendary |", "Bless")]
    [InlineData("2024/magic-item/potion-of-flying", "desc", "This potion's clear liquid floats at the top of its container and has cloudy white impurities drifting in it.", "Giant Strength")]
    [InlineData("2024/magic-item/potions-of-healing", "desc", "| Potion of Healing (supreme) | 10d4 + 20 | Very Rare |", "impurities")]
    [InlineData("2024/magic-item/belt-of-giant-strength", "desc", "| Belt of Giant Strength (hill) | 21 | Rare |", "BeltStr")]
    [InlineData("2024/magic-item/armor-of-resistance", "desc", "| 6 | Necrotic |", "1d10Damage")]
    [InlineData("2024/magic-item/bag-of-tricks", "desc", "| 8 | Giant Elk |", null)]
    [InlineData("2024/magic-item/staff-of-frost", "desc", "| Cone of Cold | 5 |", "Hold Monster")]
    [InlineData("2024/magic-item/staff-of-fire", "desc", "| Wall of Fire | 4 |", "sphere")]
    [InlineData("2024/magic-item/sphere-of-annihilation", "desc", "Any creature whose space the sphere enters must succeed on a DC 19 Dexterity saving throw", null)]
    [InlineData("2024/magic-item/wand-of-fear", "desc", "| Fear (60-foot Cone) | 3 |", null)]
    [InlineData("2024/magic-item/wand-of-wonder", "desc", "| 01–20 | You cast a spell originating from the chosen point.", "expendthe")]
    [InlineData("2024/magic-item/horn-of-valhalla-1", "desc", "Wondrous Item\nRare (Silver)\nYou can take a Magic action to blow this horn.", "Requirement01")]
    // The SRD 5.2.1 erratum the 5.2 markdown lacks: the Giant Fly has 19 HP, as upstream (5.2.1) says.
    [InlineData("2024/magic-item/figurine-of-wondrous-power", "desc", "**HP** 19 (3d10 + 3)", "**HP** 15")]
    // Spells: Control Weather's text spliced into Control Water, tables and stat blocks lost.
    [InlineData("2024/spell/control-water", "description", "**_Whirlpool._** You cause a whirlpool to form", "weather")]
    [InlineData("2024/spell/control-weather", "description", "| 5 | Torrential rain, driving hail, or blizzard |", null)]
    [InlineData("2024/spell/confusion", "description", "| 1 | The target doesn't take an action", null)]
    [InlineData("2024/spell/divine-word", "description", "| 31–40 | The target has the Blinded and Deafened conditions for 10 minutes. |", null)]
    [InlineData("2024/spell/teleport", "description", "| Very familiar | 01–05 | 06–13 | 14–24 | 25–00 |", null)]
    [InlineData("2024/spell/summon-dragon", "higher_level", "| Wis | 14 | +2 | +2 |", "WiS")]
    // 2014 Magic Mouth's material was garbled ("10 inches"); its description is corrected below with the other 2014 spells.
    [InlineData("2014/spell/magic-mouth", "material", "jade dust worth at least 10 gp, which the spell consumes", "10 inches")]
    [InlineData("2024/spell/forcecage", "material", "ruby dust worth 1,500+ GP, which the spell consumes", ",,")]
    [InlineData("2024/spell/imprisonment", "material", "a statuette of the target worth 5,000+ GP", "worth,")]
    [InlineData("2024/spell/arcane-lock", "material", "gold dust worth 25+ GP, which the spell consumes", "(")]
    [InlineData("2024/spell/find-the-path", "material", "a set of divination tools—such as cards or runes—worth 100+ GP", "tools-such")]
    [InlineData("2024/spell/summon-dragon", "material", "an object with the image of a dragon engraved on it worth 500+ GP", "a, dragon")]
    // CHAR-1, data side.
    [InlineData("2024/feature/land-circle-of-the-land-spells", "description", "| 3 | Fog Cloud, Hold Person, Ray of Frost |", null)]
    [InlineData("2024/feature/land-circle-of-the-land-spells", "description", "| 3 | Misty Step, Shocking Grasp, Sleep |", null)]
    [InlineData("2024/feature/devotion-oath-of-devotion-spells", "description", "| 3 | Protection from Evil and Good, Shield of Faith |", "Shielf")]
    [InlineData("2024/feature/wizard-spellcasting", "description", "When you reach Wizard levels 4 and 10, you learn another Wizard cantrip of your choice", null)]
    [InlineData("2024/feature/barbarian-unarmored-defense", "description", "your base Armor Class equals 10 plus your Dexterity and Constitution modifiers", "Ar- mor")]
    [InlineData("2024/feat/boon-of-irresistible-offense", "description", "Increase your Strength or Dexterity score by 1", "one ability score of your choice")]
    [InlineData("2024/feat/boon-of-spell-recall", "description", "Increase your Intelligence, Wisdom, or Charisma score by 1", "one ability score of your choice")]
    [InlineData("2024/rule/breaking-objects", "description", "*See also* \"Damage Threshold.\"\n**_No Ability Scores._**", "\nk\n")]
    // RV-FIR-N2: the markdown's mid-sentence paragraph break is rejoined, so the save sentence renders whole.
    [InlineData("2024/magic-item/dust-of-sneezing-and-choking", "desc", "every creature in a 30-foot Emanation originating from you", "30-foot\nEmanation")]
    // NF-2: separate rows, and each caption on its own line above its header row.
    [InlineData("2024/feature/draconic-sorcery-draconic-spells", "description", "Draconic Spells\n| Sorcerer Level | Spells |\n| --- | --- |", "Fear, Fly 7")]
    [InlineData("2024/feature/draconic-sorcery-draconic-spells", "description", "| 5 | Fear, Fly |\n| 7 | Arcane Eye, Charm Monster |", null)]
    [InlineData("2024/feature/fiend-patron-fiend-spells", "description", "Fiend Spells\n| Warlock Level | Spells |", "Fiend Spells Warlock Level")]
    [InlineData("2024/feature/sorcerer-font-of-magic", "description", "Creating Spell Slots\n| Spell Slot Level | Sorcery Point Cost | Min. Sorcerer Level |", "Slots Spell Slot Level")]
    // Found by the breaks-mid-sentence scan: a paragraph broken before a capitalised game term renders as two.
    [InlineData("2024/feature/cleric-divine-intervention", "description", "doesn't require a Reaction to cast", "require a\nReaction")]
    // AUD-4, AUD-6.
    [InlineData("2024/trait/fiendish-legacy", "description", "Intelligence, Wisdom, or Charisma is your spellcasting ability", "spell-casting")]
    [InlineData("2024/trait/gnomish-lineage-rock-gnome", "description", "You know the *Mending* and *Prestidigitation* cantrips.", "You know the Prestidigitation cantrip.")]
    // RV-FC-N3 / AUD-8: 2014 spells whose text was back-translated, truncated or mistyped upstream, now SRD 5.1's.
    [InlineData("2014/spell/dominate-beast", "higher_level", "When you cast this spell with a 5th-level spell slot, the duration is concentration, up to 10 minutes. When you use a 6th-level spell slot, the duration is concentration, up to 1 hour. When you use a spell slot of 7th level or higher", "9th level")]
    [InlineData("2014/spell/dominate-beast", "desc", "You attempt to beguile a beast that you can see within range.", "beguile a creature")]
    [InlineData("2014/spell/conjure-animals", "higher_level", "three times as many with a 7th-level slot, and four times as many with a 9th-level slot.", null)]
    [InlineData("2014/spell/hold-monster", "desc", "Choose a creature that you can see within range. The target must succeed on a Wisdom saving throw or be paralyzed for the duration.", "saving throw of Wisdom")]
    [InlineData("2014/spell/hold-monster", "desc", "At the end of each of its turns, the target can make another Wisdom saving throw.", "each round")]
    [InlineData("2014/spell/hold-monster", "higher_level", "using a spell slot of 6th level or higher, you can target one additional creature for each slot level above 5th. The creatures must be within 30 feet of each other", "location")]
    [InlineData("2014/spell/hold-monster", "material", "A small, straight piece of iron.", null)]
    [InlineData("2014/spell/fire-shield", "desc", "The attacker takes 2d8 fire damage from a warm shield, or 2d8 cold damage from a cold shield.", "depending on the model")]
    [InlineData("2014/spell/fire-shield", "material", "A bit of phosphorus or a firefly.", null)]
    [InlineData("2014/spell/see-invisibility", "desc", "you can see into the Ethereal Plane. Ethereal creatures and objects appear ghostly and translucent.", "see through Ethereal")]
    [InlineData("2014/spell/water-breathing", "desc", "This spell grants up to ten willing creatures you can see within range the ability to breathe underwater until the spell ends.", "end of its term")]
    [InlineData("2014/spell/shatter", "higher_level", "When you cast this spell using a spell slot of 3rd level or higher, the damage increases by 1d8 for each slot level above 2nd.", "higher spell slot 2")]
    [InlineData("2014/spell/shatter", "material", "A chip of mica.", null)]
    [InlineData("2014/spell/magic-mouth", "desc", "You implant a message within an object in range, a message that is uttered when a trigger condition is met.", "come alive")]
    [InlineData("2014/spell/true-resurrection", "desc", "If the creature was undead, it is restored to its non-undead form.", null)]
    [InlineData("2014/spell/create-or-destroy-water", "desc", "a 30-foot cube within range, extinguishing exposed flames in the area.", null)]
    [InlineData("2014/spell/find-familiar", "desc", "If the spell requires an attack roll, you use your attack modifier for the roll.", "action modifier")]
    [InlineData("2014/spell/spike-growth", "desc", "at the time the spell is cast must make a Wisdom (Perception) check", "can make")]
    [InlineData("2014/spell/hallow", "desc", "such as orcs or trolls", "such as ores")]
    [InlineData("2014/spell/druidcraft", "desc", "the faint odor of skunk", "order")]
    [InlineData("2014/spell/entangle", "desc", "starting from a point within range", "form a point")]
    [InlineData("2014/spell/hideous-laughter", "desc", "The target has advantage on the saving throw", "had advantage")]
    [InlineData("2014/spell/magic-circle", "desc", "Glowing runes appear wherever the cylinder intersects", "whenever")]
    [InlineData("2014/spell/floating-disk", "desc", "It can move across uneven terrain", "If can")]
    [InlineData("2014/spell/symbol", "desc", "taking 10d10 necrotic damage on a failed save", "10d 10")]
    [InlineData("2014/spell/wall-of-fire", "desc", "within 10 feet of that side or inside the wall", "o f")]
    [InlineData("2014/spell/moonbeam", "higher_level", "the damage increases by 1d10 for each slot level above 2nd", "1dl0")]
    [InlineData("2014/spell/blight", "higher_level", "using a spell slot of 5th level or higher", "of higher")]
    [InlineData("2014/spell/longstrider", "higher_level", "for each slot level above 1st", "each spell slot")]
    [InlineData("2014/spell/enlarge-reduce", "material", "A pinch of powdered iron.", "pinch iron")]
    public void Corrected_Text_HasTheSrdWordsAndNotTheDamage(string reference, string property, string present, string? absent)
    {
        var value = Corrected(reference).GetProperty(property);
        var text = value.ValueKind == JsonValueKind.Array
            ? string.Join("\n", value.EnumerateArray().Select(p => p.GetString()))
            : value.GetString()!;

        Assert.Contains(present, text, StringComparison.Ordinal);
        if (absent is not null)
        {
            Assert.DoesNotContain(absent, text, StringComparison.Ordinal);
        }
    }

    // Structured fields: the value as compact JSON, so a test can pin an exact index, number or dice string.
    [Theory]
    // FIR-2: category membership, both sides.
    [InlineData("2024/equipment/hide-armor", "equipment_categories", "\"index\":\"medium-armor\"", "\"index\":\"light-armor\"")]
    [InlineData("2024/equipment/explorers-pack", "equipment_categories", "\"index\":\"equipment-packs\"", null)]
    [InlineData("2024/equipment/disguise-kit", "equipment_categories", "\"index\":\"tools\"", "\"index\":\"adventuring-gear\"")]
    [InlineData("2024/equipment-category/martial-melee-weapons", "equipment", "\"index\":\"longsword\"", null)]
    [InlineData("2024/equipment-category/tools", "equipment", "\"index\":\"forgery-kit\"", null)]
    // Wrong stats and pack contents.
    [InlineData("2024/equipment/longbow", "cost", "{\"quantity\":50,\"unit\":\"gp\"}", null)]
    [InlineData("2024/equipment/dart", "cost", "{\"quantity\":5,\"unit\":\"cp\"}", null)]
    [InlineData("2024/equipment/chain-shirt", "weight", "20", null)]
    [InlineData("2024/equipment/trident", "damage", "\"damage_dice\":\"1d8\"", null)]
    [InlineData("2024/equipment/trident", "two_handed_damage", "\"damage_dice\":\"1d10\"", null)]
    [InlineData("2024/equipment/sling", "damage", "\"index\":\"bludgeoning\"", "piercing")]
    [InlineData("2024/equipment/brewers-supplies", "utilize", "\"name\":\"Detect poisoned drink\",\"dc\":{\"dc_type\":{\"index\":\"int\",\"name\":\"INT\",\"url\":\"/api/2024/ability-scores/int\"},\"dc_value\":15", null)]
    [InlineData("2024/equipment/entertainers-pack", "contents", "\"index\":\"lantern-bullseye\"", "\"index\":\"disguise-kit\"")]
    [InlineData("2024/equipment/burglars-pack", "contents", "\"index\":\"oil\",\"name\":\"Oil\",\"url\":\"/api/2024/equipment/oil\"},\"quantity\":7", "\"index\":\"flask\"")]
    [InlineData("2024/equipment/scholars-pack", "contents", "\"index\":\"book\"", "\"index\":\"blanket\"")]
    // The Mule carried the Octopus's traits, action and reaction.
    [InlineData("2024/monster/mule", "special_abilities", "\"name\":\"Beast of Burden\"", "octopus")]
    [InlineData("2024/monster/mule", "actions", "\"desc\":\"Melee Attack Roll: +4, reach 5 ft. Hit: 4 (1d4 + 2) Bludgeoning damage.\"", "Tentacles")]
    [InlineData("2024/monster/mule", "reactions", "[]", null)]
    // NF-4: the species links its Draconic Ancestry trait (the table behind Breath Weapon and Damage Resistance), first, as SRD 5.2 lists it.
    [InlineData("2024/species/dragonborn", "traits", "[{\"index\":\"draconic-ancestry\",\"url\":\"/api/2024/traits/draconic-ancestry\",\"name\":\"Draconic Ancestry\"},{\"index\":\"darkvision-60\"", null)]
    // AUD-6: the Rock Gnome option's spell list, as its text says.
    [InlineData("2024/trait/gnomish-lineage-rock-gnome", "spells", "\"index\":\"mending\"", null)]
    // AUD-2, AUD-5 / RV-FIR-N4: sections upstream dropped, added.
    [InlineData("2024/monster/pirate-captain", "bonus_actions", "\"name\":\"Captain's Charm\",\"desc\":\"Wisdom Saving Throw: DC 14, one creature the pirate can see within 30 feet. Failure: The target has the Charmed condition until the start of the pirate's next turn.\"", null)]
    [InlineData("2024/monster/unicorn", "bonus_actions", "\"usage\":{\"type\":\"per day\",\"times\":3}", null)]
    [InlineData("2024/equipment/entertainers-pack", "weight", "58.5", null)]
    [InlineData("2024/equipment/glassblowers-tools", "utilize", "\"name\":\"Discern what a glass object held in the past 24 hours\",\"dc\":{\"dc_type\":{\"index\":\"int\"", null)]
    [InlineData("2014/spell/heroism", "higher_level", "you can target one additional creature for each slot level above 1st.", null)]
    // 2014 magic items: dropped benefits and restrictions, wrong rarities.
    [InlineData("2014/magic-item/belt-of-dwarvenkind", "desc", "You have darkvision out to a range of 60 feet.", "Wondrous Items")]
    [InlineData("2014/magic-item/mithral-armor", "desc", "Armor (medium or heavy, but not hide), uncommon", null)]
    [InlineData("2014/magic-item/rod-of-lordly-might", "desc", "(you choose the type of sword)", null)]
    [InlineData("2014/magic-item/bracers-of-defense", "desc", "Wondrous item, rare (requires attunement)", "Wondous")]
    [InlineData("2014/magic-item/staff-of-the-python", "rarity", "{\"name\":\"Uncommon\"}", null)]
    [InlineData("2014/magic-item/staff-of-the-python", "desc", "Staff, uncommon (requires attunement by a cleric, druid, or warlock)", "very rare")]
    [InlineData("2014/magic-item/staff-of-swarming-insects", "rarity", "{\"name\":\"Rare\"}", null)]
    [InlineData("2014/magic-item/spell-scroll", "desc", "you can use an action to read the scroll and cast its spell without having to provide any of the spell's components.", "uninterrupted")]
    // 2014 monsters: to-hit bonuses the stat block's own Strength and proficiency contradict, and wrong text.
    [InlineData("2014/monster/kraken", "actions", "\"desc\":\"Melee Weapon Attack: +17 to hit, reach 5 ft., one target.", "+7 to hit")]
    [InlineData("2014/monster/kraken", "actions", "\"attack_bonus\":17", "\"attack_bonus\":7,")]
    [InlineData("2014/monster/purple-worm", "actions", "\"attack_bonus\":14", "\"attack_bonus\":9")]
    [InlineData("2014/monster/gynosphinx", "actions", "Melee Weapon Attack: +8 to hit, reach 5 ft., one target.", "+9 to hit")]
    [InlineData("2014/monster/black-bear", "actions", "\"attack_bonus\":4", "\"attack_bonus\":3")]
    [InlineData("2014/monster/brown-bear", "actions", "\"attack_bonus\":6", "\"attack_bonus\":5")]
    [InlineData("2014/monster/solar", "actions", "If the target is a creature that has 100 hit points or fewer", "190 hit points")]
    [InlineData("2014/monster/assassin", "special_abilities", "the assassin deals an extra 14 (4d6) damage", "13 (4d6)")]
    [InlineData("2014/monster/efreeti", "special_abilities", "leaving behind only equipment the efreeti was wearing or carrying", "djinni")]
    [InlineData("2014/monster/ettercap", "actions", "immunity to bludgeoning, poison, and psychic damage", null)]
    [InlineData("2014/monster/lich", "legendary_actions", "Each non-undead creature within 20 feet of the lich", "living")]
    [InlineData("2014/monster/harpy", "actions", "the target must move on its turn toward the harpy by the most direct route, trying to get within 5 feet", "the must move")]
    [InlineData("2014/monster/adult-gold-dragon", "actions", "\"name\":\"Change Shape\",\"desc\":\"The dragon magically polymorphs into a humanoid or beast", null)]
    [InlineData("2014/monster/adult-silver-dragon", "actions", "\"name\":\"Change Shape\"", null)]
    [InlineData("2014/monster/adult-bronze-dragon", "actions", "\"name\":\"Change Shape\"", null)]
    public void Corrected_Value_HasTheSrdValueAndNotTheDamage(string reference, string property, string present, string? absent)
    {
        var json = Corrected(reference).GetProperty(property).GetRawText();

        Assert.Contains(present, json, StringComparison.Ordinal);
        if (absent is not null)
        {
            Assert.DoesNotContain(absent, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    // A record the overlay does not name comes back untouched (Apply returns null, so the builder stores upstream's).
    [Theory]
    [InlineData("2024/magic-item/potion-of-invisibility")]
    [InlineData("2024/spell/fireball")]
    [InlineData("2014/magic-item/potion-of-heroism")]
    // AUD-1: Wild Shape's upstream text is intact (each list item on its own line, its table in the slash-row form the
    // formatters read), so the entry that only reformatted it, under a reason that was not true, is gone.
    [InlineData("2024/feature/druid-wild-shape")]
    public void Apply_UncorrectedRecord_ReturnsNull(string reference)
    {
        var target = Ref(reference);

        Assert.Null(Shipped.Apply(target, CorrectedSrdContent.Vendored(target)!.Value));
    }

    // CMR-6: each rule Parse enforces, broken once. Without these any rule could be deleted with every suite green, and a
    // duplicate ref would then escape as a raw ArgumentException instead of the actionable message the index build shows.
    [Theory]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}, {"ref": "2024/spell/fireball", "set": {"range": "x"}, "reason": "Upstream r.", "source": "s"}""", "corrects \"2024/spell/fireball\" more than once")]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {}, "reason": "Upstream r.", "source": "s"}""", "sets nothing and adds nothing")]
    [InlineData("""{"ref": "2024/spell/fireball", "reason": "Upstream r.", "source": "s"}""", "sets nothing and adds nothing")]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {}, "add": {}, "reason": "Upstream r.", "source": "s"}""", "sets nothing and adds nothing")]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {"desc": "x"}, "reason": " ", "source": "s"}""", "needs a reason and a source")]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {"desc": "x"}, "reason": "Upstream r.", "source": ""}""", "needs a reason and a source")]
    [InlineData("""{"ref": "2024/spell", "set": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}""", "is not a ref of the form edition/kind/slug")]
    [InlineData("""{"ref": "2014/species/elf", "set": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}""", "is not a ref of the form edition/kind/slug")]
    [InlineData("""{"ref": "2025/spell/fireball", "set": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}""", "is not a ref of the form edition/kind/slug")]
    [InlineData("""{"ref": "2024/spell/", "set": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}""", "is not a ref of the form edition/kind/slug")]
    [InlineData("""{"ref": "2024/spell/fireball", "set": {"desc": "x"}, "add": {"desc": "y"}, "reason": "Upstream r.", "source": "s"}""", "both sets and adds \"desc\"")]
    [InlineData("""{"ref": "2024/spell/fireball", "sets": {"desc": "x"}, "reason": "Upstream r.", "source": "s"}""", "is not valid")]
    [InlineData("""null""", "has a null entry")]
    public void Parse_EntryBreakingARule_IsRefusedWithWhy(string entries, string why)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($$"""{"corrections": [{{entries}}]}""");

        var error = Assert.Throws<InvalidDataException>(() => SrdCorrections.Parse(bytes));

        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    // D5, both directions, on real records: "set" replaces only what the record has, "add" supplies only what it lacks.
    [Theory]
    [InlineData("2024/spell/fireball", """{"set": {"descr": "x"}}""", "sets \"descr\", which the record does not have")]
    [InlineData("2024/spell/fireball", """{"add": {"description": "x"}}""", "adds \"description\", which the record already has")]
    [InlineData("2014/spell/heroism", """{"set": {"higher_level": ["x"]}}""", "sets \"higher_level\", which the record does not have")]
    [InlineData("2024/equipment/burglars-pack", """{"add": {"weight": 1}}""", "adds \"weight\", which the record already has")]
    public void Apply_CorrectionThatDoesNotFitTheRecord_IsRefusedWithWhy(string reference, string change, string why)
    {
        var target = Ref(reference);
        var corrections = One(reference, change);

        var error = Assert.Throws<InvalidDataException>(() => corrections.Apply(target, CorrectedSrdContent.Vendored(target)!.Value));

        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    // A set property keeps its place and an added one follows the record's own, so the corrected record reads like
    // upstream's with the section put back.
    [Fact]
    public void Apply_SetAndAdd_ReplacesInPlaceAndAppendsTheAdditions()
    {
        var target = Ref("2024/equipment/entertainers-pack");
        var record = CorrectedSrdContent.Vendored(target)!.Value;
        var corrections = One(target.ToString(), """{"set": {"cost": {"quantity": 1, "unit": "gp"}}, "add": {"weight": 58.5, "note": "n"}}""");

        var corrected = JsonDocument.Parse(corrections.Apply(target, record)!).RootElement;

        Assert.Equal([.. record.EnumerateObject().Select(p => p.Name), "weight", "note"], corrected.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, corrected.GetProperty("cost").GetProperty("quantity").GetInt32());
        Assert.Equal(58.5, corrected.GetProperty("weight").GetDouble());
        Assert.True(JsonElement.DeepEquals(record.GetProperty("contents"), corrected.GetProperty("contents")));
    }

    // A one-entry corrections file for reference with change's "set"/"add" members.
    private static SrdCorrections One(string reference, string change)
    {
        var members = string.Join(", ", JsonDocument.Parse(change).RootElement.EnumerateObject().Select(p => $"\"{p.Name}\": {p.Value.GetRawText()}"));
        return SrdCorrections.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"corrections": [{"ref": "{{reference}}", {{members}}, "reason": "Upstream test.", "source": "test"}]}"""));
    }

    private static JsonElement Corrected(string reference)
    {
        var target = Ref(reference);
        var json = Shipped.Apply(target, CorrectedSrdContent.Vendored(target)!.Value);
        Assert.True(json is not null, $"{reference} has no correction.");
        return JsonDocument.Parse(json!).RootElement.Clone();
    }

    private static SrdRef Ref(string reference)
    {
        var parts = reference.Split('/');
        return new SrdRef(parts[0], parts[1], parts[2]);
    }

    // "SRD 5.2 markdown 10_MagicItems.md › Potion of Heroism", "SRD 5.2 markdown 03_Classes/04_Druid.md › Level 3: …",
    // "SRD 5.2 markdown 12_MonstersA-Z.md › Pirate Captain › Bonus Actions", the SRD 5.2.1 PDF when the markdown itself
    // carries the damage, or for 2014 records the SRD 5.1 markdown ("SRD 5.1 markdown 07_Spells/Spells_Each/Magic_Mouth.md
    // › Magic Mouth", "SRD 5.1 markdown 10_Monsters/Monsters_Each/Solar_(Angel).md › Solar › Slaying Longbow").
    [GeneratedRegex(@"^(SRD 5\.2 markdown (03_Classes/)?\d\d_[\w-]+\.md › \S|SRD 5\.2\.1 PDF, \S|SRD 5\.1 markdown \d\d_\w+(/[\w'(),-]+)*\.md › \S)")]
    private static partial Regex CitedSource();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^## What is corrected \((\d+) entries: (\d+) in 2024, (\d+) in 2014\)$", RegexOptions.Multiline)]
    private static partial Regex ReadmeHeading();

    [GeneratedRegex(@"^### (2014|2024) \(")]
    private static partial Regex ReadmeEditionHeading();

    [GeneratedRegex(@"^\| ([a-z-]+) \| (\d+) \|")]
    private static partial Regex ReadmeKindRow();

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => element.EnumerateObject().Where(p => p.Name is not ("index" or "url")).SelectMany(p => Strings(p.Value)),
        _ => [],
    };

    [GeneratedRegex(@"^\|(\s*:?-{3,}:?\s*\|)+$")]
    private static partial Regex TableSeparator();
}
