using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// The "at a glance" table at the top of a 2014 vs 2024 comparison: the few fields that differ in a way prose hides
/// (a spell's level, school or range; a monster's AC, HP, CR or ability scores), one row each, with a column that says
/// whether the row changed.
///
/// <para>
/// Two kinds of change are told apart, because the 2024 SRD renamed much of its vocabulary without changing the rule:
/// "1 action" became "Action", "Up to 1 minute" became "up to 1 minute", "19 (natural armor)" became "19". Marking
/// those the same as a real change would bury the rows that matter (cure wounds moved from Evocation to Abjuration;
/// the red dragon's Charisma went from 21 to 23) under noise; hiding them would tell a reader comparing the two texts
/// that identical-looking values are identical strings. So each row compares a normalised key: a different key is
/// "yes", the same key shown differently is "wording only", and the same text is "—".
/// </para>
/// <para>
/// A key has to hold everything that decides the rule, or "wording only" (or "—") hides a real change: a spell's
/// components include the material's cost and whether the spell consumes it (bless now needs a 5+ GP Holy Symbol,
/// forcecage now eats its ruby dust). Where the data cannot decide, the row says so instead of guessing: the 2014 data
/// stores no reaction or bonus-action trigger, so a 2024 trigger (counterspell's new "with Verbal, Somatic, or Material
/// components", shining smite's "immediately after hitting") has nothing to be compared with.
/// </para>
/// <para>
/// Only spells and monsters get a table. Other kinds are mostly prose, where a table of fields adds nothing the two
/// bodies below do not already show.
/// </para>
/// </summary>
internal static partial class SrdComparisonMarkdown
{
    /// <summary>The "Changed" cell for a row whose values differ in substance.</summary>
    public const string Changed = "yes";

    /// <summary>The "Changed" cell for a row whose values mean the same but are worded differently.</summary>
    public const string WordingOnly = "wording only";

    /// <summary>
    /// The "Changed" cell when one edition's data states a casting-time trigger and the other's does not, so whether
    /// the trigger changed cannot be read from the data.
    /// </summary>
    public static string TriggerUnknown(string editionWithoutTrigger) =>
        $"unknown (no {editionWithoutTrigger} trigger in the data)";

    /// <summary>A markdown table comparing the two, or null for kinds where a table adds nothing over the text.</summary>
    public static string? Glance(SrdDocument doc2014, SrdDocument doc2024)
    {
        if (doc2014.Kind != doc2024.Kind)
        {
            return null;
        }

        IReadOnlyList<Row>? rows = doc2014.Kind switch
        {
            SrdKinds.Spell => SpellRows(doc2014.Root, doc2024.Root),
            SrdKinds.Monster => MonsterRows(doc2014.Root, doc2024.Root),
            _ => null,
        };

        return rows is null
            ? null
            : SrdMarkdownText.Table(
                ["Field", SrdEdition.Edition2014, SrdEdition.Edition2024, "Changed"],
                rows.Select(r => (IReadOnlyList<string>)
                    new[] { r.Field, r.Shown2014 ?? string.Empty, r.Shown2024 ?? string.Empty, Verdict(r) }));
    }

    /// <summary>
    /// One row: what each edition shows, and the key its value is compared by; or a fixed verdict when the keys cannot
    /// decide it.
    /// </summary>
    private sealed record Row(
        string Field, string? Shown2014, string? Shown2024, string? Key2014, string? Key2024, string? FixedVerdict = null);

    private static string Verdict(Row row) =>
        row.FixedVerdict is { } verdict ? verdict
        : !string.Equals(row.Key2014, row.Key2024, StringComparison.Ordinal) ? Changed
        : !string.Equals(row.Shown2014, row.Shown2024, StringComparison.Ordinal) ? WordingOnly
        : string.Empty;

    // A row compared as text, ignoring case, spacing and a trailing full stop.
    private static Row Text(string field, string? shown2014, string? shown2024) =>
        new(field, shown2014, shown2024, TextKey(shown2014), TextKey(shown2024));

    private static string? TextKey(string? text) =>
        text is null ? null : Whitespace().Replace(text, " ").Trim().TrimEnd('.').ToLowerInvariant();

    private static IReadOnlyList<Row> SpellRows(JsonElement a, JsonElement b) =>
    [
        new("Level", SpellLevel(a), SpellLevel(b), Number(a.Int("level")), Number(b.Int("level"))),
        Text("School", a.Obj("school")?.Str("name"), b.Obj("school")?.Str("name")),
        CastingTime(a.Str("casting_time"), b.Str("casting_time")),
        Text("Range", SpellMarkdown.RangeAndComponents(a).Range, SpellMarkdown.RangeAndComponents(b).Range),
        Components(a, b),
        new("Duration", a.Str("duration"), b.Str("duration"), DurationKey(a.Str("duration")), DurationKey(b.Str("duration"))),
        Text("Concentration", YesNo(Concentration(a)), YesNo(Concentration(b))),
        Text("Ritual", YesNo(a.Bool("ritual")), YesNo(b.Bool("ritual"))),
        Set("Classes", Names(a.Arr("classes")), Names(b.Arr("classes"))),
    ];

    private static string? SpellLevel(JsonElement spell) => spell.Int("level") switch
    {
        0 => "cantrip",
        { } level => Number(level),
        null => null,
    };

    /// <summary>
    /// "1 action" and "Action" compare equal; 1 action → 1 minute is a real change. The trigger after the comma
    /// ("Reaction, which you take when you see a creature … casting a spell with Verbal, Somatic, or Material
    /// components") is part of the rule, so it is compared too when both sides state one. The 2014 data never does
    /// (it stores "1 reaction"; SRD 5.1's trigger is in neither the casting time nor, for shield or counterspell, the
    /// description), so a trigger on one side only gets <see cref="TriggerUnknown"/>: calling it "wording only" told a
    /// reader that shining smite's cast-after-the-hit timing and counterspell's new components restriction were
    /// unchanged, and calling it "yes" would say the same of shield, whose trigger only changed its wording.
    /// </summary>
    private static Row CastingTime(string? castingTime2014, string? castingTime2024)
    {
        var (head2014, trigger2014) = SplitTrigger(castingTime2014);
        var (head2024, trigger2024) = SplitTrigger(castingTime2024);
        string? fixedVerdict = null;
        if (head2014 == head2024 && (trigger2014 is null) != (trigger2024 is null))
        {
            fixedVerdict = TriggerUnknown(trigger2014 is null ? SrdEdition.Edition2014 : SrdEdition.Edition2024);
        }

        return new Row("Casting Time", castingTime2014, castingTime2024,
            trigger2014 is null ? head2014 : $"{head2014}, {trigger2014}",
            trigger2024 is null ? head2024 : $"{head2024}, {trigger2024}",
            fixedVerdict);
    }

    // "1 Reaction, which you take when…" → ("reaction", "which you take when…"), both as compare keys.
    private static (string? Head, string? Trigger) SplitTrigger(string? castingTime)
    {
        if (castingTime is null)
        {
            return (null, null);
        }

        var comma = castingTime.IndexOf(',', StringComparison.Ordinal);
        var head = LeadingOne().Replace(TextKey(comma >= 0 ? castingTime[..comma] : castingTime)!, string.Empty);
        var trigger = comma >= 0 ? TextKey(castingTime[(comma + 1)..]) : null;
        return (head, string.IsNullOrEmpty(trigger) ? null : trigger);
    }

    /// <summary>
    /// The letters as a set plus, when there is a material component, what it costs and whether the spell consumes it.
    /// The cell shows the material text as the body does, so a reworded material reads "wording only" and a changed
    /// price or consumption "yes" (imprisonment's 500 gp per Hit Die became 5,000+ GP).
    /// </summary>
    private static Row Components(JsonElement a, JsonElement b)
    {
        var components2014 = SpellMarkdown.RangeAndComponents(a).Components;
        var components2024 = SpellMarkdown.RangeAndComponents(b).Components;
        return new Row("Components",
            SpellMarkdown.ComponentsText(components2014, a.Str("material")),
            SpellMarkdown.ComponentsText(components2024, b.Str("material")),
            ComponentsKey(components2014, a.Str("material")),
            ComponentsKey(components2024, b.Str("material")));
    }

    private static string ComponentsKey(IReadOnlyList<string> components, string? material)
    {
        var letters = SetKey(components);
        if (!components.Any(c => c.Trim().Equals("M", StringComparison.OrdinalIgnoreCase)) || material is null)
        {
            return letters;
        }

        var costs = CoinAmount().Matches(material)
            .Select(m => $"{Amount(m.Groups["amount"].Value)} {Unit(m.Groups["unit"].Value)}")
            .Order(StringComparer.Ordinal);
        var consumed = Consumed().IsMatch(material) ? "consumed" : "kept";
        return $"{letters}; {string.Join(", ", costs)}; {consumed}";
    }

    // "1,500" → 1500; "a"/"one" copper coin → 1.
    private static long Amount(string amount) => amount.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        var digits => long.Parse(digits.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture),
    };

    // "GP", "gp", "gold pieces" → "gp"; "Copper Piece", "copper coin" → "cp".
    private static string Unit(string unit)
    {
        var lower = unit.ToLowerInvariant();
        return lower.Length == 2 ? lower : $"{lower[0]}p";
    }

    // "Up to 1 minute" (2014), "up to 1 minute" and "Concentration up to 10 minutes" (2024) compare by the time alone;
    // concentration has its own row. "24 hours" (2014) and "1 day" (2024) are the same time.
    private static string? DurationKey(string? duration) =>
        TextKey(duration) is { } key
            ? TwentyFourHours().Replace(LeadingConcentration().Replace(key, string.Empty), "1 day")
            : null;

    // The flag, or a duration that says so (2024 protection from evil and good states it only in the text), so this
    // row never contradicts the Duration line in the body below.
    private static bool Concentration(JsonElement spell) =>
        spell.Bool("concentration") == true ||
        spell.Str("duration")?.TrimStart().StartsWith("Concentration", StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<Row> MonsterRows(JsonElement a, JsonElement b)
    {
        var rows = new List<Row>
        {
            // A subtype is compared only against a record that has one: the 2024 data has none at all.
            new("Size/Type", MonsterMarkdown.SizeAndType(a), MonsterMarkdown.SizeAndType(b),
                TextKey(MonsterMarkdown.SizeAndType(a, withSubtype: HasProperty(b, "subtype"))),
                TextKey(MonsterMarkdown.SizeAndType(b, withSubtype: HasProperty(a, "subtype")))),
            Text("Alignment", a.Str("alignment"), b.Str("alignment")),
            new("Armor Class",
                MonsterMarkdown.ArmorClass(a, withRefs: false), MonsterMarkdown.ArmorClass(b, withRefs: false),
                ArmorClassKey(a), ArmorClassKey(b)),
            Text("Hit Points", MonsterMarkdown.HitPoints(a), MonsterMarkdown.HitPoints(b)),
            Text("Speed", MonsterMarkdown.Speed(a), MonsterMarkdown.Speed(b)),
            Text("Challenge", MonsterMarkdown.Challenge(a), MonsterMarkdown.Challenge(b)),
        };

        foreach (var (property, label) in MonsterMarkdown.Abilities)
        {
            rows.Add(new Row(label, MonsterMarkdown.AbilityScore(a, property), MonsterMarkdown.AbilityScore(b, property),
                Number(a.Int(property)), Number(b.Int(property))));
        }

        // Entry counts, so "2024 gained bonus actions" or "lost a legendary action" shows before the bodies are read.
        // Sections neither edition has (most monsters have no reactions) are left out.
        foreach (var (property, title) in MonsterMarkdown.EntrySections)
        {
            var count2014 = a.Arr(property).Count;
            var count2024 = b.Arr(property).Count;
            if (count2014 > 0 || count2024 > 0)
            {
                rows.Add(Text(title, Number(count2014), Number(count2024)));
            }
        }

        return rows;
    }

    // The AC values alone, as shown: "19 (natural armor)" and "19" are the same armor class.
    private static string ArmorClassKey(JsonElement monster) =>
        string.Join(",", MonsterMarkdown.ArmorClassValues(monster).Select(v => Number(v)));

    private static bool HasProperty(JsonElement record, string property) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(property, out _);

    // A row compared as an unordered set: "Wizard, Sorcerer" and "Sorcerer, Wizard" are the same classes.
    private static Row Set(string field, IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        new(field, Joined(a), Joined(b), SetKey(a), SetKey(b));

    private static string? Joined(IReadOnlyList<string> items) => items.Count == 0 ? null : string.Join(", ", items);

    private static string SetKey(IReadOnlyList<string> items) =>
        string.Join(",", items.Select(i => i.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal));

    private static IReadOnlyList<string> Names(IReadOnlyList<JsonElement> references) =>
        references.Select(r => r.Str("name") ?? r.Str("index")).OfType<string>().ToList();

    private static string? YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => null,
    };

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^1 (?=action|bonus action|reaction)")]
    private static partial Regex LeadingOne();

    [GeneratedRegex(@"^concentration,?\s*")]
    private static partial Regex LeadingConcentration();

    [GeneratedRegex(@"\b24 hours\b")]
    private static partial Regex TwentyFourHours();

    // "worth 1,500+ GP", "500gp", upstream's "250+, GP", "2 Copper Pieces", "a copper coin".
    [GeneratedRegex(@"\b(?<amount>\d{1,3}(?:,\d{3})+|\d+|an?|one|two|three|four|five|six|seven|eight|nine|ten)\s*\+?,?\s*(?<unit>[gsecp]p|(?:gold|silver|electrum|copper|platinum)\s+(?:pieces?|coins?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoinAmount();

    [GeneratedRegex(@"\bconsume", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Consumed();
}
