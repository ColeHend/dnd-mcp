using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for spells, one formatter for both editions' shapes.
///
/// <para>
/// The description is the rules text and is rendered whole, its words unchanged. Only its paragraph breaks are fixed
/// (<see cref="SrdProse"/>): 2014 stores animate objects' statistics table one row per array element, and 2024
/// separates paragraphs with a bare newline, so a plain join would break the table or run paragraphs together.
/// </para>
/// <para>
/// The structured fields (<c>damage_at_slot_level</c>, <c>dc</c>, <c>heal_at_slot_level</c>) are shown after it as
/// compact lines, because they answer "how much at slot 5?" at a glance, but they are the API's reading of the text,
/// not the text: the 2014 eldritch blast table says 1d10 at every level (the extra beams exist only in the prose),
/// and 2024 spells carry only the base dice. Upstream's dice strings ("1d8 + MOD", "4d6 OR 5d6", a flat "20") are
/// shown as stored, never parsed.
/// </para>
/// </summary>
internal static partial class SpellMarkdown
{
    private static readonly Dictionary<string, string> AbilityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "Strength", ["dex"] = "Dexterity", ["con"] = "Constitution",
        ["int"] = "Intelligence", ["wis"] = "Wisdom", ["cha"] = "Charisma",
    };

    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var (range, components) = RangeAndComponents(root);

        return SrdMarkdownText.Blocks(
        [
            Subtitle(root),
            SrdMarkdownText.Lines(
            [
                SrdMarkdownText.Field("Casting Time", root.Str("casting_time")),
                SrdMarkdownText.Field("Range", WithArea(range, root.Obj("area_of_effect"))),
                SrdMarkdownText.Field("Components", ComponentsText(components, root.Str("material"))),
                SrdMarkdownText.Field("Duration", Duration(root)),
                SrdMarkdownText.Field("Classes", SrdMarkdownText.LinkList(root.Arr("classes"))),
                SrdMarkdownText.Field("Subclasses", SrdMarkdownText.LinkList(root.Arr("subclasses"))),
            ]),
            SrdProse.Join(root.Description()),
            HigherLevel(doc),
            SrdMarkdownText.Lines(Mechanics(root)),
        ]);
    }

    /// <summary>"*Level 3 Evocation*", "*Evocation cantrip*", with " (ritual)" when the spell can be cast as one.</summary>
    private static string? Subtitle(JsonElement root)
    {
        var school = root.Obj("school")?.Str("name");
        var text = root.Int("level") switch
        {
            0 => school is null ? "Cantrip" : $"{school} cantrip",
            { } level => string.Create(CultureInfo.InvariantCulture, $"Level {level} {school ?? "spell"}"),
            null => school,
        };

        if (text is null)
        {
            return null;
        }

        return root.Bool("ritual") == true ? $"*{text} (ritual)*" : $"*{text}*";
    }

    /// <summary>
    /// The range and the component letters, repairing one upstream scraping fault: nine 2024 spells (guidance,
    /// resistance, power word kill, …) have an empty <c>components</c> array and their components glued onto the
    /// range ("Touch Component: V, S"). Shown as stored, the spell would read as having no components and a range of
    /// "Touch Component: V, S". The text is only moved, never reworded.
    /// </summary>
    internal static (string? Range, IReadOnlyList<string> Components) RangeAndComponents(JsonElement root)
    {
        var range = root.Str("range");
        var components = root.Arr("components")
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .ToList();

        if (components.Count == 0 && range is not null && ComponentsInRange().Match(range) is { Success: true } glued)
        {
            return (glued.Groups["range"].Value, glued.Groups["components"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return (range, components);
    }

    /// <summary>
    /// 2014's area of effect after a Self range, "Self (15-foot cone)", the one place SRD 5.1 prints an area in the
    /// Range line. For every other spell the area is upstream's approximation, and several contradict the text: flame
    /// strike stores its 40-foot height as the size ("(40-foot cylinder)" for a 10-foot radius), forbiddance and guards
    /// and wards turn square feet of floor into 40,000- and 2,500-foot cubes, fire storm's ten 10-foot cubes become one
    /// 100-foot cube. The header line is what gets quoted for "how big is it?", so those are left to the description,
    /// which states the area. 2024 stores no area at all.
    /// </summary>
    private static string? WithArea(string? range, JsonElement? area)
    {
        if (range?.StartsWith("Self", StringComparison.Ordinal) != true ||
            area is not { } a || a.Str("type") is not { Length: > 0 } shape || a.Int("size") is not { } size)
        {
            return range;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{range} ({size}-foot {shape})");
    }

    // "V, S, M (a ball of bat guano and sulfur)"; the material text is shown as stored.
    internal static string? ComponentsText(IReadOnlyList<string> components, string? material)
    {
        if (components.Count == 0)
        {
            return null;
        }

        var letters = string.Join(", ", components);
        return components.Contains("M") && !string.IsNullOrWhiteSpace(material) ? $"{letters} ({material.Trim()})" : letters;
    }

    /// <summary>
    /// "Concentration, up to 1 minute" for concentration spells, as both SRDs print it. The data keeps concentration
    /// as a separate flag and the duration as "Up to 1 minute"; shown alone, the duration hides the one fact that
    /// most often decides whether the spell can be cast. A duration that already says so (2024 protection from evil
    /// and good: "Concentration up to 10 minutes", flag false) is left as it is.
    /// </summary>
    private static string? Duration(JsonElement root)
    {
        var duration = root.Str("duration")?.Trim();
        if (root.Bool("concentration") != true ||
            duration?.StartsWith("Concentration", StringComparison.OrdinalIgnoreCase) == true)
        {
            return duration;
        }

        if (string.IsNullOrEmpty(duration))
        {
            return "Concentration";
        }

        return duration.StartsWith("Up to", StringComparison.Ordinal)
            ? "Concentration, u" + duration[1..]
            : "Concentration, " + duration;
    }

    /// <summary>
    /// "***At Higher Levels.*** …" (2014) or "***Using a Higher-Level Spell Slot.*** …" (2024), each SRD's own
    /// heading. 2024 cantrips have no <c>higher_level</c>: their Cantrip Upgrade is the last paragraph of the
    /// description and is already shown there.
    /// </summary>
    private static string? HigherLevel(SrdDocument doc)
    {
        var lines = doc.Root.Paragraphs("higher_level")
            .SelectMany(p => p.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var label = doc.Edition == SrdEdition.Edition2014 ? "At Higher Levels." : "Using a Higher-Level Spell Slot.";
        lines[0] = $"***{label}*** {lines[0]}";
        return SrdProse.Join(lines);
    }

    /// <summary>
    /// The structured lines: damage per entry (flame strike has fire and radiant), healing, the save and the attack
    /// type, each only where the data has it and the text agrees with the label.
    ///
    /// <para>
    /// The label is a rules claim, so it has to match what the spell does. Upstream filed three 2014 spells' numbers
    /// under the wrong mechanic: sleep's 5d8 hit-point pool as damage, false life's temporary hit points and aid's hit
    /// point maximum as healing. "**Damage** 5d8" answers "how much damage does Sleep do?" with a number the spell never
    /// deals. So an untyped damage entry is shown only when the description calls its dice damage (prismatic spray's
    /// 10d6 is), and a heal table is labelled by what the description says the target gets: hit points regained or
    /// restored (Healing), temporary hit points, or neither (aid), in which case the description alone says it.
    /// </para>
    /// <para>
    /// Typed damage lines are shown only when they name every damage type the description deals dice of. The 2024 data
    /// keeps one damage entry per spell, so Meteor Swarm read "**Damage** Fire — 20d6" beside prose dealing 20d6 Fire and
    /// 20d6 Bludgeoning, and Prismatic Spray "Acid" under a table of five colours: the line alone answers "how much damage"
    /// with half of it, and beside 2014's two lines reads as if 2024 dropped a type. When the entries name fewer types
    /// than the prose, the typed lines are left out and the description states the damage.
    /// </para>
    /// </summary>
    private static IEnumerable<string?> Mechanics(JsonElement root)
    {
        var level = root.Int("level");
        var description = string.Join("\n", root.Description());

        // 2014 stores damage as an array of entries, 2024 as a single object.
        List<JsonElement> entries = root.Obj("damage") is { } single
            ? [single]
            : root.Arr("damage").Where(e => e.ValueKind == JsonValueKind.Object).ToList();

        var shown = new List<(JsonElement? Type, JsonElement? Table, string Scaling)>();
        foreach (var entry in entries)
        {
            var table = entry.Obj("damage_at_slot_level");
            var scaling = Scaling(table, "slot", level);
            if (scaling is null)
            {
                table = entry.Obj("damage_at_character_level");
                scaling = Scaling(table, "level", null);
            }

            if (scaling is not null)
            {
                shown.Add((entry.Obj("damage_type"), table, scaling));
            }
        }

        var typesShown = shown
            .Select(e => e.Type is { } t ? (t.Str("index") ?? t.Str("name"))?.ToLowerInvariant() : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var typedLinesAgree = ProseDamageTypes(description).IsSubsetOf(typesShown);

        foreach (var (typeElement, table, scaling) in shown)
        {
            if (typeElement is { } type)
            {
                if (typedLinesAgree)
                {
                    yield return SrdMarkdownText.Field("Damage", $"{SrdMarkdownText.Link(type)} — {scaling}");
                }
            }
            else if (FirstDice(table) is { } dice && DescribedAsDamage(description, dice))
            {
                yield return SrdMarkdownText.Field("Damage", scaling);
            }
        }

        if (HealLabel(description) is { } healLabel)
        {
            yield return SrdMarkdownText.Field(healLabel, Scaling(root.Obj("heal_at_slot_level"), "slot", level));
        }

        yield return SrdMarkdownText.Field("Save", Save(root.Obj("dc")));
        yield return SrdMarkdownText.Field("Attack",
            root.Str("attack_type") is { Length: > 0 } attack ? $"{attack} spell attack" : null);
    }

    /// <summary>
    /// A scaling table as one line: "8d6 (slot 3), 9d6 (4), …, 14d6 (9)", or by character level "1d10 (level 1),
    /// 2d10 (5), 3d10 (11), 4d10 (17)". A single entry at the spell's own level (every 2024 spell; 2024 cantrips key
    /// it "0") is just the dice. Keys sort numerically so "10" never lands between "1" and "2".
    /// </summary>
    private static string? Scaling(JsonElement? table, string unit, long? spellLevel)
    {
        if (table is not { } t)
        {
            return null;
        }

        var steps = t.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String &&
                        long.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(p => (Key: long.Parse(p.Name, CultureInfo.InvariantCulture), Dice: p.Value.GetString()!))
            .OrderBy(s => s.Key)
            .ToList();

        if (steps.Count == 0)
        {
            return null;
        }

        if (steps.Count == 1 && steps[0].Key == spellLevel)
        {
            return steps[0].Dice;
        }

        return string.Join(", ", steps.Select((s, i) => i == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{s.Dice} ({unit} {s.Key})")
            : string.Create(CultureInfo.InvariantCulture, $"{s.Dice} ({s.Key})")));
    }

    // The dice of the lowest slot or character level, "5d8" for sleep.
    private static string? FirstDice(JsonElement? table) =>
        table?.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String &&
                        long.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .OrderBy(p => long.Parse(p.Name, CultureInfo.InvariantCulture))
            .Select(p => p.Value.GetString())
            .FirstOrDefault();

    /// <summary>
    /// The damage types the description deals dice of, lower-case: "20d6 Fire damage and 20d6 Bludgeoning damage" is fire
    /// and bludgeoning; "3d8 Radiant damage (if you are good or neutral) or 3d8 Necrotic damage" radiant and necrotic.
    /// </summary>
    internal static IReadOnlySet<string> ProseDamageTypes(string description) =>
        DiceOfADamageType().Matches(description).Select(m => m.Groups["type"].Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    // "takes 10d6 fire damage": the dice, then "damage" later in the same clause.
    private static bool DescribedAsDamage(string description, string dice) =>
        Regex.IsMatch(description, Regex.Escape(dice) + @"[^.;:\n]*\bdamage\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // What a heal table gives, by the description's own words; null when it is neither (aid raises the maximum).
    private static string? HealLabel(string description) =>
        TemporaryHitPoints().IsMatch(description) ? "Temporary Hit Points"
        : RegainsHitPoints().IsMatch(description) ? "Healing"
        : null;

    /// <summary>
    /// "Dexterity, half on success". Only "half" is spelled out: upstream's "none" and "other" cover everything from
    /// "no effect" to "the target always takes the damage" (2014 feeblemind), and the description says which.
    /// </summary>
    private static string? Save(JsonElement? dc)
    {
        if (dc is not { } d || d.Obj("dc_type") is not { } type)
        {
            return null;
        }

        var ability = type.Str("index") is { } index && AbilityNames.TryGetValue(index, out var full)
            ? full
            : type.Str("name");
        if (ability is null)
        {
            return null;
        }

        return d.Str("dc_success") == "half" ? $"{ability}, half on success" : ability;
    }

    // "20d6 Fire damage", "1d4 + 1 Force damage", "2d8 cold damage": dice (with a flat bonus) directly before a damage type.
    [GeneratedRegex(
        @"\b\d+d\d+(?:\s*[+−-]\s*\d+)?\s+(?<type>acid|bludgeoning|cold|fire|force|lightning|necrotic|piercing|poison|psychic|radiant|slashing|thunder)\s+damage\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiceOfADamageType();

    [GeneratedRegex(@"\btemporary hit points\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryHitPoints();

    [GeneratedRegex(@"\b(?:regains?|restores?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegainsHitPoints();

    // "Touch Component: V, S" / "60 feet Components: V".
    [GeneratedRegex(@"^(?<range>.*?)\s+Components?:\s*(?<components>.+)$")]
    private static partial Regex ComponentsInRange();
}
