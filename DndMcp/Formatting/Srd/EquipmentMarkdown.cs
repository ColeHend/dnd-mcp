using System.Globalization;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Body formatter for equipment, equipment categories, magic items, weapon properties, weapon masteries and poisons.
///
/// <para>
/// One equipment formatter serves weapons, armor, gear, tools, packs, mounts and vehicles in both editions, because
/// upstream does not type them: an item is whatever fields it happens to carry (<c>damage</c> makes it a weapon,
/// <c>armor_class</c> armor, <c>contents</c> a pack). Every line is therefore conditional on its field, and a field the
/// record lacks, or holds in an unexpected shape, drops its line instead of failing the whole <c>rules_get</c> call.
/// </para>
/// <para>
/// Structured fields come first, as <c>**Label** value</c> lines in the order a player asks about them (what it does,
/// then what it costs), then upstream's prose unchanged apart from paragraph breaks (<see cref="SrdProse"/>).
/// </para>
/// </summary>
internal static class EquipmentMarkdown
{
    public static string Body(SrdDocument doc, ISrdLookup lookup)
    {
        var body = doc.Kind switch
        {
            SrdKinds.Equipment => Equipment(doc),
            SrdKinds.EquipmentCategory => Category(doc, lookup),
            SrdKinds.MagicItem => MagicItem(doc, lookup),
            SrdKinds.Poison => Poison(doc),
            _ => SrdProse.Join(doc.Root.Description()),
        };

        return string.IsNullOrWhiteSpace(body) ? SrdMarkdownText.NoDescription : body;
    }

    private static string Equipment(SrdDocument doc)
    {
        var root = doc.Root;
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Category", Categories(root)),

            SrdMarkdownText.Field("Damage", WeaponDamage(root, doc.Edition)),
            SrdMarkdownText.Field("Properties", SrdMarkdownText.LinkList(root.Arr("properties"))),
            SrdMarkdownText.Field("Mastery", LinkOrNull(root.Obj("mastery"))),
            SrdMarkdownText.Field("Range", Range(root)),
            SrdMarkdownText.Field("Ammunition", LinkOrNull(root.Obj("ammunition"))),
            SrdMarkdownText.Field("Notes", string.Join("; ", root.Paragraphs("notes"))),

            SrdMarkdownText.Field("Armor Class", ArmorClass(root)),
            SrdMarkdownText.Field("Minimum Strength", Positive(root.Int("str_minimum"))),
            root.Bool("stealth_disadvantage") == true ? "**Stealth** Disadvantage" : null,
            DonAndDoff(root),

            SrdMarkdownText.Field("Ability", LinkOrNull(root.Obj("ability"))),
            SrdMarkdownText.Field("Utilize", Utilize(root)),
            SrdMarkdownText.Field("Craft", SrdMarkdownText.LinkList(root.Arr("craft"))),

            SrdMarkdownText.Field("Speed", Speed(root)),
            SrdMarkdownText.Field("Carrying Capacity", root.Str("capacity")),

            SrdMarkdownText.Field("Contents", Contents(root)),

            // Ammunition is sold in bundles ("Arrows (20)"); the cost and weight below are for the whole bundle.
            SrdMarkdownText.Field("Quantity", root.Int("quantity") is > 1 and var quantity ? Amount(quantity) : null),
            SrdMarkdownText.Field("Storage", LinkOrNull(root.Obj("storage"))),

            SrdMarkdownText.Field("Cost", Cost(root)),
            SrdMarkdownText.Field("Weight", Weight(root)),
        ]);

        return SrdMarkdownText.Blocks([fields, SrdProse.Join(root.Description()), Special(root)]);
    }

    /// <summary>
    /// Every category the item is in, as refs, so "which other martial melee weapons are there" is one call away.
    /// Both editions carry <c>equipment_categories</c>; the single 2014 <c>equipment_category</c> is only a fallback.
    /// </summary>
    private static string? Categories(JsonElement root)
    {
        var categories = root.Arr("equipment_categories");
        if (categories.Count > 0)
        {
            return SrdMarkdownText.LinkList(categories);
        }

        return LinkOrNull(root.Obj("equipment_category"));
    }

    /// <summary>
    /// "1d8 slashing (<c>ref</c>), or 1d10 two-handed". The type is lower-case in 2014 and capitalised in 2024 because
    /// that is how each SRD prints it. Upstream's dice text is kept as it is, including the blowgun's flat "1".
    /// </summary>
    private static string? WeaponDamage(JsonElement root, string edition)
    {
        if (root.Obj("damage") is not { } damage || DamageText(damage, edition) is not { } oneHanded)
        {
            return null;
        }

        if (root.Obj("two_handed_damage") is not { } twoHanded || twoHanded.Str("damage_dice") is not { } dice)
        {
            return oneHanded;
        }

        var sameType = twoHanded.Obj("damage_type")?.Str("index") == damage.Obj("damage_type")?.Str("index");
        return $"{oneHanded}, or {(sameType ? dice : DamageText(twoHanded, edition))} two-handed";
    }

    private static string? DamageText(JsonElement damage, string edition)
    {
        if (damage.Str("damage_dice") is not { } dice)
        {
            return null;
        }

        if (damage.Obj("damage_type") is not { } type || (type.Str("name") ?? type.Str("index")) is not { } name)
        {
            return dice;
        }

        if (edition == SrdEdition.Edition2014)
        {
            name = name.ToLowerInvariant();
        }

        return SrdRef.FromApiUrl(type.Str("url")) is { } reference ? $"{dice} {name} (`{reference}`)" : $"{dice} {name}";
    }

    /// <summary>
    /// "150/600 ft.", or "20/60 ft. (thrown)" for a thrown weapon. Upstream gives every melee weapon a range of 5 ft.,
    /// reach weapons included, so a range without a long distance is never shown: "Range 5 ft." on a glaive would
    /// contradict its Reach property.
    /// </summary>
    private static string? Range(JsonElement root)
    {
        var normal = Distance(root.Obj("range"), requireLong: true);
        var thrown = Distance(root.Obj("throw_range"), requireLong: false);

        if (thrown is null)
        {
            return normal;
        }

        return normal is null || normal == thrown ? $"{thrown} (thrown)" : $"{normal}; {thrown} thrown";
    }

    private static string? Distance(JsonElement? range, bool requireLong)
    {
        if (range is not { } value || value.Num("normal") is not { } normal)
        {
            return null;
        }

        if (value.Num("long") is { } far)
        {
            return $"{Amount(normal)}/{Amount(far)} ft.";
        }

        return requireLong ? null : $"{Amount(normal)} ft.";
    }

    /// <summary>
    /// The SRD's own notation: "18", "11 + Dex modifier", "12 + Dex modifier (max 2)", and "+2" for a shield, whose
    /// value is a bonus to the wearer's AC rather than an AC. 2024 heavy armor carries <c>max_bonus: 0</c> with
    /// <c>dex_bonus: false</c>; the cap means nothing when Dex is not added at all, so it is not shown.
    /// </summary>
    private static string? ArmorClass(JsonElement root)
    {
        if (root.Obj("armor_class") is not { } armorClass || armorClass.Int("base") is not { } baseValue)
        {
            return null;
        }

        if (IsShield(root))
        {
            return SrdMarkdownText.Signed(baseValue);
        }

        var value = baseValue.ToString(CultureInfo.InvariantCulture);
        if (armorClass.Bool("dex_bonus") != true)
        {
            return value;
        }

        return armorClass.Int("max_bonus") is > 0 and var cap
            ? $"{value} + Dex modifier (max {cap.ToString(CultureInfo.InvariantCulture)})"
            : $"{value} + Dex modifier";
    }

    private static bool IsShield(JsonElement root) =>
        root.Str("armor_category") == "Shield" ||
        root.Arr("equipment_categories").Any(c => c.Str("index") == "shields");

    /// <summary>"**Don** 10 minutes · **Doff** 5 minutes" (2024 armor), or whichever half the record has.</summary>
    private static string? DonAndDoff(JsonElement root)
    {
        var parts = new[] { SrdMarkdownText.Field("Don", root.Str("don_time")), SrdMarkdownText.Field("Doff", root.Str("doff_time")) }
            .Where(p => p is not null)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>2024 tools: "Identify a substance (DC 15); Start a fire (DC 15)".</summary>
    private static string Utilize(JsonElement root) =>
        string.Join("; ", root.Arr("utilize")
            .Where(u => u.Str("name") is not null)
            .Select(u => u.Obj("dc")?.Int("dc_value") is { } dc
                ? $"{u.Str("name")} (DC {dc.ToString(CultureInfo.InvariantCulture)})"
                : u.Str("name")!));

    /// <summary>
    /// Mount speed. Upstream's "ft/round" is the SRD's plain "60 ft." (a creature's speed); vehicles use "mph".
    /// </summary>
    private static string? Speed(JsonElement root)
    {
        if (root.Obj("speed") is not { } speed || speed.Num("quantity") is not { } quantity)
        {
            return null;
        }

        return speed.Str("unit") switch
        {
            "ft/round" => $"{Amount(quantity)} ft.",
            { Length: > 0 } unit => $"{Amount(quantity)} {unit}",
            _ => Amount(quantity),
        };
    }

    /// <summary>A pack's contents: "Backpack (<c>ref</c>), …, 10 × Torch (<c>ref</c>)".</summary>
    private static string Contents(JsonElement root) =>
        string.Join(", ", root.Arr("contents")
            .Where(c => c.Obj("item") is not null)
            .Select(c => c.Int("quantity") is > 1 and var quantity
                ? $"{Amount(quantity)} × {SrdMarkdownText.Link(c.Obj("item")!.Value)}"
                : SrdMarkdownText.Link(c.Obj("item")!.Value)));

    /// <summary>
    /// "1,500 gp". A zero cost is not shown: the priest's pack's alms box, censer and vestments have no price of their
    /// own, and "0 gp" would tell a player they are free to buy.
    /// </summary>
    private static string? Cost(JsonElement root)
    {
        if (root.Obj("cost") is not { } cost || cost.Num("quantity") is not { } quantity || quantity == 0)
        {
            return null;
        }

        return cost.Str("unit") is { Length: > 0 } unit ? $"{Amount(quantity)} {unit}" : Amount(quantity);
    }

    /// <summary>"3 lb.", "1/4 lb."; zero weight (the SRD's "—") is not shown.</summary>
    private static string? Weight(JsonElement root) =>
        root.Num("weight") is { } weight && weight != 0 ? $"{Amount(weight)} lb." : null;

    /// <summary>The 2014 lance's and net's "special" rules, labelled as the SRD labels them.</summary>
    private static string? Special(JsonElement root)
    {
        var special = root.Paragraphs("special").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (special.Count == 0)
        {
            return null;
        }

        special[0] = "***Special.*** " + special[0].Trim();
        return SrdProse.Join(special);
    }

    /// <summary>
    /// Every item in the category as a ref: up to 177 of them (2014 wondrous items), as one compact line. Each item is
    /// listed and counted once: upstream lists 25 of the 2014 mounts-and-vehicles entries twice (every barding, the
    /// saddles, cart, wagon…) and four standard-gear entries twice, and the count is a fact the body states ("Items (65)"
    /// for 40 mounts, vehicles and tack would be relayed as the SRD's number).
    /// </summary>
    private static string? Category(SrdDocument doc, ISrdLookup lookup)
    {
        var items = doc.Root.Arr("equipment")
            .DistinctBy(item => item.Str("url") ?? item.Str("index") ?? item.GetRawText(), StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var list = $"**Items ({items.Count.ToString(CultureInfo.InvariantCulture)})** {SrdMarkdownText.LinkList(items)}";
        return IsWondrousItems2024(doc.Root.Str("index"), doc.Edition) && OtherTypesFiledAsWondrous(items, lookup) is { } note
            ? $"{list}\n\n{note}"
            : list;
    }

    /// <summary>
    /// For the 2024 Wondrous Items category, the items whose own type line says they are something else: upstream has no
    /// Rods or Scrolls category, so the six rods and the Spell Scroll are filed here, while SRD 5.2.1 lists Rods and
    /// Scrolls as categories of their own (and "a rod can be used as an Arcane Focus" hangs on it). Said beside the count,
    /// so the count is not read as the SRD's number of wondrous items.
    /// </summary>
    private static string? OtherTypesFiledAsWondrous(IReadOnlyList<JsonElement> items, ISrdLookup lookup)
    {
        var others = items
            .Select(item => SrdChoiceMarkdown.Resolve(item.Str("url"), lookup))
            .OfType<SrdDocument>()
            .Select(record => (Record: record, Type: OwnType2024(record.Root)))
            .Where(r => r.Type is not null && r.Type != "Wondrous Item")
            .GroupBy(r => r.Type!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {g.Key}{(g.Count() == 1 ? string.Empty : "s")} " +
                         $"({string.Join(", ", g.Select(r => r.Record.Name))})")
            .ToList();
        return others.Count == 0
            ? null
            : $"*Of these, {string.Join(" and ", others)} are filed here by the data; their own type lines, and SRD 5.2.1, " +
              "put them in categories of their own.*";
    }

    // "Rod" for "Rod, Legendary (Requires Attunement)", "Wondrous Item" for "Wondrous Item, Rare": a 2024 magic item's
    // type, the first line of its text up to the first comma or parenthesis.
    private static string? OwnType2024(JsonElement root)
    {
        var all = string.Join("\n", root.Description()).TrimStart();
        var newline = all.IndexOf('\n');
        if (newline < 0)
        {
            return null;
        }

        var line = WithoutEmphasis(all[..newline]);
        var end = line.IndexOfAny([',', '(']);
        return (end < 0 ? line : line[..end]).Trim();
    }

    private static bool IsWondrousItems2024(string? categoryIndex, string edition) =>
        edition == SrdEdition.Edition2024 && categoryIndex == "wondrous-items";

    /// <summary>
    /// A magic item: the SRD's italic type line, the links that connect it to its variants and category, then its
    /// text.
    ///
    /// <para>
    /// 2014 keeps the type line as <c>desc[0]</c> ("Weapon (any sword), rare (requires attunement)"), complete, so it
    /// is shown as it is. 2024 keeps only the type ("Weapon (Any Melee Weapon)") as the first line of its <c>desc</c>
    /// string and moves rarity and attunement into fields; the line is rebuilt the way SRD 5.2.1 prints it
    /// ("Weapon (Any Melee Weapon), Rare (Requires Attunement)"), because whether an item needs attunement, and by whom,
    /// is the first thing a player asks.
    /// </para>
    /// <para>
    /// A curated correction (<c>content/srd-corrections.json</c>) replaces a corrupted 2024 <c>desc</c> with the SRD 5.2
    /// markdown's own text, which may start with the complete italic line ("*Wondrous Item, Rarity Varies (Requires
    /// Attunement)*") and separate paragraphs with blank lines. Either shape reads the same: emphasis around the line is
    /// dropped (the subtitle adds its own), and a line that already states rarity or attunement is not rebuilt, so it is
    /// never printed as "Rare (Requires Attunement), Rare (Requires Attunement)".
    /// </para>
    /// </summary>
    private static string MagicItem(SrdDocument doc, ISrdLookup lookup)
    {
        var root = doc.Root;
        string? typeLine;
        IReadOnlyList<string> text;

        if (root.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Array)
        {
            var paragraphs = root.Paragraphs("desc");
            typeLine = paragraphs.Count > 0 ? WithoutEmphasis(paragraphs[0]) : null;
            text = paragraphs.Skip(1).ToList();
        }
        else
        {
            var all = string.Join("\n", root.Description()).TrimStart();
            var newline = all.IndexOf('\n');
            typeLine = TypeLine2024(root, newline < 0 ? null : WithoutEmphasis(all[..newline]));
            text = [newline < 0 ? all : all[(newline + 1)..]];
        }

        // A 2024 rod or scroll filed under Wondrous Items (upstream has no Rods or Scrolls category) shows no category line
        // beside its "Rod, …" type line: "Category: Wondrous Items" contradicts it, and in a comparison with 2014's "Rod"
        // reads as a rules change.
        var category = root.Obj("equipment_category");
        var categoryContradicted = doc.Edition == SrdEdition.Edition2024 && IsWondrousItems2024(category?.Str("index"), doc.Edition) &&
                                   OwnType2024(root) is { } ownType && ownType != "Wondrous Item";
        var parent = VariantParent(doc, lookup);
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Category", categoryContradicted ? null : LinkOrNull(category)),
            SrdMarkdownText.Field("Variant of", parent is null ? null : $"{parent.Name} (`{parent.Ref}`)"),
            SrdMarkdownText.Field("Variants", SrdMarkdownText.LinkList(root.Arr("variants"))),
            root.Bool("attunement") == true ? null : SrdMarkdownText.Field("Limited to", root.Str("limited-to")),
        ]);

        return SrdMarkdownText.Blocks(
        [
            string.IsNullOrWhiteSpace(typeLine) ? null : $"*{typeLine}*",
            fields,
            SrdProse.Join(text),
        ]);
    }

    /// <summary>
    /// "Wand, Rare (Requires Attunement by a Spellcaster)". Upstream's own type line is kept even where it is odd
    /// ("Weapon (Uncommon)" on +1 ammunition): it is the SRD text, and the rarity field follows it either way. A line
    /// that already names the item's rarity or its attunement is the complete SRD line (a corrected record's) and is
    /// kept as it is; no upstream type line does either.
    /// </summary>
    private static string? TypeLine2024(JsonElement root, string? type)
    {
        var rarity = root.Obj("rarity")?.Str("name");
        if (type is not null &&
            ((!string.IsNullOrWhiteSpace(rarity) && type.Contains(rarity.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             type.Contains("attunement", StringComparison.OrdinalIgnoreCase)))
        {
            return type;
        }

        var attunement = root.Bool("attunement") == true
            ? root.Str("limited-to") is { Length: > 0 } limitedTo
                ? $" (Requires Attunement by {Article(limitedTo)} {limitedTo})"
                : " (Requires Attunement)"
            : string.Empty;

        var parts = new[] { type, rarity }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts) + attunement;
    }

    private static string Article(string noun) => "AEIOUaeiou".Contains(noun[0]) ? "an" : "a";

    /// <summary>
    /// The line without the emphasis markers around it ("*Potion, Uncommon*", "_…_", "**…**"), trimmed. The subtitle is
    /// italicised by <see cref="MagicItem"/>; kept, the markers would double up ("**Potion, Uncommon**" in bold) or
    /// unbalance it. Only markers at both ends are taken off, so emphasis inside the line stays.
    /// </summary>
    private static string WithoutEmphasis(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 1 && trimmed[0] is '*' or '_' && trimmed[^1] is '*' or '_'
            ? trimmed.Trim('*', '_').Trim()
            : trimmed;
    }

    /// <summary>
    /// The item this one is a variant of (Belt of Hill Giant Strength → Belt of Giant Strength), found by trying ever
    /// shorter hyphen prefixes of its slug and accepting a candidate only when the candidate's own <c>variants</c> list
    /// names this item. Upstream links parent to variants but not back, and without this a model reading "Armor +1"
    /// cannot find the shared rules text on "Armor". The check against the list is what makes the prefix guess exact:
    /// <c>armor-of-resistance</c> is not a variant of <c>armor</c> just because its slug starts that way. Every one of
    /// the 123 (2014) and 19 (2024) listed variants is found this way; the <c>variant</c> flag is not consulted, so an
    /// item upstream forgot to flag is still linked.
    /// </summary>
    private static SrdDocument? VariantParent(SrdDocument doc, ISrdLookup lookup)
    {
        for (var cut = doc.Slug.LastIndexOf('-'); cut > 0; cut = doc.Slug.LastIndexOf('-', cut - 1))
        {
            if (lookup.Get(doc.Edition, doc.Kind, doc.Slug[..cut]) is { } candidate &&
                candidate.Root.Arr("variants").Any(v => v.Str("index") == doc.Slug))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>A 2024 poison: its delivery type and price, then the effect.</summary>
    private static string Poison(SrdDocument doc)
    {
        var root = doc.Root;
        var fields = SrdMarkdownText.Lines(
        [
            SrdMarkdownText.Field("Type", Capitalized(root.Str("type"))),
            SrdMarkdownText.Field("Price per Dose", root.Num("cost") is { } cost ? $"{Amount(cost)} gp" : null),
        ]);

        return SrdMarkdownText.Blocks([fields, SrdProse.Join(root.Description())]);
    }

    private static string? LinkOrNull(JsonElement? reference) =>
        reference is { } value ? SrdMarkdownText.Link(value) : null;

    private static string? Positive(long? value) =>
        value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;

    private static string? Capitalized(string? text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A quantity as the SRD prints it: thousands separated ("1,500"), and the fractions it uses for weights
    /// ("1/4", "1/2") rather than decimals.
    /// </summary>
    private static string Amount(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString("#,0", CultureInfo.InvariantCulture)
            : SrdJsonAccess.FormatNumber(value);
}
