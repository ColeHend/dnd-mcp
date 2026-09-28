using System.Collections.Concurrent;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// A corrected srd.db monster document → the <see cref="StatBlock"/> the simulator runs, <c>rules_get format
/// "combatant"</c> shows and <c>balance_dpr</c> targets.
///
/// <para>
/// <b>Corrected records only.</b> It reads <see cref="SrdDocument.Json"/>, which carries <c>srd-corrections.json</c>; the
/// vendored files do not (the uncorrected 2024 Mule has the Octopus's actions). Callers hand it index documents, and
/// tests build documents through the same corrections.
/// </para>
/// <para>
/// <b>Never silent.</b> Every trait, action, reaction, legendary action and spell ends up in the stat block: run by the
/// simulator, named in <see cref="StatBlock.Notes"/> as having no combat effect, or carried with a
/// <see cref="NormalizationWarning"/> saying what is left out. It never throws for a record in the vendored data; the
/// normalizer's tests normalize all 675 and pin the warning counts per edition and code.
/// </para>
/// <para>
/// Thread-safe and meant to be shared: the host builds one per content root and normalizes on demand. Spell profiles
/// are cached by ref (a spell record never changes under a running server).
/// </para>
/// </summary>
public sealed class MonsterNormalizer
{
    private readonly ConcurrentDictionary<string, SpellProfile> _profiles = new(StringComparer.Ordinal);

    public MonsterNormalizer(MonsterOverrides monsters, SpellOverlay spells)
    {
        Monsters = monsters ?? throw new ArgumentNullException(nameof(monsters));
        Spells = spells ?? throw new ArgumentNullException(nameof(spells));
    }

    public MonsterOverrides Monsters { get; }

    public SpellOverlay Spells { get; }

    /// <summary>
    /// A monster document → its stat block. Spells are read through <paramref name="lookup"/> (edition, "spell", slug).
    /// Anything in the record the normalizer does not understand becomes a warning on the result.
    /// </summary>
    /// <exception cref="ArgumentException">The document is not a monster.</exception>
    /// <exception cref="InvalidDataException">The document's JSON is not a monster record of its edition (never true of vendored data).</exception>
    public StatBlock Normalize(SrdDocument monster, ISrdLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(monster);
        ArgumentNullException.ThrowIfNull(lookup);
        if (monster.Kind != SrdKinds.Monster)
        {
            throw new ArgumentException($"`{monster.Ref}` is a {monster.Kind}, not a monster.", nameof(monster));
        }

        var record = MonsterRecord.Read(monster);
        return new MonsterReading(this, record, Monsters.For(record.Edition, record.Index), lookup).Build(monster.Ref.ToString());
    }

    /// <summary>A spell document's profile, cached by ref.</summary>
    /// <exception cref="ArgumentException">The document is not a spell.</exception>
    public SpellProfile Profile(SrdDocument spell) =>
        _profiles.GetOrAdd(spell.Ref.ToString(), _ => SpellNormalizer.Normalize(spell, Spells));
}
