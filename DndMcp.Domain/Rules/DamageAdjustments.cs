using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Domain.Rules;

/// <summary>
/// One part of one damage instance as the tracker and the sheet receive it: an amount and its damage type. Named apart
/// from <see cref="DamagePart"/> (a build's flat damage part in the DSL), which means something else.
/// </summary>
/// <param name="Amount">
/// The damage as rolled or given. A negative amount (a roll with a penalty) counts as 0: SRD 5.2 "it's possible to deal 0
/// damage but not negative damage", and a negative part must not cancel another part of the same hit.
/// </param>
/// <param name="DamageType">
/// A canonical damage type (<see cref="DslValues.DamageTypes"/>: "fire", "bludgeoning", …) or null for untyped damage,
/// which no resistance, immunity or vulnerability of a type touches (only Petrified and "all" entries halve it). The
/// caller canonicalises what the user typed; anything else here is a host bug.
/// </param>
public sealed record DamageInstancePart(int Amount, string? DamageType);

/// <summary>
/// One resistance, immunity or vulnerability the damage pipeline applies: a damage type (or <see cref="AllTypes"/>), the
/// stat block's qualifier when it has one, the types an "all" entry leaves out, and where it came from (for the
/// breakdown: "Rage", "sheet").
/// </summary>
/// <param name="DamageType">A canonical damage type, or <see cref="AllTypes"/> (an effect such as a Bear-totem Rage: "all" except psychic).</param>
/// <param name="Qualifier">
/// <see cref="StatBlockValues.DamageQualifiers"/> as the normalizer wrote it on a stat block entry
/// (<see cref="DamageAdjustment.Qualifier"/>), or null for an unqualified entry. Read exactly as the simulator reads it
/// (<c>Qualifier.Of</c>): "nonmagical" does not apply to magical damage, "nonmagical_not_silvered" not to magical or
/// silvered, "nonmagical_not_adamantine" not to magical or adamantine, and "other" (a qualifier the simulator cannot read,
/// "from magic weapons wielded by good creatures") always applies, with the breakdown naming it so the table can rule.
/// </param>
/// <param name="Except">For an <see cref="AllTypes"/> entry: the canonical types it does not cover.</param>
/// <param name="Source">What granted it, for the breakdown: an effect's name, "sheet", or null for a stat block entry.</param>
public sealed record DamageAdjustmentEntry(string DamageType, string? Qualifier = null, IReadOnlyList<string>? Except = null, string? Source = null)
{
    /// <summary>The wire word for "every damage type" in an effect's or a sheet's lists.</summary>
    public const string AllTypes = "all";

    /// <summary>
    /// Whether the entry is about damage of <paramref name="damageType"/> (null: untyped). A typed entry covers its own type
    /// only (case ignored, as the simulator's type index does), never untyped damage; an "all" entry covers every type and
    /// untyped damage, except its <see cref="Except"/> list.
    /// </summary>
    public bool Covers(string? damageType)
    {
        if (DamageType == AllTypes)
        {
            return damageType is null || Except is null || !Except.Contains(damageType, StringComparer.OrdinalIgnoreCase);
        }

        return damageType is not null && string.Equals(DamageType, damageType, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Everything that adjusts damage against one target, gathered from every place it can come from: the stat block
/// snapshot (with its qualifiers), the sheet's <c>defenses</c>, each active combat effect (<c>effect {resist, immune,
/// vulnerable, except}</c>) and the Petrified condition. The pipeline applies resistance once and vulnerability once
/// however many entries cover a type (SRD 5.1 "Multiple instances of resistance or vulnerability that affect the same
/// damage type count as only one instance"; SRD 5.2.1 "Resistance is applied only once to an instance of damage").
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entry decides a type.</b> When several entries cover a type, the most general one decides whether it applies,
/// exactly as the simulator's per-type code table does (<c>Qualifier.Table</c>: an unqualified entry beats a qualified
/// one, "nonmagical" beats the silvered and adamantine forms). A tracker that let any applicable entry decide would agree
/// with the simulator on every SRD stat block but could disagree on a hand-made one; the agreement test runs both over
/// every SRD monster.
/// </para>
/// <para>
/// Sheets and effects carry no qualifiers: a PC's or a Rage's resistance applies to magical damage too.
/// </para>
/// </remarks>
public sealed record DamageAdjustments
{
    /// <summary>No adjustments: damage is taken as given.</summary>
    public static DamageAdjustments None { get; } = new();

    /// <summary>Entries that halve damage of their type (once, however many cover it).</summary>
    public IReadOnlyList<DamageAdjustmentEntry> Resistances { get; init; } = [];

    /// <summary>Entries that reduce damage of their type to 0.</summary>
    public IReadOnlyList<DamageAdjustmentEntry> Immunities { get; init; } = [];

    /// <summary>Entries that double damage of their type (once).</summary>
    public IReadOnlyList<DamageAdjustmentEntry> Vulnerabilities { get; init; } = [];

    /// <summary>
    /// The target is Petrified: resistance to all damage, untyped included (SRD 5.1 Petrified "resistance to all damage";
    /// SRD 5.2.1 "Resistance to all damage"), counted as the same one resistance as any other.
    /// </summary>
    public bool Petrified { get; init; }

    /// <summary>A stat block's resistances, immunities and vulnerabilities with their qualifiers.</summary>
    public static DamageAdjustments FromStatBlock(StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new DamageAdjustments
        {
            Resistances = block.Resistances.Select(Entry).ToList(),
            Immunities = block.Immunities.Select(Entry).ToList(),
            Vulnerabilities = block.Vulnerabilities.Select(Entry).ToList(),
        };

        // An "other" qualifier keeps the data's own words, so the breakdown can quote what the table must rule on.
        static DamageAdjustmentEntry Entry(DamageAdjustment a) =>
            new(a.DamageType, a.Qualifier, Source: a.Qualifier == StatBlockValues.DamageQualifiers.Other ? a.Text : null);
    }

    /// <summary>
    /// Unqualified lists of canonical types or <see cref="DamageAdjustmentEntry.AllTypes"/>, as a sheet's <c>defenses</c>
    /// or an effect store them; <paramref name="except"/> narrows every "all" entry.
    /// </summary>
    /// <param name="source">"sheet", or the effect's name ("Rage"): what the breakdown says granted it.</param>
    /// <exception cref="ArgumentException">A value that is not a canonical damage type or "all" (the caller validates input).</exception>
    public static DamageAdjustments FromTypes(
        IEnumerable<string>? resist, IEnumerable<string>? immune, IEnumerable<string>? vulnerable, IEnumerable<string>? except = null, string? source = null)
    {
        var excluded = except?.Select(t => Canonical(t, allowAll: false)).ToList();
        return new DamageAdjustments
        {
            Resistances = Entries(resist),
            Immunities = Entries(immune),
            Vulnerabilities = Entries(vulnerable),
        };

        List<DamageAdjustmentEntry> Entries(IEnumerable<string>? types) =>
            (types ?? []).Select(t => Canonical(t, allowAll: true))
                .Select(t => new DamageAdjustmentEntry(t, null, t == DamageAdjustmentEntry.AllTypes ? excluded : null, source))
                .ToList();
    }

    /// <summary>
    /// A sheet's <c>defenses</c> (<c>{"resist":["fire"],"immune":["poison"],"vulnerable":[]}</c>): unqualified types (or
    /// "all"), reported as from the "sheet". Its <c>condition_immune</c> list is not about damage: see
    /// <see cref="CombatRules.IsImmuneToCondition"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A value that is not a canonical damage type or "all".</exception>
    public static DamageAdjustments FromSheetDefenses(IEnumerable<string>? resist, IEnumerable<string>? immune, IEnumerable<string>? vulnerable) =>
        FromTypes(resist, immune, vulnerable, except: null, source: SheetSource);

    /// <summary>
    /// An active combat effect's adjustments (<c>effect {resist, immune, vulnerable, except}</c>, contract §6.5): "all"
    /// entries leave out <paramref name="except"/> (FIX B12: a Bear-totem Rage resists "all" except psychic), reported
    /// under the effect's <paramref name="name"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A value that is not a canonical damage type or "all".</exception>
    public static DamageAdjustments FromEffect(
        string name, IEnumerable<string>? resist, IEnumerable<string>? immune, IEnumerable<string>? vulnerable, IEnumerable<string>? except = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return FromTypes(resist, immune, vulnerable, except, name);
    }

    /// <summary>The <see cref="DamageAdjustmentEntry.Source"/> of a sheet's defenses.</summary>
    public const string SheetSource = "sheet";

    /// <summary>The <see cref="DamageStep.Source"/> of the resistance Petrified gives (the condition's canonical name).</summary>
    public const string PetrifiedSource = StatBlockValues.Conditions.Petrified;

    /// <summary>
    /// Every set together, in order: the stat block or the sheet, then each active effect, then Petrified (any set that
    /// says so). A combatant's adjustments are <c>Combine(snapshot-or-sheet, effect1, effect2, …)</c>.
    /// </summary>
    public static DamageAdjustments Combine(params IEnumerable<DamageAdjustments?> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return sets.Aggregate(None, (all, set) => set is null ? all : all.Plus(set));
    }

    /// <summary>Both sets together (stat block or sheet, plus every active effect, plus Petrified).</summary>
    public DamageAdjustments Plus(DamageAdjustments other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new DamageAdjustments
        {
            Resistances = [.. Resistances, .. other.Resistances],
            Immunities = [.. Immunities, .. other.Immunities],
            Vulnerabilities = [.. Vulnerabilities, .. other.Vulnerabilities],
            Petrified = Petrified || other.Petrified,
        };
    }

    /// <summary>The entry that makes the target immune to this damage, or null.</summary>
    public DamageAdjustmentEntry? Immunity(string? damageType, bool magical, bool silvered = false, bool adamantine = false) =>
        Deciding(Immunities, damageType, magical, silvered, adamantine);

    /// <summary>The entry that makes the target resistant to this damage, or null (Petrified is reported apart).</summary>
    public DamageAdjustmentEntry? Resistance(string? damageType, bool magical, bool silvered = false, bool adamantine = false) =>
        Deciding(Resistances, damageType, magical, silvered, adamantine);

    /// <summary>The entry that makes the target vulnerable to this damage, or null.</summary>
    public DamageAdjustmentEntry? Vulnerability(string? damageType, bool magical, bool silvered = false, bool adamantine = false) =>
        Deciding(Vulnerabilities, damageType, magical, silvered, adamantine);

    // The simulator's codes (CombatantTemplate.Qualifier): the lowest code among the covering entries decides.
    private const int Always = 1;
    private const int Nonmagical = 2;
    private const int NonmagicalNotSilvered = 3;
    private const int NonmagicalNotAdamantine = 4;

    private static int Code(string? qualifier) => qualifier switch
    {
        StatBlockValues.DamageQualifiers.Nonmagical => Nonmagical,
        StatBlockValues.DamageQualifiers.NonmagicalNotSilvered => NonmagicalNotSilvered,
        StatBlockValues.DamageQualifiers.NonmagicalNotAdamantine => NonmagicalNotAdamantine,
        _ => Always,
    };

    private static bool Applies(int code, bool magical, bool silvered, bool adamantine) => code switch
    {
        Nonmagical => !magical,
        NonmagicalNotSilvered => !magical && !silvered,
        NonmagicalNotAdamantine => !magical && !adamantine,
        _ => true,
    };

    private static DamageAdjustmentEntry? Deciding(IReadOnlyList<DamageAdjustmentEntry> entries, string? damageType, bool magical, bool silvered, bool adamantine)
    {
        DamageAdjustmentEntry? deciding = null;
        var best = int.MaxValue;
        foreach (var entry in entries)
        {
            if (!entry.Covers(damageType))
            {
                continue;
            }

            var code = Code(entry.Qualifier);
            if (code < best)
            {
                best = code;
                deciding = entry;
            }
        }

        return deciding is not null && Applies(best, magical, silvered, adamantine) ? deciding : null;
    }

    private static string Canonical(string type, bool allowAll)
    {
        if (allowAll && string.Equals(type, DamageAdjustmentEntry.AllTypes, StringComparison.OrdinalIgnoreCase))
        {
            return DamageAdjustmentEntry.AllTypes;
        }

        return DslValues.DamageTypes.Set.TryMatch(type, out var canonical)
            ? canonical
            : throw new ArgumentException($"\"{type}\" is not a damage type{(allowAll ? " or \"all\"" : "")}.", nameof(type));
    }
}
