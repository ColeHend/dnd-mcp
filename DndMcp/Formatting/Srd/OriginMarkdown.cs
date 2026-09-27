using System.Globalization;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for a character's origins: races and subraces (2014), species and subspecies (2024), their traits,
/// backgrounds and feats.
///
/// <para>
/// A race or species record only lists its traits by reference, and the traits are where the rules are ("You have
/// Darkvision with a range of 60 feet"), so race, species, subrace and subspecies bodies inline each trait's text under
/// its ref. A question like "what does a high elf get?" is then one call, and the ref still leads to the trait's own
/// record for its structured details (proficiency choices, spell options, the dragonborn breath weapon).
/// </para>
/// <para>
/// One formatter serves both editions, keyed on the fields a record has: 2014 and 2024 kept most field names
/// (<c>traits</c>, <c>speed</c>, <c>proficiency_choices</c>) and renamed the rest, so reading every known name keeps a
/// record from losing a line because it is from the other edition.
/// </para>
/// </summary>
internal static class OriginMarkdown
{
    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var body = doc.Kind switch
        {
            SrdKinds.Race or SrdKinds.Species => RaceBody(doc.Root, lookup),
            SrdKinds.Subrace or SrdKinds.Subspecies => SubraceBody(doc.Root, lookup),
            SrdKinds.Trait => TraitBody(doc.Root, lookup),
            SrdKinds.Background => BackgroundBody(doc.Root),
            SrdKinds.Feat => FeatBody(doc.Root),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(body) ? GenericMarkdown.Body(doc, lookup) : body;
    }

    private static string RaceBody(JsonElement root, ISrdLookup lookup)
    {
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Creature Type", root.Str("type")),
            SrdMarkdownText.Field("Size", root.Str("size") ?? Choice(root, "size_options")),
            SrdMarkdownText.Field("Speed", Speed(root)),
            SrdMarkdownText.Field("Ability Score Increases", AbilityBonuses(root.Arr("ability_bonuses"))),
            SrdMarkdownText.Field("Ability Score Increase Options", Choice(root, "ability_bonus_options")),
            SrdMarkdownText.Field("Languages", SrdMarkdownText.LinkList(root.Arr("languages"))),
            SrdMarkdownText.Field("Language Options", Choice(root, "language_options")),
        ]);

        // 2014 races describe age, alignment, size and languages in prose; the SRD prints them as run-in paragraphs.
        var prose = SrdProse.Join(new[]
        {
            RunIn("Age", root.Str("age")),
            RunIn("Alignment", root.Str("alignment")),
            RunIn("Size", root.Str("size_description")),
            RunIn("Languages", root.Str("language_desc")),
        }.OfType<string>());

        return SrdMarkdownText.Blocks(
        [
            fields,
            prose,
            SrdMarkdownText.Section("Traits", Traits(root.Arr("traits"), lookup)),
            SrdMarkdownText.Section("Subraces", SrdMarkdownText.LinkList(root.Arr("subraces"))),
            SrdMarkdownText.Section("Subspecies", Subspecies(root.Arr("subspecies"), lookup)),
        ]);
    }

    /// <summary>
    /// A 2024 species' subspecies, each with the names of the traits it adds: "- Draconic Ancestor: Blue (<c>ref</c>) —
    /// Breath Weapon: Lightning; Damage Resistance: Lightning". 2024 puts core traits on the subspecies (the dragonborn's
    /// Breath Weapon and Damage Resistance exist only there), so a species page that only linked them read as a
    /// dragonborn without a breath weapon, and a comparison with 2014 as a rules change. The traits' text stays on the
    /// subspecies' own pages. A subspecies the lookup cannot resolve, or with no traits, is just its link.
    /// </summary>
    private static string? Subspecies(IReadOnlyList<JsonElement> subspecies, ISrdLookup lookup)
    {
        if (subspecies.Count == 0)
        {
            return null;
        }

        var items = subspecies.Select(reference =>
        {
            var traits = SrdChoiceMarkdown.Resolve(reference.Str("url"), lookup)?.Root.Arr("traits")
                .Select(t => t.Str("name") is not { } name ? null
                    : t.Int("level") is { } level and > 1 ? $"{name} (level {level.ToString(CultureInfo.InvariantCulture)})"
                    : name)
                .OfType<string>()
                .ToList();
            return traits is { Count: > 0 }
                ? $"- {SrdMarkdownText.Link(reference)} — {string.Join("; ", traits)}"
                : $"- {SrdMarkdownText.Link(reference)}";
        });
        return string.Join("\n", items);
    }

    private static string SubraceBody(JsonElement root, ISrdLookup lookup)
    {
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Race", root.Obj("race") is { } race ? SrdMarkdownText.Link(race) : null),
            SrdMarkdownText.Field("Species", root.Obj("species") is { } species ? SrdMarkdownText.Link(species) : null),
            SrdMarkdownText.Field("Ability Score Increases", AbilityBonuses(root.Arr("ability_bonuses"))),
            SrdMarkdownText.Field("Damage Type", root.Obj("damage_type") is { } damage ? SrdMarkdownText.Link(damage) : null),
        ]);

        // 2014 subraces call them racial_traits; 2024 subspecies call them traits and give each the character level it
        // arrives at (High Elf: Misty Step at 5).
        var traits = root.Arr("racial_traits").Concat(root.Arr("traits")).ToList();
        return SrdMarkdownText.Blocks(
        [
            fields,
            SrdProse.Join(root.Description()),
            SrdMarkdownText.Section("Traits", Traits(traits, lookup)),
        ]);
    }

    /// <summary>
    /// Each trait as "#### [Level N: ]Name (<c>ref</c>)" followed by its text from the trait record (labelled when a
    /// curated correction changed it) and, when the trait is a choice between other traits, those options with their
    /// one-line summaries. The 2014 Draconic Ancestry text says "Choose one type of dragon from the Draconic Ancestry
    /// table"; the options (damage type, breath shape and save per dragon) are that table, and a race page that cites it
    /// without showing it leaves "what does a blue dragonborn breathe?" to a guess. Each option's full breath-weapon
    /// details stay on its own record.
    /// </summary>
    private static string? Traits(IReadOnlyList<JsonElement> traits, ISrdLookup lookup) =>
        SrdMarkdownText.Blocks(traits.Select(reference =>
        {
            var level = reference.Int("level") is { } l ? $"Level {l.ToString(CultureInfo.InvariantCulture)}: " : string.Empty;
            var trait = SrdChoiceMarkdown.Resolve(reference.Str("url"), lookup);
            var heading = $"#### {level}{SrdMarkdownText.Link(reference)}";
            return trait is null
                ? heading
                : SrdMarkdownText.Blocks(
                [
                    heading,
                    SrdChoiceMarkdown.CorrectionNotes(trait),
                    SrdProse.Join(trait.Root.Description()),
                    TraitOptions(trait.Root, lookup),
                ]);
        }));

    // The trait_specific choices whose options are all trait records, as TraitSpecific lists them on the trait's page.
    private static string? TraitOptions(JsonElement root, ISrdLookup lookup)
    {
        if (root.Obj("trait_specific") is not { } specific)
        {
            return null;
        }

        var parent = root.Description();
        return SrdMarkdownText.Blocks(specific.EnumerateObject()
            .Select(entry => SrdChoiceMarkdown.RecordOptions(entry.Value, SrdKinds.Trait) is { } options
                ? SrdChoiceMarkdown.OptionBullets(OptionsLabel(entry.Name), entry.Value.Int("choose"), options, lookup, parent,
                    AncestrySummary)
                : null));
    }

    private static string OptionsLabel(string key) => key == "subtrait_options" ? "Options" : SrdChoiceMarkdown.Humanize(key);

    private static string TraitBody(JsonElement root, ISrdLookup lookup)
    {
        var owners = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Races", SrdMarkdownText.LinkList(root.Arr("races"))),
            SrdMarkdownText.Field("Subraces", SrdMarkdownText.LinkList(root.Arr("subraces"))),
            SrdMarkdownText.Field("Species", SrdMarkdownText.LinkList(root.Arr("species"))),
            SrdMarkdownText.Field("Subspecies", SrdMarkdownText.LinkList(root.Arr("subspecies"))),
            SrdMarkdownText.Field("Parent", root.Obj("parent") is { } parent ? SrdMarkdownText.Link(parent) : null),
        ]);

        var details = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Proficiencies", SrdMarkdownText.LinkList(root.Arr("proficiencies"))),
            SrdMarkdownText.Field("Proficiency Choices", Choice(root, "proficiency_choices")),
            SrdMarkdownText.Field("Language Options", Choice(root, "language_options")),
            SrdMarkdownText.Field("Spells", SrdMarkdownText.LinkList(root.Arr("spells").Select(s => s.Obj("spell")).OfType<JsonElement>())),
            SrdMarkdownText.Field("Speed", Speed(root)),
        ]);

        return SrdMarkdownText.Blocks(
        [
            owners,
            SrdProse.Join(root.Description()),
            details,
            TraitSpecific(root, lookup),
        ]);
    }

    /// <summary>
    /// 2014 <c>trait_specific</c>: a Draconic Ancestry option's damage type and breath weapon (area, save, uses and the
    /// damage by character level, which the prose gives only as a sentence), the parent trait's ancestry options with
    /// each one's damage type and breath shape (the SRD's Draconic Ancestry table), and spell choices (High Elf cantrip).
    /// </summary>
    private static string? TraitSpecific(JsonElement root, ISrdLookup lookup)
    {
        if (root.Obj("trait_specific") is not { } specific)
        {
            return null;
        }

        var parent = root.Description();
        var blocks = new List<string?>();
        foreach (var entry in specific.EnumerateObject())
        {
            var value = entry.Value;
            if (entry.Name == "damage_type" && value.ValueKind == JsonValueKind.Object)
            {
                blocks.Add(SrdMarkdownText.Field("Damage Type", SrdMarkdownText.Link(value)));
            }
            else if (entry.Name == "breath_weapon" && value.ValueKind == JsonValueKind.Object)
            {
                blocks.Add(BreathWeapon(value));
            }
            else if (SrdChoiceMarkdown.RecordOptions(value, SrdKinds.Trait) is { } options)
            {
                blocks.Add(SrdChoiceMarkdown.OptionBullets(OptionsLabel(entry.Name), value.Int("choose"), options, lookup, parent,
                    AncestrySummary));
            }
            else
            {
                blocks.Add(SrdMarkdownText.Field(SrdChoiceMarkdown.Humanize(entry.Name), SrdChoiceMarkdown.Describe(value)));
            }
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    // "Acid damage; breath weapon 30-foot line, DEX save." for one Draconic Ancestry option; null for other traits.
    private static string? AncestrySummary(SrdDocument option)
    {
        if (option.Root.Obj("trait_specific") is not { } specific)
        {
            return null;
        }

        var parts = new List<string>();
        if (specific.Obj("damage_type")?.Str("name") is { } damage)
        {
            parts.Add($"{damage} damage");
        }

        if (specific.Obj("breath_weapon") is { } breath)
        {
            var shape = string.Join(", ", new[] { Area(breath.Obj("area_of_effect")), SaveAbility(breath.Obj("dc")) }
                .Where(p => p is not null));
            parts.Add(shape.Length > 0 ? $"breath weapon {shape}" : "breath weapon");
        }

        return parts.Count > 0 ? string.Join("; ", parts) + "." : null;
    }

    private static string? BreathWeapon(JsonElement breath)
    {
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Area", Area(breath.Obj("area_of_effect"))),
            SrdMarkdownText.Field("Save", Save(breath.Obj("dc"))),
            SrdMarkdownText.Field("Uses", Usage(breath.Obj("usage"))),
            .. breath.Arr("damage").Select(damage => SrdMarkdownText.Field("Damage", Damage(damage))),
        ]);

        return SrdMarkdownText.Section(breath.Str("name") ?? "Breath Weapon",
            SrdMarkdownText.Blocks([fields, SrdProse.Join(breath.Paragraphs("desc"))]));
    }

    // {size: 30, type: "line"} → "30-foot line", as the spell formatter prints areas.
    private static string? Area(JsonElement? area) =>
        area is { } a && a.Int("size") is { } size && a.Str("type") is { } type
            ? $"{size.ToString(CultureInfo.InvariantCulture)}-foot {type}"
            : null;

    private static string? SaveAbility(JsonElement? dc) =>
        dc?.Obj("dc_type")?.Str("name") is { } ability ? $"{ability} save" : null;

    // "DEX, half damage on a success": the label already says it is a save.
    private static string? Save(JsonElement? dc)
    {
        if (dc?.Obj("dc_type")?.Str("name") is not { } ability)
        {
            return null;
        }

        return dc?.Str("success_type") switch
        {
            "half" => $"{ability}, half damage on a success",
            "none" => $"{ability}, no damage on a success",
            { } other => $"{ability}, {other} on a success",
            null => ability,
        };
    }

    // {type: "per rest", times: 1} → "1 per rest".
    private static string? Usage(JsonElement? usage) =>
        usage is { } u && u.Str("type") is { } type
            ? u.Int("times") is { } times ? $"{times.ToString(CultureInfo.InvariantCulture)} {type}" : type
            : null;

    // "Fire (`2014/damage-type/fire`): 2d6 (level 1), 3d6 (6), 4d6 (11), 5d6 (16)", the spell formatter's cantrip style.
    private static string? Damage(JsonElement damage)
    {
        var type = damage.Obj("damage_type") is { } t ? SrdMarkdownText.Link(t) : null;
        var byLevel = damage.Obj("damage_at_character_level") is { } levels
            ? levels.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => (Level: int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue,
                              Dice: p.Value.GetString()!))
                .OrderBy(p => p.Level)
                .Select((p, i) => i == 0
                    ? $"{p.Dice} (level {p.Level.ToString(CultureInfo.InvariantCulture)})"
                    : $"{p.Dice} ({p.Level.ToString(CultureInfo.InvariantCulture)})")
                .ToList()
            : [];
        var dice = byLevel.Count > 0 ? string.Join(", ", byLevel) : damage.Str("damage_dice");

        return (type, dice) switch
        {
            (null, null) => null,
            (_, null) => type,
            (null, _) => dice,
            _ => $"{type}: {dice}",
        };
    }

    private static string BackgroundBody(JsonElement root)
    {
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Ability Scores", SrdMarkdownText.LinkList(root.Arr("ability_scores"))),
            SrdMarkdownText.Field("Feat", root.Obj("feat") is { } feat ? SrdMarkdownText.LinkWithNote(feat) : null),
            SrdMarkdownText.Field("Proficiencies",
                SrdMarkdownText.LinkList(root.Arr("starting_proficiencies").Concat(root.Arr("proficiencies")))),
            SrdChoiceMarkdown.Bullets("Proficiency Choices", root.Arr("proficiency_choices").Select(SrdChoiceMarkdown.Describe)),
            SrdMarkdownText.Field("Languages", Choice(root, "language_options")),
            SrdMarkdownText.Field("Equipment", StartingEquipment(root)),
            SrdChoiceMarkdown.Bullets("Equipment", root.Arr("equipment_options").Select(SrdChoiceMarkdown.Describe)),
        ]);

        var feature = root.Obj("feature") is { } f
            ? SrdMarkdownText.Section($"Feature: {f.Str("name") ?? "?"}", SrdProse.Join(f.Description()))
            : null;

        return SrdMarkdownText.Blocks(
        [
            fields,
            SrdProse.Join(root.Description()),
            feature,
            NumberedChoice("Personality Traits", root.Obj("personality_traits")),
            NumberedChoice("Ideals", root.Obj("ideals")),
            NumberedChoice("Bonds", root.Obj("bonds")),
            NumberedChoice("Flaws", root.Obj("flaws")),
        ]);
    }

    // 2014: fixed items, the equipment choices and the gold, in the order the SRD lists a background's equipment.
    private static string? StartingEquipment(JsonElement root)
    {
        var parts = new List<string?> { SrdChoiceMarkdown.EquipmentList(root.Arr("starting_equipment")) };
        parts.AddRange(root.Arr("starting_equipment_options").Select(SrdChoiceMarkdown.Describe));
        if (root.Obj("starting_gold") is { } gold && gold.Int("quantity") is { } quantity)
        {
            parts.Add($"{quantity.ToString(CultureInfo.InvariantCulture)} {gold.Str("unit") ?? string.Empty}".TrimEnd());
        }

        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count > 0 ? string.Join(", ", present) : null;
    }

    // A 2014 background's d8/d6 characteristic tables as numbered lists under "### Ideals (choose 1)".
    private static string? NumberedChoice(string title, JsonElement? choice)
    {
        if (choice is not { } c)
        {
            return null;
        }

        var options = SrdChoiceMarkdown.OptionsOf(c).Select(SrdChoiceMarkdown.Option).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        var heading = c.Int("choose") is { } n ? $"{title} (choose {n.ToString(CultureInfo.InvariantCulture)})" : title;
        return SrdMarkdownText.Section(heading,
            string.Join("\n", options.Select((o, i) => $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {o!.Trim()}")));
    }

    private static string FeatBody(JsonElement root)
    {
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Type", root.Str("type") is { } type ? $"{SrdChoiceMarkdown.Humanize(type)} Feat" : null),
            SrdMarkdownText.Field("Prerequisite", FeatPrerequisites(root)),
            SrdMarkdownText.Field("Repeatable", root.Str("repeatable") ?? (root.Bool("repeatable") == true ? "Yes" : null)),
        ]);

        return SrdMarkdownText.Blocks([fields, SrdProse.Join(root.Description())]);
    }

    /// <summary>
    /// A feat's prerequisites in the SRD's words: 2014 minimum scores ("STR 13+"), 2024 <c>minimum_level</c>
    /// ("Level 4+") and <c>feature_named</c> ("Fighting Style Feature"), and a 2024 choice of scores by its desc
    /// ("Strength or Dexterity 13+"). Null when the feat has none.
    /// </summary>
    private static string? FeatPrerequisites(JsonElement root)
    {
        var parts = new List<string?>();
        parts.AddRange(root.Arr("prerequisites").Select(SrdChoiceMarkdown.ScorePrerequisite));

        if (root.Obj("prerequisites") is { } required)
        {
            foreach (var entry in required.EnumerateObject())
            {
                parts.Add(entry.Name switch
                {
                    "minimum_level" when entry.Value.ValueKind == JsonValueKind.Number =>
                        $"Level {entry.Value.GetRawText()}+",
                    "feature_named" when entry.Value.ValueKind == JsonValueKind.String => $"{entry.Value.GetString()} Feature",
                    _ => ClassMarkdown.Value(entry.Name, entry.Value) is { } value
                        ? $"{SrdChoiceMarkdown.Humanize(entry.Name)} {value}"
                        : null,
                });
            }
        }

        parts.Add(Choice(root, "prerequisite_options"));
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count > 0 ? string.Join(", ", present) : null;
    }

    // A choice stored as one object (most) or as an array of them (2014 class-style); each described, joined by "; ".
    private static string? Choice(JsonElement root, string property)
    {
        if (root.Obj(property) is { } single)
        {
            return SrdChoiceMarkdown.Describe(single);
        }

        var described = root.Arr(property).Select(SrdChoiceMarkdown.Describe).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        return described.Count > 0 ? string.Join("; ", described) : null;
    }

    // "DEX +2, CHA +1": the ability is part of a value here, so it is named rather than linked.
    private static string? AbilityBonuses(IReadOnlyList<JsonElement> bonuses)
    {
        var parts = bonuses
            .Where(b => b.Obj("ability_score") is not null && b.Int("bonus") is not null)
            .Select(b => $"{b.Obj("ability_score")!.Value.Str("name")} {SrdMarkdownText.Signed(b.Int("bonus")!.Value)}")
            .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static string? Speed(JsonElement root) =>
        root.Int("speed") is { } feet ? $"{feet.ToString(CultureInfo.InvariantCulture)} ft." : root.Str("speed");

    private static string? RunIn(string label, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : $"***{label}.*** {text.Trim()}";
}
