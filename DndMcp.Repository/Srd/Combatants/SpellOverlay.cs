using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// The combat facts of spells that the spell records lack: <c>content/overrides/spells.{2014,2024}.json</c>, keyed by
/// spell index.
///
/// <para>
/// <b>Why an overlay.</b> No 2024 spell record has a saving throw, an area or a healing table, and its damage is one
/// entry at the base slot: Flame Strike loses its radiant half and Magic Missile has no dice at all. A simulator reading
/// those records would cast a Fireball nobody can save against. 2014 records carry most of it, but not everything a
/// fight needs (Scorching Ray's three rays, Magic Missile's darts hitting automatically, Eldritch Blast's beams, Hold
/// Person's paralysis). Each entry states those facts in the SRD's own numbers (2024 from the SRD 5.2 markdown
/// <c>07_Spells.md</c>, 2014 from the SRD 5.1 markdown); <c>SpellOverlayTests</c> re-reads the markdown where it is checked
/// out and fails on any number the text does not say.
/// </para>
/// <para>
/// <b>What is not here.</b> Which spells have no combat effect, or one the simulator does not model, is a judgment,
/// not a fact of the text: that catalogue is <see cref="SpellNormalizer"/>'s, in code, with its own test. The one
/// judgment an entry may carry is <see cref="SpellOverlayEntry.Approximation"/>: how a lingering effect is simplified.
/// </para>
/// </summary>
public sealed class SpellOverlay
{
    private static readonly JsonSerializerOptions FileJson = CreateOptions();

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, SpellOverlayEntry>> _byEdition;

    private SpellOverlay(IReadOnlyDictionary<string, IReadOnlyDictionary<string, SpellOverlayEntry>> byEdition)
    {
        _byEdition = byEdition;
    }

    /// <summary>No overlay: what the spell records alone say.</summary>
    public static SpellOverlay Empty { get; } = new(new Dictionary<string, IReadOnlyDictionary<string, SpellOverlayEntry>>());

    /// <summary>
    /// The file name for an edition: <c>spells.2024.json</c>, in the same <see cref="MonsterOverrides.DirectoryName"/>
    /// directory as the monster overrides (both are normalization facts the records lack).
    /// </summary>
    public static string FileName(string edition) => $"spells.{edition}.json";

    /// <summary>Every entry of one edition, by spell index.</summary>
    public IReadOnlyDictionary<string, SpellOverlayEntry> Entries(string edition) =>
        _byEdition.GetValueOrDefault(edition) ?? new Dictionary<string, SpellOverlayEntry>();

    /// <summary>The entry for one spell, or null.</summary>
    public SpellOverlayEntry? For(string edition, string slug) => _byEdition.GetValueOrDefault(edition)?.GetValueOrDefault(slug);

    /// <summary>Reads both editions' files from <c>&lt;contentRoot&gt;/overrides/</c>.</summary>
    /// <exception cref="FileNotFoundException">A file is missing: they ship with the content, so this is a broken install.</exception>
    /// <exception cref="InvalidDataException">A file is malformed or an entry breaks a rule described on <see cref="SpellOverlayEntry"/>.</exception>
    public static SpellOverlay Load(string contentRoot)
    {
        var files = new List<(string, string)>();
        foreach (var edition in SrdEdition.All)
        {
            var path = Path.Combine(contentRoot, MonsterOverrides.DirectoryName, FileName(edition));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No spell overlay at {path}; it ships with the content directory.", path);
            }

            files.Add((edition, File.ReadAllText(path)));
        }

        return Parse(files);
    }

    /// <inheritdoc cref="Load"/>
    public static SpellOverlay Parse(IEnumerable<(string Edition, string Json)> files)
    {
        var byEdition = new Dictionary<string, IReadOnlyDictionary<string, SpellOverlayEntry>>(StringComparer.Ordinal);
        foreach (var (edition, json) in files)
        {
            Dictionary<string, SpellOverlayEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, SpellOverlayEntry>>(json, FileJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FileName(edition)} is not valid: {ex.Message}", ex);
            }

            if (entries is null)
            {
                throw new InvalidDataException($"{FileName(edition)} is JSON null.");
            }

            foreach (var (slug, entry) in entries)
            {
                entry.Validate($"{FileName(edition)}: \"{slug}\"");
            }

            byEdition[edition] = entries;
        }

        return new SpellOverlay(byEdition);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// One spell's combat facts. Every field is optional: in 2014 an entry supplements what the record's structure gives
/// (fields set here win), in 2024 it is the whole profile. Dice are the SRD's at the spell's own level; upcast fields
/// are per slot level above it.
/// </summary>
public sealed record SpellOverlayEntry
{
    /// <summary><see cref="StatBlockValues.ActionKinds"/>: attack, save, auto_hit, heal or parry.</summary>
    public string? Kind { get; init; }

    /// <summary>Attack spells: melee or ranged.</summary>
    public string? Attack { get; init; }

    /// <summary>Save spells: the ability key.</summary>
    public string? Save { get; init; }

    /// <summary>Save spells: half or none.</summary>
    public string? OnSuccess { get; init; }

    public IReadOnlyList<SpellOverlayDamage>? Damage { get; init; }

    /// <summary>Damage added per slot level above the spell's level (Fireball: 1d6 fire).</summary>
    public IReadOnlyList<SpellOverlayDamage>? Upcast { get; init; }

    /// <summary>The spellcasting ability modifier is added to the damage (Spiritual Weapon).</summary>
    public bool? DamageAddsModifier { get; init; }

    /// <summary>Cantrips: <see cref="CantripScalings"/> (dice ×2/×3/×4 at levels 5/11/17, or that many beams).</summary>
    public string? Cantrip { get; init; }

    /// <summary>Rays, darts or creatures at the spell's level (Scorching Ray 3, Magic Missile 3, Chain Lightning 4).</summary>
    public int? Targets { get; init; }

    /// <summary>Extra rays, darts or creatures per slot level above the spell's level.</summary>
    public int? UpcastTargets { get; init; }

    public AreaOverride? Area { get; init; }

    /// <summary>The spell affects chosen creatures, not an area, whatever the record says (2014 Disintegrate's 10-foot cube is for objects).</summary>
    public bool? NoArea { get; init; }

    public SpellOverlayCondition? Condition { get; init; }

    /// <summary>Heal spells: the dice restored at the spell's level.</summary>
    public string? Heal { get; init; }

    /// <summary>Heal spells: the spellcasting ability modifier is added.</summary>
    public bool? HealModifier { get; init; }

    /// <summary>Heal spells: dice added per slot level above.</summary>
    public string? UpcastHeal { get; init; }

    /// <summary>Parry spells (Shield): the AC bonus.</summary>
    public int? AcBonus { get; init; }

    /// <summary>
    /// Power Word Kill: "If the target has 100 Hit Points or fewer, it dies" (both editions). A target with at most this
    /// many hit points dies outright; one with more takes <see cref="Damage"/>, if any.
    /// </summary>
    public int? KillAtOrBelowHp { get; init; }

    /// <summary>
    /// How the simulator simplifies an effect that lingers (a zone's damage dealt once, on casting). Becomes an
    /// <c>approximated</c> warning on every monster that casts the spell.
    /// </summary>
    public string? Approximation { get; init; }

    /// <summary>Anything worth saying about the entry (a rider not modelled, a target restriction).</summary>
    public string? Note { get; init; }

    internal void Validate(string where)
    {
        if (Kind is not null && !SpellProfileKinds.Combat.Contains(Kind))
        {
            throw new InvalidDataException($"{where}: kind \"{Kind}\" is not {string.Join(", ", SpellProfileKinds.Combat)}.");
        }

        if (Attack is not null && !StatBlockValues.AttackRanges.All.Contains(Attack))
        {
            throw new InvalidDataException($"{where}: attack must be melee or ranged.");
        }

        if (Save is not null && !DslValues.Abilities.All.Contains(Save))
        {
            throw new InvalidDataException($"{where}: save must be an ability key.");
        }

        if (OnSuccess is not null && !StatBlockValues.OnSuccess.All.Contains(OnSuccess))
        {
            throw new InvalidDataException($"{where}: on_success must be half or none.");
        }

        if (Cantrip is not null && !CantripScalings.All.Contains(Cantrip))
        {
            throw new InvalidDataException($"{where}: cantrip must be {string.Join(" or ", CantripScalings.All)}.");
        }

        foreach (var d in (Damage ?? []).Concat(Upcast ?? []))
        {
            if (ProseText.Dice(d.Dice) is null || (d.Type is not null && !DslValues.DamageTypes.Set.Contains(d.Type)))
            {
                throw new InvalidDataException($"{where}: damage \"{d.Dice}\" {d.Type} is not dice and a damage type.");
            }
        }

        foreach (var dice in new[] { Heal, UpcastHeal }.OfType<string>())
        {
            if (ProseText.Dice(dice) is null)
            {
                throw new InvalidDataException($"{where}: \"{dice}\" is not dice.");
            }
        }

        if (Area is { } area && (!StatBlockValues.Shapes.All.Contains(area.Shape) || area.Size is < 1 or > 1000))
        {
            throw new InvalidDataException($"{where}: area needs a shape ({string.Join(", ", StatBlockValues.Shapes.All)}) and a size 1-1000; it has {area.Shape} {area.Size}.");
        }

        // The same ranges as the monster overrides': a typo ("targets": 30, "ac_bonus": 50) fails at load instead of
        // turning one spell into a party wipe.
        foreach (var (field, value, min, max) in new (string, int?, int, int)[]
                 {
                     ("targets", Targets, 1, 20), ("upcast_targets", UpcastTargets, 0, 10), ("ac_bonus", AcBonus, 1, 10),
                     ("kill_at_or_below_hp", KillAtOrBelowHp, 1, 1000),
                 })
        {
            if (value is { } n && (n < min || n > max))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{where}: {field} must be {min}-{max}; it is {n}."));
            }
        }

        if (Condition is { } condition && (StatBlockValues.Conditions.All.Contains(condition.Condition) is false ||
                                           StatBlockValues.Durations.All.Contains(condition.Duration) is false))
        {
            throw new InvalidDataException($"{where}: condition needs a condition and a duration from the stat block vocabulary.");
        }
    }
}

/// <summary>A damage roll in the overlay; <see cref="Type"/> null only where the spell's type is chosen (Chromatic Orb).</summary>
public sealed record SpellOverlayDamage(string Dice, string? Type);

/// <summary>
/// The condition a failed save (or a hit) imposes. For <c>save_ends</c> the repeated save is the spell's own; for
/// <c>until_escape</c> the escape DC is the spell's save DC.
/// </summary>
public sealed record SpellOverlayCondition(string Condition, string Duration, int? Rounds = null);
