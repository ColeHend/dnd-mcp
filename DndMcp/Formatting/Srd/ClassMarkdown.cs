using System.Globalization;
using System.Text;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for classes, subclasses, level records and class features: what a player reads to build a character
/// level by level.
///
/// <para>
/// A class record holds only proficiencies and equipment. What a class does lives in its 20 level records and the
/// feature records those link to, so a class body is mostly the level table from <see cref="ISrdLookup.ClassLevels"/>,
/// and every feature named in the table carries its ref: the table only names features, and the model must be able to
/// fetch the rules text of the one a player asks about. A subclass has four to six features and is usually read as a
/// whole, so its body inlines their text.
/// </para>
/// <para>
/// Both editions go through the same code, keyed on which fields a record has rather than on its edition. The shapes
/// overlap but differ (a 2014 feature's <c>level</c> is a number, a 2024 one's is a level reference; 2014 subclasses
/// list their features only through level records, 2024 ones inline them), and a switch on the edition would drop a
/// line silently the first time upstream moved a field.
/// </para>
/// </summary>
internal static class ClassMarkdown
{
    /// <summary>Upstream's stand-in for "Unlimited" (2014 barbarian rages at level 20).</summary>
    private const long UnlimitedSentinel = 9999;

    /// <summary>Spellcasting columns in the order the SRD's class tables print them; slot levels follow.</summary>
    private static readonly string[] SpellcastingCountKeys = ["cantrips_known", "prepared_spells", "spells_known"];

    private const string SpellSlotKeyPrefix = "spell_slots_level_";

    /// <summary>
    /// Level values that are distances, by upstream key, printed with their unit. Upstream stores bare numbers, and a
    /// bare "10" under Unarmored Movement reads as a speed of 10 feet where the SRD's column (both editions) says
    /// "+10 ft.", a speed increase. 2014 names that key <c>unarmored_movement</c> and 2024
    /// <c>unarmored_movement_bonus</c>, so signing only <c>*_bonus</c> keys showed the same rule two ways side by side in
    /// a comparison. The paladin's aura range is feet as well.
    /// </summary>
    private static readonly Dictionary<string, Func<long, string>> FeetValues = new(StringComparer.Ordinal)
    {
        ["unarmored_movement"] = SpeedIncrease,
        ["unarmored_movement_bonus"] = SpeedIncrease,
        ["aura_range"] = feet => $"{feet.ToString(CultureInfo.InvariantCulture)} ft.",
    };

    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var body = doc.Kind switch
        {
            SrdKinds.Class => ClassBody(doc, lookup),
            SrdKinds.Subclass => SubclassBody(doc, lookup),
            SrdKinds.Level => LevelBody(doc),
            SrdKinds.Feature => FeatureBody(doc, lookup),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(body) ? GenericMarkdown.Body(doc, lookup) : body;
    }

    private static string ClassBody(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var header = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Hit Die", root.Int("hit_die") is { } die ? $"d{die.ToString(CultureInfo.InvariantCulture)}" : null),
            SrdMarkdownText.Field("Primary Ability", PrimaryAbility(root)),
            SrdMarkdownText.Field("Saving Throws", SrdMarkdownText.LinkList(root.Arr("saving_throws"))),
            SrdMarkdownText.Field("Proficiencies", SrdMarkdownText.LinkList(root.Arr("proficiencies"))),
            SrdChoiceMarkdown.Bullets("Proficiency Choices", root.Arr("proficiency_choices").Select(SrdChoiceMarkdown.Describe)),
            SrdMarkdownText.Field("Starting Equipment", SrdChoiceMarkdown.EquipmentList(root.Arr("starting_equipment"))),
            SrdChoiceMarkdown.Bullets("Starting Equipment Options",
                root.Arr("starting_equipment_options").Select(SrdChoiceMarkdown.Describe)),
            MulticlassingLines(root.Obj("multi_classing")),
            SrdMarkdownText.Field("Spellcasting", Spellcasting(root.Obj("spellcasting"))),
        ]);

        return SrdMarkdownText.Blocks(
        [
            header,
            SrdMarkdownText.Section("Levels", LevelTable(lookup.ClassLevels(doc.Edition, doc.Slug))),
            SrdMarkdownText.Section("Subclasses", SrdMarkdownText.LinkList(root.Arr("subclasses"))),
        ]);
    }

    // 2024 only. The desc is the SRD's own wording ("Strength or Dexterity"); the references are the fallback.
    private static string? PrimaryAbility(JsonElement root)
    {
        if (root.Obj("primary_ability") is not { } primary)
        {
            return null;
        }

        if (primary.Str("desc") is { } desc && !string.IsNullOrWhiteSpace(desc))
        {
            return desc;
        }

        var abilities = primary.Arr("ability_scores");
        if (abilities.Count > 0)
        {
            return string.Join(" and ", abilities.Select(SrdMarkdownText.Link));
        }

        return primary.Obj("ability_score_options") is { } options ? SrdChoiceMarkdown.Describe(options) : null;
    }

    private static string? MulticlassingLines(JsonElement? multiclassing)
    {
        if (multiclassing is not { } mc)
        {
            return null;
        }

        // "prerequisites" must all be met; "prerequisite_options" is a choose-one (fighter: STR 13+ or DEX 13+).
        var required = mc.Arr("prerequisites").Select(SrdChoiceMarkdown.ScorePrerequisite).Where(p => p is not null).ToList();
        var either = mc.Obj("prerequisite_options") is { } options
            ? string.Join(" or ", SrdChoiceMarkdown.OptionsOf(options).Select(SrdChoiceMarkdown.Option).Where(o => o is not null))
            : null;
        var prerequisites = string.Join(" and ", required.Append(either).Where(p => !string.IsNullOrWhiteSpace(p)));

        return SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Multiclassing Prerequisites", prerequisites),
            SrdMarkdownText.Field("Multiclassing Proficiencies", SrdMarkdownText.LinkList(mc.Arr("proficiencies"))),
            SrdChoiceMarkdown.Bullets("Multiclassing Proficiency Choices",
                mc.Arr("proficiency_choices").Select(SrdChoiceMarkdown.Describe)),
        ]);
    }

    // The class's long spellcasting rules (its "info" sections, ~3,000 characters) stay out of the class body. The level
    // table links the class's Spellcasting (or Pact Magic) feature, whose body has them: a 2014 feature through its
    // reference (see Reference), a 2024 feature in its own text.
    private static string? Spellcasting(JsonElement? spellcasting)
    {
        if (spellcasting is not { } casting)
        {
            return null;
        }

        var ability = casting.Obj("spellcasting_ability") is { } a ? SrdMarkdownText.Link(a) : null;
        var level = casting.Int("level") is { } l ? $"from level {l.ToString(CultureInfo.InvariantCulture)}" : null;
        return string.Join(", ", new[] { ability, level }.Where(p => p is not null));
    }

    /// <summary>
    /// The class table: Level | PB | Features | every class-specific column | the spellcasting columns that are ever
    /// non-zero. Class-specific columns whose values are lists (2014 sorcerer "creating spell slots") are left out: a
    /// list does not fit a cell, and the level record shows it.
    /// </summary>
    private static string? LevelTable(IReadOnlyList<SrdDocument> levels)
    {
        if (levels.Count == 0)
        {
            return null;
        }

        var specificKeys = TabularKeys(levels, "class_specific");
        var spellKeys = SpellcastingKeys(levels);

        var headers = new List<string> { "Level", "PB", "Features" };
        headers.AddRange(specificKeys.Select(ColumnName));
        headers.AddRange(spellKeys.Select(ColumnName));

        var rows = levels.Select(level =>
        {
            var root = level.Root;
            var row = new List<string>
            {
                root.Int("level")?.ToString(CultureInfo.InvariantCulture) ?? "—",
                root.Int("prof_bonus") is { } pb ? SrdMarkdownText.Signed(pb) : "—",
                SrdMarkdownText.LinkList(root.Arr("features")),
            };
            row.AddRange(specificKeys.Select(key => CellValue(root.Obj("class_specific"), key)));
            row.AddRange(spellKeys.Select(key => CellValue(root.Obj("spellcasting"), key)));
            return (IReadOnlyList<string>)row;
        });

        return SrdMarkdownText.Table(headers, rows);
    }

    private static string CellValue(JsonElement? container, string key) =>
        container is { } c && c.TryGetProperty(key, out var value) ? Value(key, value) ?? "—" : "—";

    // Keys in first-seen order whose every value fits a table cell.
    private static List<string> TabularKeys(IEnumerable<SrdDocument> levels, string property)
    {
        var keys = new List<string>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            if (level.Root.Obj(property) is not { } values)
            {
                continue;
            }

            foreach (var entry in values.EnumerateObject())
            {
                if (Value(entry.Name, entry.Value) is null && entry.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    excluded.Add(entry.Name);
                }

                if (!keys.Contains(entry.Name))
                {
                    keys.Add(entry.Name);
                }
            }
        }

        return keys.Where(k => !excluded.Contains(k)).ToList();
    }

    // Spellcasting columns that are non-zero at some level, counts first, then slot levels 1–9, then anything unknown.
    private static List<string> SpellcastingKeys(IReadOnlyList<SrdDocument> levels)
    {
        var used = new List<string>();
        foreach (var level in levels)
        {
            if (level.Root.Obj("spellcasting") is not { } casting)
            {
                continue;
            }

            foreach (var entry in casting.EnumerateObject())
            {
                if (!used.Contains(entry.Name) && entry.Value.ValueKind == JsonValueKind.Number && entry.Value.GetDouble() != 0)
                {
                    used.Add(entry.Name);
                }
            }
        }

        return used.OrderBy(SpellcastingOrder).ToList();
    }

    private static int SpellcastingOrder(string key)
    {
        var count = Array.IndexOf(SpellcastingCountKeys, key);
        if (count >= 0)
        {
            return count;
        }

        return SlotLevel(key) is { } slot ? SpellcastingCountKeys.Length + slot : 100;
    }

    private static int? SlotLevel(string key) =>
        key.StartsWith(SpellSlotKeyPrefix, StringComparison.Ordinal) &&
        int.TryParse(key.AsSpan(SpellSlotKeyPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
            ? slot
            : null;

    /// <summary>
    /// A column heading for an upstream key: <c>spell_slots_level_3</c> → "3rd", <c>wild_shape_max_cr</c> →
    /// "Wild Shape Max CR", <c>rage_count</c> → "Rage Count". Upstream's key names are all the data says about a
    /// column, so they are shown rather than guessed into the SRD's own headings ("Rages").
    /// </summary>
    internal static string ColumnName(string key) =>
        SlotLevel(key) is { } slot ? Ordinal(slot) : SrdChoiceMarkdown.Humanize(key);

    /// <summary>
    /// One class-specific or spellcasting value as the SRD's tables print it, or null when it does not fit a cell (a
    /// list). Zero is "—" (the SRD's tables leave a feature you do not have yet blank, and "0 Ki Points" at level 1 would
    /// read as a real value); <c>*_die</c> sizes are dice ("d8"); distances carry their unit (<see cref="FeetValues"/>:
    /// "+10 ft."); other <c>*_bonus</c> values are signed; fractions are CRs ("1/4"); <c>{dice_count, dice_value}</c> is
    /// "3d6".
    /// </summary>
    internal static string? Value(string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                if (number == 0)
                {
                    return "—";
                }

                if (number == UnlimitedSentinel)
                {
                    return "Unlimited";
                }

                var text = number.ToString(CultureInfo.InvariantCulture);
                return key.EndsWith("_die", StringComparison.Ordinal) ? "d" + text
                    : FeetValues.TryGetValue(key, out var feet) ? feet(number)
                    : key.EndsWith("_bonus", StringComparison.Ordinal) ? SrdMarkdownText.Signed(number)
                    : text;
            case JsonValueKind.Number:
                return SrdJsonAccess.FormatNumber(value.GetDouble());
            case JsonValueKind.True:
                return "Yes";
            case JsonValueKind.False:
                return "No";
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Object when value.Int("dice_count") is { } count && value.Int("dice_value") is { } sides:
                return $"{count.ToString(CultureInfo.InvariantCulture)}d{sides.ToString(CultureInfo.InvariantCulture)}";
            default:
                return null;
        }
    }

    private static string SpeedIncrease(long feet) => $"{SrdMarkdownText.Signed(feet)} ft.";

    private static string Ordinal(int n) => n.ToString(CultureInfo.InvariantCulture) + (n switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });

    private static string SubclassBody(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var levels = lookup.SubclassLevels(doc.Edition, doc.Slug);
        var inlineFeatures = root.Arr("features");

        var header = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Class", root.Obj("class") is { } owner ? SrdMarkdownText.Link(owner) : null),
            SrdMarkdownText.Field("Subclass Type", root.Str("subclass_flavor")),
        ]);
        var summary = root.Str("summary") is { } s && !string.IsNullOrWhiteSpace(s) ? $"*{s.Trim()}*" : null;

        // 2024 subclasses carry their features inline; 2014 ones only through level records.
        var features = inlineFeatures.Count > 0
            ? InlineFeatures(inlineFeatures, levels, lookup)
            : LevelFeatures(levels, lookup);

        return SrdMarkdownText.Blocks(
        [
            header,
            summary,
            SrdProse.Join(root.Description()),
            SrdMarkdownText.Section("Features", features),
            SrdMarkdownText.Section("Spells", SubclassSpells(root.Arr("spells"))),
            SrdMarkdownText.Section("Subclass Table", SubclassTable(levels)),
        ]);
    }

    /// <summary>
    /// 2024 subclass features, in the subclass record's order, each headed with the ref of the matching feature record
    /// from the subclass's level records (same level, same name) and showing that record's text.
    ///
    /// <para>
    /// Upstream keeps every 2024 subclass feature twice, inline in the subclass and as a feature record, and the two
    /// copies drift: the inline Oath of Devotion Spells is a PDF extraction of the wrong table (no level 3, 9 or 17 oath
    /// spells, Paladin spell-list rows instead). The record is what <c>rules_get</c> returns for the ref in the heading,
    /// and it is what curated corrections (<see cref="SrdDocument.Corrections"/>) fix, so the body shows the record: the
    /// subclass page and the feature page then never tell a model two different things. The inline text is the fallback
    /// for a feature with no record.
    /// </para>
    /// <para>
    /// One inline name ("Circle of the Land Spells") has the first sentence of its description run into it; it still
    /// matches by prefix. The record holds that sentence, so it is only rescued from the name when the inline text is
    /// shown. Level-record features that match no inline feature are listed at the end so that every ref the levels hold
    /// is reachable.
    /// </para>
    /// </summary>
    private static string? InlineFeatures(IReadOnlyList<JsonElement> inline, IReadOnlyList<SrdDocument> levels, ISrdLookup lookup)
    {
        var refs = levels
            .SelectMany(level => level.Root.Arr("features").Select(feature => (Level: level.Root.Int("level"), Feature: feature)))
            .ToList();
        var matched = new HashSet<int>();
        var blocks = new List<string?>();

        foreach (var feature in inline)
        {
            var name = feature.Str("name")?.Trim() ?? "?";
            var level = feature.Int("level");

            var index = -1;
            for (var i = 0; i < refs.Count && index < 0; i++)
            {
                if (!matched.Contains(i) && refs[i].Level == level && refs[i].Feature.Str("name") is { } candidate &&
                    (name == candidate || name.StartsWith(candidate + " ", StringComparison.Ordinal)))
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                blocks.Add(SrdMarkdownText.Blocks([FeatureHeading(level, name), SrdProse.Join(feature.Description())]));
                continue;
            }

            matched.Add(index);
            var reference = refs[index].Feature;
            var heading = FeatureHeading(level, SrdMarkdownText.Link(reference));
            if (SrdChoiceMarkdown.Resolve(reference.Str("url"), lookup) is { } record)
            {
                blocks.Add(SrdMarkdownText.Blocks([heading, SrdChoiceMarkdown.CorrectionNotes(record), FeatureText(record, lookup)]));
                continue;
            }

            var refName = reference.Str("name")!;
            IReadOnlyList<string> paragraphs = name.Length > refName.Length
                ? [name[refName.Length..].Trim(), .. feature.Description()]
                : feature.Description();
            blocks.Add(SrdMarkdownText.Blocks([heading, SrdProse.Join(paragraphs)]));
        }

        var unmatched = refs.Where((_, i) => !matched.Contains(i)).Select(r => r.Feature).ToList();
        blocks.Add(SrdMarkdownText.Field("Other features at these levels", SrdMarkdownText.LinkList(unmatched)));
        return SrdMarkdownText.Blocks(blocks);
    }

    private static string? LevelFeatures(IReadOnlyList<SrdDocument> levels, ISrdLookup lookup)
    {
        var blocks = new List<string?>();
        foreach (var level in levels)
        {
            foreach (var reference in level.Root.Arr("features"))
            {
                var heading = FeatureHeading(level.Root.Int("level"), SrdMarkdownText.Link(reference));
                var feature = SrdChoiceMarkdown.Resolve(reference.Str("url"), lookup);
                blocks.Add(feature is null
                    ? heading
                    : SrdMarkdownText.Blocks([heading, SrdChoiceMarkdown.CorrectionNotes(feature), FeatureText(feature, lookup)]));
            }
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    private static string FeatureHeading(long? level, string title) =>
        level is { } l ? $"#### Level {l.ToString(CultureInfo.InvariantCulture)}: {title}" : $"#### {title}";

    /// <summary>
    /// 2014 subclass spells (domain, oath, circle, patron lists) as Level | Spells tables. Spells that also require a
    /// feature (2014 Land: one list per terrain) are grouped under that feature, in data order, so each terrain's list
    /// reads as the SRD's table does.
    /// </summary>
    private static string? SubclassSpells(IReadOnlyList<JsonElement> spells)
    {
        if (spells.Count == 0)
        {
            return null;
        }

        var groups = spells
            .GroupBy(spell => string.Join(", ", Prerequisites(spell, "level", invert: true).Select(SrdMarkdownText.Link)))
            .ToList();

        var blocks = new List<string?>();
        foreach (var group in groups)
        {
            var rows = group
                .GroupBy(spell => string.Join(", ", Prerequisites(spell, "level", invert: false).Select(SrdMarkdownText.Link)))
                .Select(byLevel => (IReadOnlyList<string>)new[]
                {
                    byLevel.Key,
                    SrdMarkdownText.LinkList(byLevel.Select(s => s.Obj("spell")).OfType<JsonElement>()),
                });

            var table = SrdMarkdownText.Table(["Level", "Spells"], rows);
            blocks.Add(group.Key.Length > 0 ? $"**{group.Key}**\n\n{table}" : table);
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    // A subclass spell's prerequisites of one type (or, inverted, of every other type).
    private static IEnumerable<JsonElement> Prerequisites(JsonElement spell, string type, bool invert) =>
        spell.Arr("prerequisites").Where(p => (p.Str("type") == type) != invert);

    // 2014 subclass_specific values (Devotion's aura range, Lore's magical secrets level) as Level | … rows.
    private static string? SubclassTable(IReadOnlyList<SrdDocument> levels)
    {
        var withValues = levels.Where(l => l.Root.Obj("subclass_specific") is not null).ToList();
        var keys = TabularKeys(withValues, "subclass_specific");
        if (keys.Count == 0)
        {
            return null;
        }

        var headers = new List<string> { "Level" };
        headers.AddRange(keys.Select(ColumnName));
        var rows = withValues.Select(level =>
        {
            var row = new List<string> { level.Root.Int("level")?.ToString(CultureInfo.InvariantCulture) ?? "—" };
            row.AddRange(keys.Select(key => CellValue(level.Root.Obj("subclass_specific"), key)));
            return (IReadOnlyList<string>)row;
        });
        return SrdMarkdownText.Table(headers, rows);
    }

    private static string LevelBody(SrdDocument doc)
    {
        var root = doc.Root;
        var lines = new List<string?>
        {
            SrdMarkdownText.Field("Class", root.Obj("class") is { } owner ? SrdMarkdownText.Link(owner) : null),
            SrdMarkdownText.Field("Subclass", root.Obj("subclass") is { } subclass ? SrdMarkdownText.Link(subclass) : null),
            SrdMarkdownText.Field("Level", root.Int("level")?.ToString(CultureInfo.InvariantCulture)),
            SrdMarkdownText.Field("Proficiency Bonus", root.Int("prof_bonus") is { } pb ? SrdMarkdownText.Signed(pb) : null),
            SrdMarkdownText.Field("Ability Score Improvements",
                root.Int("ability_score_bonuses") is { } asi ? $"{asi.ToString(CultureInfo.InvariantCulture)} gained by this level" : null),
        };

        var features = root.Arr("features");
        lines.Add(SrdMarkdownText.Field("Features",
            features.Count > 0 ? SrdMarkdownText.LinkList(features) : "none gained at this level"));
        lines.AddRange(ValueLines(root.Obj("class_specific")));
        lines.AddRange(ValueLines(root.Obj("subclass_specific")));
        lines.AddRange(SpellcastingLines(root.Obj("spellcasting")));
        return SrdMarkdownText.Lines(lines);
    }

    // One "**Name** value" line per key; a list (2014 sorcerer spell-slot costs) is spelled out entry by entry.
    private static IEnumerable<string?> ValueLines(JsonElement? values)
    {
        if (values is not { } v)
        {
            yield break;
        }

        foreach (var entry in v.EnumerateObject())
        {
            var text = entry.Value.ValueKind == JsonValueKind.Array
                ? string.Join("; ", entry.Value.EnumerateArray().Select(DescribeObject).Where(t => t.Length > 0))
                : Value(entry.Name, entry.Value);
            yield return SrdMarkdownText.Field(ColumnName(entry.Name), string.IsNullOrEmpty(text) ? "—" : text);
        }
    }

    private static string DescribeObject(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
            ? string.Join(", ", item.EnumerateObject().Select(p => $"{ColumnName(p.Name)} {Value(p.Name, p.Value) ?? "—"}"))
            : Value(string.Empty, item) ?? string.Empty;

    private static IEnumerable<string?> SpellcastingLines(JsonElement? spellcasting)
    {
        if (spellcasting is not { } casting)
        {
            yield break;
        }

        var slots = new List<string>();
        foreach (var entry in casting.EnumerateObject().OrderBy(e => SpellcastingOrder(e.Name)))
        {
            if (SlotLevel(entry.Name) is { } slot)
            {
                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.GetDouble() != 0)
                {
                    slots.Add($"{Ordinal(slot)} {Value(entry.Name, entry.Value)}");
                }
            }
            else
            {
                yield return SrdMarkdownText.Field(ColumnName(entry.Name), Value(entry.Name, entry.Value));
            }
        }

        // No slots at this level (2014 paladin 1) means no line: "Spell Slots" with nothing after it would mislead.
        yield return SrdMarkdownText.Field("Spell Slots", string.Join(" · ", slots));
    }

    private static string FeatureBody(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var header = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Class", root.Obj("class") is { } owner ? SrdMarkdownText.Link(owner) : null),
            SrdMarkdownText.Field("Subclass", root.Obj("subclass") is { } subclass ? SrdMarkdownText.Link(subclass) : null),
            SrdMarkdownText.Field("Level", FeatureLevel(root)),
            SrdMarkdownText.Field("Parent", root.Obj("parent") is { } parent ? SrdMarkdownText.Link(parent) : null),
        ]);

        return SrdMarkdownText.Blocks([header, FeatureText(doc, lookup)]);
    }

    // 2014: a number. 2024: a level reference ("Fighter 3"), which is itself a record worth linking.
    private static string? FeatureLevel(JsonElement root) =>
        root.Int("level")?.ToString(CultureInfo.InvariantCulture) ??
        (root.Obj("level") is { } level ? SrdMarkdownText.Link(level) : null);

    /// <summary>
    /// What a feature says, without the class/subclass/level header: prerequisites, its text, its options and what it
    /// refers to. Shared by the feature body and the subclass body, which already names the class and level.
    /// </summary>
    private static string? FeatureText(SrdDocument feature, ISrdLookup lookup)
    {
        var root = feature.Root;
        return SrdMarkdownText.Blocks(
        [
            SrdMarkdownText.Field("Prerequisites", FeaturePrerequisites(root, lookup)),
            SrdProse.Join(root.Description()),
            FeatureSpecific(root, lookup),
            Reference(root, lookup),
        ]);
    }

    /// <summary>
    /// 2014 feature prerequisites: <c>{type: level, level: 5}</c> → "level 5"; a spell or feature URL → its link
    /// ("Pact of the Blade (<c>2014/feature/pact-of-the-blade</c>)"). Null when there are none.
    /// </summary>
    private static string? FeaturePrerequisites(JsonElement root, ISrdLookup lookup)
    {
        var parts = root.Arr("prerequisites")
            .Select(p => p.Str("type") switch
            {
                "level" when p.Int("level") is { } level => $"level {level.ToString(CultureInfo.InvariantCulture)}",
                { } type when p.Str(type) is { } url => SrdChoiceMarkdown.ResolveLink(url, lookup),
                _ => null,
            })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>
    /// 2014 <c>feature_specific</c>: choices between sub-features (Fighting Style, Pact Boon, Metamagic) and the
    /// invocation list become option bullets with each option's own text, because "which fighting styles are there and
    /// what do they do" is the question these features answer; other choices (expertise, favored enemy type) become one
    /// line.
    /// </summary>
    private static string? FeatureSpecific(JsonElement root, ISrdLookup lookup)
    {
        if (root.Obj("feature_specific") is not { } specific)
        {
            return null;
        }

        var parent = root.Description();
        var blocks = new List<string?>();
        foreach (var entry in specific.EnumerateObject())
        {
            var label = entry.Name is "subfeature_options" ? "Options" : SrdChoiceMarkdown.Humanize(entry.Name);
            var value = entry.Value;

            if (value.ValueKind == JsonValueKind.Array)
            {
                blocks.Add(SrdChoiceMarkdown.OptionBullets(label, null, value.EnumerateArray().ToList(), lookup, parent, Prerequisite));
            }
            else if (SrdChoiceMarkdown.RecordOptions(value, SrdKinds.Feature) is { } options)
            {
                blocks.Add(SrdChoiceMarkdown.OptionBullets(label, value.Int("choose"), options, lookup, parent, Prerequisite));
            }
            else
            {
                blocks.Add(SrdMarkdownText.Field(label, SrdChoiceMarkdown.Describe(value)));
            }
        }

        return SrdMarkdownText.Blocks(blocks);

        // An invocation's own prerequisites, the first thing a player needs to know about it.
        string? Prerequisite(SrdDocument option) =>
            FeaturePrerequisites(option.Root, lookup) is { } p ? $"Prerequisite: {p}." : null;
    }

    /// <summary>
    /// 2014 <c>reference</c>. A class-spellcasting URL (<c>/api/2014/classes/wizard/spellcasting</c>) points at rules
    /// text no record owns, the class's spellcasting sections, so they are rendered here: the Spellcasting feature's own
    /// text is one sentence and the rules a player needs are in those sections. Any other resolvable URL is a link.
    /// </summary>
    private static string? Reference(JsonElement root, ISrdLookup lookup)
    {
        if (root.Str("reference") is not { } url)
        {
            return null;
        }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["api", var urlEdition, "classes", var classSlug, "spellcasting"] &&
            lookup.Get(urlEdition, SrdKinds.Class, classSlug) is { } owner &&
            owner.Root.Obj("spellcasting") is { } spellcasting)
        {
            return SpellcastingRules(spellcasting);
        }

        return SrdMarkdownText.Field("Reference", SrdChoiceMarkdown.ResolveLink(url, lookup));
    }

    private static string? SpellcastingRules(JsonElement spellcasting)
    {
        var sections = spellcasting.Arr("info")
            .Select(info =>
            {
                var paragraphs = info.Paragraphs("desc").ToList();
                if (paragraphs.Count > 0 && info.Str("name") is { } name)
                {
                    paragraphs[0] = $"***{name.Trim()}.*** {paragraphs[0].Trim()}";
                }

                return SrdProse.Join(paragraphs);
            });

        return SrdMarkdownText.Blocks(
        [
            SrdMarkdownText.Field("Spellcasting Ability",
                spellcasting.Obj("spellcasting_ability") is { } ability ? SrdMarkdownText.Link(ability) : null),
            .. sections,
        ]);
    }
}
