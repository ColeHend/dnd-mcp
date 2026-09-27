using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for monster stat blocks, one formatter for both editions' shapes.
///
/// <para>
/// The model quotes this to a table, so every entry's <c>desc</c> is rendered word for word (<see cref="SrdProse"/>
/// only turns its line breaks into paragraph and list breaks): it already holds the attack bonus, reach, damage and
/// save text, and re-deriving any of that from the structured fields would put two versions of the same number on
/// the page that can disagree (the 2014 data mislabels five half-damage saves as "none"; the prose is right).
/// Structured fields are used only for what the prose does not carry: the header lines, and the parts upstream
/// stripped out of the text.
/// </para>
/// <para>
/// Two such parts are easy to lose. Upstream removes "(Recharge 5–6)", "(3/Day)" and the like from entry names and
/// keeps them only in <c>usage</c>, so a formatter that prints names as stored shows a breath weapon usable every
/// turn; <see cref="EntryName"/> puts them back. And a 2024 "Spellcasting" desc ends with "…the following spells:"
/// while the spells themselves live only in <c>spellcasting.spells</c>; printing the desc alone lists no spells at
/// all, so <see cref="SpellList"/> renders them.
/// </para>
/// <para>
/// Nothing is invented. The data has no initiative and no legendary action count (neither edition stores them;
/// Phase 5 derives them), so neither is shown.
/// </para>
/// </summary>
internal static partial class MonsterMarkdown
{
    /// <summary>Ability score properties in stat-block order, with the column labels the SRD prints.</summary>
    internal static IReadOnlyList<(string Property, string Label)> Abilities { get; } =
    [
        ("strength", "STR"), ("dexterity", "DEX"), ("constitution", "CON"),
        ("intelligence", "INT"), ("wisdom", "WIS"), ("charisma", "CHA"),
    ];

    /// <summary>The entry arrays, in stat-block order, with the section heading each gets.</summary>
    internal static IReadOnlyList<(string Property, string Title)> EntrySections { get; } =
    [
        ("special_abilities", "Traits"),
        ("actions", "Actions"),
        ("bonus_actions", "Bonus Actions"),
        ("reactions", "Reactions"),
        ("legendary_actions", "Legendary Actions"),
    ];

    private static readonly Dictionary<string, string> AbilityShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "Str", ["dex"] = "Dex", ["con"] = "Con", ["int"] = "Int", ["wis"] = "Wis", ["cha"] = "Cha",
    };

    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        var blocks = new List<string?>
        {
            Subtitle(root),
            SrdProse.Join(root.Paragraphs("desc")),
            SrdMarkdownText.Lines(
            [
                SrdMarkdownText.Field("Armor Class", ArmorClass(root, withRefs: true)),
                SrdMarkdownText.Field("Hit Points", HitPoints(root)),
                SrdMarkdownText.Field("Speed", Speed(root)),
            ]),
            AbilityTable(root),
            SrdMarkdownText.Lines(
            [
                SrdMarkdownText.Field("Saving Throws", Proficiencies(root, "saving-throw-", SavingThrowLabel)),
                SrdMarkdownText.Field("Skills", Proficiencies(root, "skill-", SkillLabel)),
                SrdMarkdownText.Field("Damage Vulnerabilities", DamageList(root, "damage_vulnerabilities")),
                SrdMarkdownText.Field("Damage Resistances", DamageList(root, "damage_resistances")),
                SrdMarkdownText.Field("Damage Immunities", DamageList(root, "damage_immunities")),
                SrdMarkdownText.Field("Condition Immunities", ConditionImmunities(root)),
                SrdMarkdownText.Field("Senses", Senses(root)),
                SrdMarkdownText.Field("Languages", Languages(root)),
                ChallengeLine(root),
                SrdMarkdownText.Field("Gear", Gear(doc, lookup)),
                SrdMarkdownText.Field("Other forms", string.Join("; ", root.Arr("forms").Select(SrdMarkdownText.Link))),
            ]),
        };

        foreach (var (property, title) in EntrySections)
        {
            blocks.Add(SrdMarkdownText.Section(title, Entries(doc, property, lookup)));
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    /// <summary>
    /// "*Small humanoid (goblinoid), neutral evil*": size, type, subtype and alignment as the data spells them.
    /// </summary>
    private static string? Subtitle(JsonElement root)
    {
        var what = SizeAndType(root);
        var alignment = root.Str("alignment");
        var text = string.Join(", ", new[] { what, alignment }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? null : $"*{text}*";
    }

    /// <summary>"Small humanoid (goblinoid)", "Medium swarm of Tiny beasts", or null when the data has none of them.</summary>
    /// <param name="withSubtype">
    /// False leaves the subtype out. The comparison table's key needs that: the 2024 data stores no subtype for any
    /// monster (SRD 5.2.1 still prints "Huge Fiend (Demon)"), so a 2014 subtype compared against its absence would call
    /// every demon, devil and shapechanger changed.
    /// </param>
    internal static string? SizeAndType(JsonElement root, bool withSubtype = true)
    {
        var text = string.Join(" ", new[] { root.Str("size"), root.Str("type") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (withSubtype && root.Str("subtype") is { Length: > 0 } subtype)
        {
            text = $"{text} ({subtype})".TrimStart();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Every armor class entry, the way the 2014 SRD reads them: the first with its source in parentheses
    /// ("15 (Leather Armor, Shield)", "19 (natural armor)"), each alternative after it as "15 with Mage Armor" or
    /// "11 while Prone". Five 2014 monsters have a real alternative (a spell, a condition), and joining the two with a
    /// bare comma would read as two unrelated numbers. 2024 entries carry only a value (and sometimes the armor).
    /// </summary>
    /// <param name="withRefs">
    /// Show each linked armor, spell or condition with its ref. The comparison table passes false: it is a summary,
    /// and the bodies under it carry the refs.
    /// </param>
    internal static string? ArmorClass(JsonElement root, bool withRefs)
    {
        var parts = ArmorClassParts(root, withRefs);
        return parts.Count == 0 ? null : string.Join(", ", parts.Select((p, i) => p.Text(first: i == 0)));
    }

    /// <summary>
    /// The armor class values in the order <see cref="ArmorClass"/> shows them, so the comparison table compares the
    /// numbers a reader sees: the azer's one AC of 17, not the 15 and 17 upstream split it into.
    /// </summary>
    internal static IReadOnlyList<long> ArmorClassValues(JsonElement root) =>
        ArmorClassParts(root, withRefs: false).Select(p => p.Value).ToList();

    /// <summary>
    /// The entries as shown, with one repair. SRD 5.1 prints the azer's AC as "17 (natural armor, shield)" and the
    /// lizardfolk's as "15 (natural armor, shield)"; upstream splits each into a natural entry and a second entry that
    /// holds only a Shield. Shown as stored, "15 (natural armor), 17 with Shield" reads as an AC of 15 with an optional
    /// shield, when the shield is standard kit. So a shield-only entry right after base armor (natural, worn or plain)
    /// is folded into it; after a spell or a condition it stays an alternative.
    /// </summary>
    private static List<ArmorClassPart> ArmorClassParts(JsonElement root, bool withRefs)
    {
        var parts = new List<ArmorClassPart>();
        foreach (var entry in root.Arr("armor_class"))
        {
            if (entry.Int("value") is not { } value)
            {
                continue;
            }

            var armor = entry.Arr("armor");
            if (parts.Count > 0 && parts[^1].IsBaseArmor && armor.Count > 0 && armor.All(a => a.Str("index") == "shield"))
            {
                var shields = string.Join(", ", armor.Select(a => Reference(a, withRefs)));
                var previous = parts[^1];
                parts[^1] = previous with
                {
                    Value = value,
                    Source = previous.Source is null ? shields : $"{previous.Source}, {shields}",
                    Kind = previous.Kind == ArmorClassKind.Natural ? ArmorClassKind.Natural : ArmorClassKind.Worn,
                };
                continue;
            }

            parts.Add(entry.Str("type") == "natural" ? new ArmorClassPart(value, "natural armor", ArmorClassKind.Natural)
                : entry.Obj("condition") is { } condition ? new ArmorClassPart(value, Reference(condition, withRefs), ArmorClassKind.Condition)
                : armor.Count > 0 ? new ArmorClassPart(value, string.Join(", ", armor.Select(a => Reference(a, withRefs))), ArmorClassKind.Worn)
                : entry.Obj("spell") is { } spell ? new ArmorClassPart(value, Reference(spell, withRefs), ArmorClassKind.Spell)
                : entry.Str("desc") is { Length: > 0 } desc ? new ArmorClassPart(value, desc, ArmorClassKind.Worn)
                : new ArmorClassPart(value, null, ArmorClassKind.Plain));
        }

        return parts;
    }

    private enum ArmorClassKind
    {
        Plain,
        Natural,
        Worn,
        Spell,
        Condition,
    }

    /// <summary>One armor class as shown: its value and what supplies it (null for plain Dex-based AC).</summary>
    private sealed record ArmorClassPart(long Value, string? Source, ArmorClassKind Kind)
    {
        /// <summary>Armor the monster always has on, which a shield adds to rather than replaces.</summary>
        public bool IsBaseArmor => Kind is ArmorClassKind.Plain or ArmorClassKind.Natural or ArmorClassKind.Worn;

        public string Text(bool first)
        {
            var number = Value.ToString(CultureInfo.InvariantCulture);
            return (Kind, Source) switch
            {
                (_, null) => number,
                (ArmorClassKind.Natural, _) => $"{number} ({Source})",
                (ArmorClassKind.Condition, _) => first ? $"{number} (while {Source})" : $"{number} while {Source}",
                _ => first ? $"{number} ({Source})" : $"{number} with {Source}",
            };
        }
    }

    private static string Reference(JsonElement apiReference, bool withRef) =>
        withRef ? SrdMarkdownText.Link(apiReference) : apiReference.Str("name") ?? apiReference.Str("index") ?? "?";

    /// <summary>
    /// "256 (19d12 + 133)". 2014 writes the roll as "19d12+133" and 2024 as "19d12 + 133"; both come out spaced the
    /// 2024 way. The minus stays an ASCII hyphen ("1d4 - 1", as 2024 writes it) because the model may paste the roll
    /// into <c>dice_roll</c>, which does not read U+2212.
    /// </summary>
    internal static string? HitPoints(JsonElement root)
    {
        var points = root.Int("hit_points");
        var roll = root.Str("hit_points_roll") is { Length: > 0 } text
            ? SpacedOperators().Replace(text, " $1 ").Trim()
            : null;

        return (points, roll) switch
        {
            (null, null) => null,
            (null, _) => roll,
            (_, null) => points.Value.ToString(CultureInfo.InvariantCulture),
            _ => $"{points.Value.ToString(CultureInfo.InvariantCulture)} ({roll})",
        };
    }

    /// <summary>
    /// "40 ft., climb 40 ft., fly 80 ft. (hover)": walking speed first and unlabelled, the rest in the data's order
    /// (alphabetical in every record). <c>hover</c> is a boolean flag in the same object, not a speed; printed as a
    /// speed it would read "hover True".
    /// </summary>
    internal static string? Speed(JsonElement root)
    {
        if (root.Obj("speed") is not { } speed)
        {
            return null;
        }

        var hover = speed.Bool("hover") == true;
        var parts = new List<string>();
        if (speed.Str("walk") is { Length: > 0 } walk)
        {
            parts.Add(walk);
        }

        foreach (var property in speed.EnumerateObject())
        {
            if (property.Name == "walk" || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var part = $"{property.Name} {property.Value.GetString()}";
            parts.Add(hover && property.Name == "fly" ? part + " (hover)" : part);
        }

        if (hover && speed.Str("fly") is null && parts.Count > 0)
        {
            parts[^1] += " (hover)";
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>One score with its modifier, "27 (+8)"; null when the record has no such score.</summary>
    internal static string? AbilityScore(JsonElement root, string property) =>
        root.Int(property) is { } score
            ? $"{score.ToString(CultureInfo.InvariantCulture)} ({SrdMarkdownText.AbilityModifier(score)})"
            : null;

    private static string? AbilityTable(JsonElement root)
    {
        var scores = Abilities.Select(a => AbilityScore(root, a.Property)).ToList();
        return scores.All(s => s is null)
            ? null
            : SrdMarkdownText.Table(Abilities.Select(a => a.Label).ToList(), [scores.Select(s => s ?? string.Empty).ToList()]);
    }

    /// <summary>
    /// Saving throws or skills, "Dex +6, Con +13", from the <c>proficiencies</c> entries whose index has the prefix.
    /// <c>value</c> is already the total bonus, not the proficiency bonus.
    /// </summary>
    private static string? Proficiencies(JsonElement root, string indexPrefix, Func<JsonElement, string, string> label)
    {
        var parts = new List<string>();
        foreach (var entry in root.Arr("proficiencies"))
        {
            if (entry.Obj("proficiency") is not { } proficiency ||
                proficiency.Str("index") is not { } index ||
                !index.StartsWith(indexPrefix, StringComparison.Ordinal) ||
                entry.Int("value") is not { } value)
            {
                continue;
            }

            parts.Add($"{label(proficiency, index[indexPrefix.Length..])} {SrdMarkdownText.Signed(value)}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // "saving-throw-dex" → "Dex", as the SRD prints saving throws.
    private static string SavingThrowLabel(JsonElement proficiency, string suffix) =>
        AbilityShortNames.TryGetValue(suffix, out var shortName) ? shortName : NameAfterColon(proficiency) ?? suffix;

    // "Skill: Perception" → "Perception".
    private static string SkillLabel(JsonElement proficiency, string suffix) => NameAfterColon(proficiency) ?? suffix;

    private static string? NameAfterColon(JsonElement proficiency)
    {
        var name = proficiency.Str("name");
        var colon = name?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        return colon >= 0 ? name![(colon + 2)..] : name;
    }

    /// <summary>
    /// A damage vulnerability/resistance/immunity list, as the SRD punctuates it: "cold, lightning; bludgeoning,
    /// piercing, and slashing from nonmagical weapons". A qualified entry has commas of its own, so it is set off with
    /// a semicolon; joined with commas like the rest, "cold, bludgeoning, piercing, and slashing from nonmagical
    /// weapons" would make cold look nonmagical-only too.
    /// </summary>
    private static string? DamageList(JsonElement root, string property)
    {
        var items = root.Arr(property)
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.Str("name"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var text = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            var qualified = items[i].Contains(',') || items[i - 1].Contains(',');
            text += (qualified ? "; " : ", ") + items[i];
        }

        return text;
    }

    /// <summary>
    /// Linked conditions, with 2024's qualifier notes: "Charmed (with Mind Blank) (<c>2024/condition/charmed</c>)".
    /// </summary>
    private static string? ConditionImmunities(JsonElement root)
    {
        var items = root.Arr("condition_immunities").Select(c => LinkWithNote(c, [c.Str("note")])).ToList();
        return items.Count == 0 ? null : string.Join(", ", items);
    }

    /// <summary>
    /// "Name (note; note) (<c>ref</c>)": a linked record with the SRD's own qualifiers kept next to the name they
    /// qualify, ahead of the ref, so "Command (level 2 version)" still reads as the SRD prints it. Notes are trimmed and
    /// blank ones left out (upstream pads some), so no "Name ()" or "( note )" reaches the page.
    /// </summary>
    private static string LinkWithNote(JsonElement apiReference, IEnumerable<string?> notes) =>
        SrdMarkdownText.LinkWithNote(
            apiReference, string.Join("; ", notes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim())));

    /// <summary>
    /// "blindsight 60 ft., darkvision 120 ft., passive Perception 23": senses alphabetically, as both SRDs print
    /// them (the 2024 data stores darkvision first), and passive Perception last.
    /// </summary>
    private static string? Senses(JsonElement root)
    {
        if (root.Obj("senses") is not { } senses)
        {
            return null;
        }

        var parts = new List<string>();
        var others = senses.EnumerateObject()
            .Where(p => p.Name != "passive_perception")
            .OrderBy(p => p.Name, StringComparer.Ordinal);
        foreach (var property in others)
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{property.Name.Replace('_', ' ')} {value}");
            }
        }

        if (senses.Int("passive_perception") is { } passive)
        {
            parts.Add($"passive Perception {passive.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // 134 2014 monsters store "" where the SRD prints "—"; a missing line would read as "unknown", not "none".
    private static string Languages(JsonElement root) =>
        root.Str("languages") is { } languages && !string.IsNullOrWhiteSpace(languages) ? languages.Trim() : "—";

    /// <summary>
    /// "17 (18,000 XP; 20,000 XP in lair)": the challenge rating as the SRD writes it (1/4, not 0.25) with its XP.
    /// </summary>
    internal static string? Challenge(JsonElement root)
    {
        if (root.Num("challenge_rating") is not { } rating)
        {
            return null;
        }

        var xp = new List<string>();
        if (root.Int("xp") is { } points)
        {
            xp.Add($"{points.ToString("N0", CultureInfo.InvariantCulture)} XP");
        }

        if (root.Int("xp_in_lair") is { } lair)
        {
            xp.Add($"{lair.ToString("N0", CultureInfo.InvariantCulture)} XP in lair");
        }

        var cr = SrdJsonAccess.FormatNumber(rating);
        return xp.Count == 0 ? cr : $"{cr} ({string.Join("; ", xp)})";
    }

    private static string? ChallengeLine(JsonElement root)
    {
        var parts = new List<string?>
        {
            SrdMarkdownText.Field("Challenge", Challenge(root)),
            root.Int("proficiency_bonus") is { } bonus
                ? SrdMarkdownText.Field("Proficiency Bonus", SrdMarkdownText.Signed(bonus))
                : null,
        };
        var shown = parts.Where(p => p is not null).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }

    /// <summary>
    /// The 2024 <c>gear</c> line with each item linked to its equipment record where one exists.
    ///
    /// <para>
    /// Upstream stores gear as one string of names ("Leather Armor, Scimitar, Shield, Shortbow"), not references, so
    /// the refs are found by name: the SRD's own slug rule, then the singular for counted plurals ("Javelins (6)" →
    /// <c>javelin</c>). A name that matches no record (Holy Symbol) is shown plain rather than with a guessed ref.
    /// </para>
    /// </summary>
    private static string? Gear(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        if (root.Arr("gear") is { Count: > 0 } references)
        {
            return SrdMarkdownText.LinkList(references);
        }

        if (root.Str("gear") is not { Length: > 0 } gear)
        {
            return null;
        }

        var items = gear.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => GearItemRef(doc.Edition, item, lookup) is { } reference ? $"{item} (`{reference}`)" : item);
        return string.Join(", ", items);
    }

    private static SrdRef? GearItemRef(string edition, string item, ISrdLookup lookup)
    {
        var name = GearCount().Replace(item, string.Empty).Trim();
        var candidates = name.EndsWith('s') ? new[] { name, name[..^1] } : [name];
        foreach (var candidate in candidates)
        {
            var slug = SrdSlug.FromName(candidate);
            foreach (var kind in new[] { SrdKinds.Equipment, SrdKinds.MagicItem })
            {
                if (lookup.Get(edition, kind, slug) is { } found)
                {
                    return found.Ref;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every entry of one section, as "***Name (usage).*** desc", blank-line separated; null when there are none.
    /// </summary>
    private static string? Entries(SrdDocument doc, string property, ISrdLookup lookup)
    {
        var entries = doc.Root.Arr(property)
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => Entry(doc, e, lookup))
            .ToList();
        return entries.Count == 0 ? null : string.Join("\n\n", entries);
    }

    private static string Entry(SrdDocument doc, JsonElement entry, ISrdLookup lookup)
    {
        var name = EntryName(entry);
        var heading = name.EndsWith('.') ? name : name + ".";
        var text = $"***{heading}*** {SrdProse.Join(entry.Paragraphs("desc"))}".TrimEnd();
        return entry.Obj("spellcasting") is { } spellcasting &&
               SpellList(doc, entry, spellcasting, lookup) is { } spells
            ? $"{text}\n\n{spells}"
            : text;
    }

    /// <summary>
    /// The entry's name with its usage put back: "Fire Breath (Recharge 5–6)", "Legendary Resistance (3/Day, or 4/Day
    /// in Lair)". When the stored name already ends in a qualifier, the usage goes first inside the same parentheses,
    /// as the SRD prints it: "Nightmare Haunting (1/Day; Requires Soul Bag)". 2014 legendary costs ("(Costs 2
    /// Actions)") are part of the stored name and stay.
    /// </summary>
    internal static string EntryName(JsonElement entry)
    {
        var name = entry.Str("name")?.Trim() ?? "?";
        if (Usage(entry.Obj("usage")) is not { } usage)
        {
            return name;
        }

        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return name.EndsWith(')') && open > 0
            ? $"{name[..open]} ({usage}; {name[(open + 2)..]}"
            : $"{name} ({usage})";
    }

    /// <summary>
    /// The SRD's wording for a <c>usage</c> object; null for at-will and for shapes the data does not use, so an
    /// unknown usage drops out of the name rather than printing something wrong. The raw record is in format full.
    /// </summary>
    internal static string? Usage(JsonElement? usage)
    {
        if (usage is not { } u)
        {
            return null;
        }

        switch (u.Str("type"))
        {
            case "recharge on roll":
                if (u.Int("min_value") is not { } min)
                {
                    return null;
                }

                var sides = u.Str("dice") is { } dice && dice.IndexOf('d') is var d and >= 0 &&
                            long.TryParse(dice[(d + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : 6;
                return min >= sides
                    ? string.Create(CultureInfo.InvariantCulture, $"Recharge {min}")
                    : string.Create(CultureInfo.InvariantCulture, $"Recharge {min}–{sides}");

            case "per day":
                if (u.Int("times") is not { } times)
                {
                    return null;
                }

                return u.Int("times_in_lair") is { } lair
                    ? string.Create(CultureInfo.InvariantCulture, $"{times}/Day, or {lair}/Day in Lair")
                    : string.Create(CultureInfo.InvariantCulture, $"{times}/Day");

            case "recharge after rest":
                var rests = u.Arr("rest_types")
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.GetString()!))
                    .ToList();
                if (rests.Count == 0)
                {
                    return null;
                }

                // Both SRDs: 5.1 "Recharges after a Short or Long Rest", and 5.2.1 the same ("Fell Glare (Fiend Only;
                // Recharges after a Long Rest)" in Find Steed's Otherworldly Steed, the only 5.2.1 text using it).
                return $"Recharges after a {string.Join(" or ", rests)} Rest";

            default:
                return null;
        }
    }

    /// <summary>
    /// The spells a spellcasting entry can cast, each with its ref.
    ///
    /// <para>
    /// When the desc introduces a list it does not contain (every 2024 "…the following spells:" block), the list is
    /// rendered as the SRD prints it, grouped by use: "- **At Will:** …", "- **1/Day Each:** …". Otherwise the desc
    /// already names every spell (2014 lists them in the prose; 2024 single-spell entries such as Misty Step name
    /// theirs), and one "Spells:" line adds the refs.
    /// </para>
    /// <para>
    /// In 2024 a spell's <c>level</c> here is the level it is cast at, and the SRD calls an upcast one "Fireball
    /// (level 5 version)". That note is shown when the level is above the spell's own level (looked up in the spell
    /// record). Not for 2014, whose levels are the spells' own, and never for a cantrip: the 2014 drider stores
    /// Dancing Lights at level 1.
    /// </para>
    /// <para>
    /// A spell's own <c>notes</c> go in the same parentheses, as stored: "Disguise Self (24-hour duration)",
    /// "Shapechange (Beast or Humanoid form only, … no Concentration …)", "Mage Armor (Included in AC)". They are this
    /// monster's rules for the spell, and the linked spell record says otherwise (1 hour, Concentration, +3 AC on top).
    /// In a "…the following spells:" list they exist nowhere else; a model that follows the ref without them relays the
    /// base spell as the monster's. Elsewhere the desc usually says the same, and the note beside the ref still helps.
    /// </para>
    /// </summary>
    private static string? SpellList(SrdDocument doc, JsonElement entry, JsonElement spellcasting, ISrdLookup lookup)
    {
        var spells = spellcasting.Arr("spells").Where(s => s.ValueKind == JsonValueKind.Object).ToList();
        if (spells.Count == 0)
        {
            return null;
        }

        if (entry.Str("desc")?.TrimEnd().EndsWith(':') != true)
        {
            return "Spells: " + string.Join(", ", spells.Select(s => SpellLink(doc.Edition, s, lookup)));
        }

        var groups = spells
            .GroupBy(s => SpellUsageLabel(s.Obj("usage")))
            .Select(g => $"- **{g.Key}:** {string.Join(", ", g.Select(s => SpellLink(doc.Edition, s, lookup)))}");
        return string.Join("\n", groups);
    }

    private static string SpellUsageLabel(JsonElement? usage) => usage switch
    {
        { } u when u.Str("type") == "at will" => "At Will",
        { } u when u.Str("type") == "per day" && u.Int("times") is { } times =>
            string.Create(CultureInfo.InvariantCulture, $"{times}/Day Each"),
        _ => "Spells",
    };

    private static string SpellLink(string edition, JsonElement spell, ISrdLookup lookup)
    {
        string? note = null;
        if (edition == SrdEdition.Edition2024 &&
            spell.Int("level") is { } castAt &&
            SrdRef.FromApiUrl(spell.Str("url")) is { } reference &&
            lookup.Get(reference.Edition, SrdKinds.Spell, reference.Slug)?.Root.Int("level") is { } own &&
            own > 0 && castAt > own)
        {
            note = string.Create(CultureInfo.InvariantCulture, $"level {castAt} version");
        }

        return LinkWithNote(spell, [note, spell.Str("notes")]);
    }

    [GeneratedRegex(@"\s*([+-])\s*")]
    private static partial Regex SpacedOperators();

    // A counted gear entry's " (6)".
    [GeneratedRegex(@"\s*\(\d+\)\s*$")]
    private static partial Regex GearCount();
}
