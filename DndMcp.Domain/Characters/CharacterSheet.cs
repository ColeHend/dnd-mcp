using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// One <c>character_sheet</c> row, parsed: every column, with its JSON parsed into the records below (contract §4 gives
/// the stored shapes; <see cref="SheetJson"/> reads and writes them). Any character entity may have one, a PC's or an
/// NPC's (contract D19); the sheet never creates the entity.
///
/// <para>
/// <b>Immutable, changed with <c>with</c>.</b> Every sheet operation (<see cref="SheetUpdate"/>, <see cref="SheetRest"/>,
/// <see cref="SheetLevelUp"/>, <see cref="SheetUse"/>, <see cref="SheetConditions"/>, <see cref="SheetXp"/>, and the
/// Repository's damage and healing) takes a sheet and returns a new one; <see cref="SheetDiff.Between"/> then says what
/// changed, column by column and key by key, which is what the Repository logs. Two sheets are compared through their
/// stored text (<see cref="SheetJson.Column"/>), never with record equality: the lists and maps here compare by reference.
/// </para>
/// <para>
/// <b>Nothing is lost when a column is rewritten.</b> Members a reader does not recognise (a key a later version or the
/// Phase 8 import added) are kept: inside an entry in its <c>Extra</c>, and at the top of the four map columns (abilities,
/// hit_dice, spell_slots, resources) in <see cref="Extras"/>. Entries of the array columns that this version cannot read
/// (no name, a class level below 1) are kept verbatim in <see cref="Unreadable"/> and written back where they were. A
/// value of the wrong JSON type is refused when the row is read (<see cref="SheetJson.FromColumns"/>), never read as a
/// default that the next write would store.
/// </para>
/// <para>
/// <b>Edition.</b> <see cref="Ruleset"/> null means the campaign's; every operation takes the campaign's ruleset as its
/// fallback and reads <see cref="EditionOr"/>. Effective maximum HP is <see cref="HitPointMath.EffectiveMaxHp"/> and
/// nothing else, so the sheet, the tracker and the simulator agree on it.
/// </para>
/// </summary>
public sealed record CharacterSheet
{
    /// <summary>The character entity's id (the primary key).</summary>
    public required string EntityId { get; init; }

    /// <summary>Who plays the character. Author-only: never in a non-author view.</summary>
    public string? Player { get; init; }

    /// <summary>"2014" or "2024", or null for the campaign's ruleset.</summary>
    public string? Ruleset { get; init; }

    public string? Species { get; init; }

    public string? Lineage { get; init; }

    public string? Background { get; init; }

    public string? Size { get; init; }

    /// <summary>The classes, the starting class first (it gives the level 1 hit points and the saving throws).</summary>
    public IReadOnlyList<SheetClass> Classes { get; init; } = [];

    /// <summary>The character level: the sum of the class levels when there are classes; alone on a minimal sheet.</summary>
    public int? Level { get; init; }

    /// <summary>Experience points; null = no XP total (an XP award is recorded but this stays null; never "milestone": review U02).</summary>
    public int? Xp { get; init; }

    /// <summary>Ability scores by key (<c>str</c> … <c>cha</c>); any subset.</summary>
    public IReadOnlyDictionary<string, int> Abilities { get; init; } = SheetMaps.Empty<int>();

    public SheetSaves Saves { get; init; } = SheetSaves.None;

    /// <summary>Reserved for Phase 8 (the raw JSON object; v1 never writes it).</summary>
    public string Skills { get; init; } = SheetJson.EmptyObject;

    /// <summary>Base AC: armour and shield, never Bladesong or Shield (those are fight effects), so nothing double-counts.</summary>
    public int? Ac { get; init; }

    public int? MaxHp { get; init; }

    /// <summary>A recorded reduction of the maximum (Life Drain, mummy rot); cleared by a long rest (contract D3).</summary>
    public int MaxHpReduction { get; init; }

    /// <summary>Current hit points; null = not tracked.</summary>
    public int? Hp { get; init; }

    public int TempHp { get; init; }

    public int? Speed { get; init; }

    /// <summary>Other speeds (the raw JSON object, e.g. {"fly":60}).</summary>
    public string Movement { get; init; } = SheetJson.EmptyObject;

    /// <summary>Senses (the raw JSON object, e.g. {"darkvision":60}).</summary>
    public string Senses { get; init; } = SheetJson.EmptyObject;

    /// <summary>Null reads as the Dex modifier (<see cref="InitiativeOrDex"/>).</summary>
    public int? InitiativeBonus { get; init; }

    public int? PassivePerception { get; init; }

    public int? SpellSaveDc { get; init; }

    public int? SpellAttack { get; init; }

    public SheetDefenses Defenses { get; init; } = SheetDefenses.None;

    /// <summary>Hit Dice by die ("d6", "d10"): the maximum derived from the classes, the number spent.</summary>
    public IReadOnlyDictionary<string, HitDiceEntry> HitDice { get; init; } = SheetMaps.Empty<HitDiceEntry>();

    /// <summary>Spell slots by key: "1" … "9" and "pact" (a level with no slots is absent).</summary>
    public IReadOnlyDictionary<string, SpellSlotEntry> SpellSlots { get; init; } = SheetMaps.Empty<SpellSlotEntry>();

    /// <summary>Class and custom resources by the slug of their name (<see cref="SheetResource.KeyOf"/>), in the order added.</summary>
    public IReadOnlyDictionary<string, SheetResource> Resources { get; init; } = SheetMaps.Empty<SheetResource>();

    /// <summary>Conditions and named effects that persist outside a fight (never exhaustion: <see cref="Exhaustion"/>).</summary>
    public IReadOnlyList<SheetCondition> Conditions { get; init; } = [];

    public SheetConcentration? Concentration { get; init; }

    public SheetDeathSaves DeathSaves { get; init; } = SheetDeathSaves.Reset;

    /// <summary>The Exhaustion level, 0-6 (6 = dead).</summary>
    public int Exhaustion { get; init; }

    public bool Inspiration { get; init; }

    public IReadOnlyList<SheetEntry> Feats { get; init; } = [];

    public IReadOnlyList<SheetEntry> Features { get; init; } = [];

    public IReadOnlyList<SheetEntry> Spells { get; init; } = [];

    public IReadOnlyList<SheetEntry> Languages { get; init; } = [];

    /// <summary>The stored sim_profile: a canonical <see cref="BuildSpec"/> as <see cref="DslJson.Options"/> write it (<see cref="SimProfile"/>).</summary>
    public string? SimProfile { get; init; }

    /// <summary>Free text (notes_md). Author-only.</summary>
    public string NotesMd { get; init; } = string.Empty;

    /// <summary>Where the sheet came from (<see cref="SheetValues.SheetSources"/>, or other one-line text).</summary>
    public string? SheetSource { get; init; }

    /// <summary>Set by the Repository on insert; the sheet operations never change it.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Stamped by the Repository on every write; the sheet operations never change it.</summary>
    public string? UpdatedAt { get; init; }

    /// <summary>
    /// For the map columns (abilities, hit_dice, spell_slots, resources): the stored top-level members that are not an
    /// entry this model reads (a non-object value, an unknown ability key), as a compact JSON object per column, written
    /// back as they were.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } = SheetMaps.Empty<string>();

    /// <summary>
    /// For the array columns (classes, conditions, feats, features, spells, languages): the stored items this version could
    /// not read (a class with no name or a level below 1, an entry with no name), verbatim, each with its index in the
    /// stored array. The sheet operations never see them; <see cref="SheetJson.Column"/> writes them back at their places,
    /// so a rewrite of the column (even a list replaced by <c>update</c>) never drops one.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SheetRawItem>> Unreadable { get; init; } = SheetMaps.Empty<IReadOnlyList<SheetRawItem>>();

    /// <summary>A sheet with nothing on it but its key and the column defaults: what <see cref="SheetUpdate"/> creates from.</summary>
    public static CharacterSheet New(string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return new CharacterSheet { EntityId = entityId };
    }

    /// <summary>The sheet's ruleset, else <paramref name="fallback"/> (the campaign's).</summary>
    public string EditionOr(string fallback) => Ruleset ?? fallback;

    /// <summary>The ability score, or null when the sheet does not give it.</summary>
    public int? Score(string ability) => Abilities.TryGetValue(ability, out var score) ? score : null;

    /// <summary>The ability modifier, or null when the sheet does not give the score.</summary>
    public int? Modifier(string ability) => Score(ability) is { } score ? DslLimits.AbilityModifier(score) : null;

    /// <summary>The initiative bonus, else the Dex modifier, else 0 (contract §7.2: "read as Dex mod when null").</summary>
    public int InitiativeOrDex => InitiativeBonus ?? Modifier(V.Abilities.Dex) ?? 0;

    /// <summary>The proficiency bonus by <see cref="Level"/>, or null without a level.</summary>
    public int? ProficiencyBonus => Level is { } level and >= DslLimits.MinLevel and <= DslLimits.MaxLevel ? DslLimits.ProficiencyBonus(level) : null;

    /// <summary>The effective hit point maximum (<see cref="HitPointMath.EffectiveMaxHp"/>), or null without a maximum.</summary>
    public int? EffectiveMaxHp(string edition) =>
        MaxHp is { } max ? HitPointMath.EffectiveMaxHp(max, MaxHpReduction, Exhaustion, EditionOr(edition)) : null;

    /// <summary>
    /// Dead: three failed death saves, Exhaustion 6, or an effective maximum of 0 (contract §4, D16). Healing and rests
    /// refuse a dead character.
    /// </summary>
    public bool IsDead(string edition) =>
        DeathSaves.Failures >= 3 || Exhaustion >= SheetLimits.MaxExhaustion || EffectiveMaxHp(edition) == 0;

    /// <summary>At 0 HP, not stable and not dead.</summary>
    public bool IsDying(string edition) => Hp == 0 && !DeathSaves.Stable && !IsDead(edition);

    /// <summary>
    /// The class with the most levels; on a tie the one listed first (contract D7: the simulator's archetype). Null with no
    /// classes.
    /// </summary>
    public SheetClass? MainClass =>
        Classes.Count == 0 ? null : Classes.Aggregate((best, next) => next.Level > best.Level ? next : best);

    /// <summary>"wizard 12" or "ranger 6 / rogue 6": the classes as a note names them (class indexes or names as stored).</summary>
    public string ClassSummary =>
        string.Join(" / ", Classes.Select(c => $"{c.Class} {c.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
}

/// <summary>
/// One class on a sheet: <c>{"class":"wizard","subclass":"bladesinger","level":12,"hit_die":6}</c>. <see cref="Class"/>
/// is the SRD class index when the name matched one of the 12 (<see cref="ClassTable"/>), else the name as given
/// (homebrew); <see cref="HitDie"/> is always stored (the SRD's for an SRD class).
/// </summary>
public sealed record SheetClass(string Class, string? Subclass, int Level, int? HitDie)
{
    /// <summary>Stored members this model does not read, as a compact JSON object (kept on rewrite).</summary>
    public string? Extra { get; init; }

    /// <summary>The SRD class, when <see cref="Class"/> is one.</summary>
    public ClassInfo? Srd => ClassTable.Find(Class);

    /// <summary>The hit die: the stored one, else the SRD class's, else null (an old homebrew row without one).</summary>
    public int? Die => HitDie ?? Srd?.HitDie;
}

/// <summary>
/// Saving throws: <c>{"proficient":["int","wis","con"],"bonus":{"con":1}}</c>. <see cref="Bonus"/> is a flat extra per
/// ability (a Ring of Protection, a paladin's aura given by hand), added to modifier + proficiency. Both are as stored
/// (what this server writes is canonical ability keys; a later version's word is kept, never dropped), so a user takes
/// the ability keys from them (<see cref="IsProficient"/>, <see cref="Abilities"/>).
/// </summary>
public sealed record SheetSaves(IReadOnlyList<string> Proficient, IReadOnlyDictionary<string, int> Bonus)
{
    public static SheetSaves None { get; } = new([], SheetMaps.Empty<int>());

    public string? Extra { get; init; }

    public bool IsProficient(string ability) => Proficient.Contains(ability, StringComparer.Ordinal);

    /// <summary>The proficient saves that are ability keys, each once, in stored order (what the simulator takes).</summary>
    public IReadOnlyList<string> Abilities => Proficient.Where(V.Abilities.All.Contains).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Damage and condition defences: <c>{"resist":["fire"],"immune":["poison"],"vulnerable":[],"condition_immune":["poisoned"]}</c>
/// (damage types and SRD condition names, canonical lower case).
/// </summary>
public sealed record SheetDefenses(
    IReadOnlyList<string> Resist,
    IReadOnlyList<string> Immune,
    IReadOnlyList<string> Vulnerable,
    IReadOnlyList<string> ConditionImmune)
{
    public static SheetDefenses None { get; } = new([], [], [], []);

    public string? Extra { get; init; }
}

/// <summary>One die size's Hit Dice: <c>{"max":12,"used":0}</c>.</summary>
public sealed record HitDiceEntry(int Max, int Used)
{
    public string? Extra { get; init; }

    public int Left => Math.Max(0, Max - Used);
}

/// <summary>
/// One spell level's slots, <c>{"max":4,"used":1}</c>, or the Pact Magic slots, <c>{"level":3,"max":2,"used":1}</c>
/// (<see cref="Level"/> only on the <c>pact</c> key).
/// </summary>
public sealed record SpellSlotEntry(int Max, int Used, int? Level = null)
{
    /// <summary>The Pact Magic key of <c>spell_slots</c>.</summary>
    public const string PactKey = "pact";

    public string? Extra { get; init; }

    public int Left => Math.Max(0, Max - Used);

    /// <summary>"1" … "9": the key of a spell level.</summary>
    public static string KeyOf(int spellLevel) => spellLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A resource: counted, <c>{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest"}</c>, or a free-form tracker with a
/// <see cref="State"/> instead of a maximum, <c>{"name":"Contingency","state":"set","note":"Polymorph → T-rex at low HP"}</c>.
/// Keyed in <see cref="CharacterSheet.Resources"/> by <see cref="KeyOf"/> of its name.
/// </summary>
public sealed record SheetResource(string Name, int? Max, int? Used, string? Recharge, string? State, string? Note)
{
    public string? Extra { get; init; }

    /// <summary>A counted resource (it has a maximum); otherwise a state tracker.</summary>
    public bool IsCounted => Max is not null;

    public int Left => Math.Max(0, (Max ?? 0) - (Used ?? 0));

    /// <summary>The recharge, <see cref="SheetValues.Recharges.Default"/> when none is stored.</summary>
    public string RechargeOrDefault => Recharge ?? SheetValues.Recharges.Default;

    /// <summary>"Bladesong" → "bladesong", "Channel Divinity" → "channel-divinity": the key (the campaign slug of the name), or "" for a name with no letters or digits.</summary>
    public static string KeyOf(string name) => Campaign.CampaignSlugs.From(name, string.Empty);
}

/// <summary>
/// A condition or named effect that persists outside a fight:
/// <c>{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"…"}</c> or
/// <c>{"name":"Circle of Power","effect":{},"duration":"rounds","remaining_rounds":98,"note":"…"}</c>.
/// <see cref="Source"/> is a party-safe name (never an entity name the party may not know, never a combatant id);
/// <see cref="Note"/> is author text; <see cref="Effect"/> is the fight's effect object (AC, resistances), kept raw.
/// </summary>
public sealed record SheetCondition(string Name, string? Source, string? Duration, int? RemainingRounds, string? Note, string? Effect = null)
{
    public string? Extra { get; init; }

    /// <summary>The duration, <see cref="SheetValues.Durations.UntilRemoved"/> when none is stored.</summary>
    public string DurationOrDefault => Duration ?? SheetValues.Durations.UntilRemoved;

    /// <summary>A timed entry (<c>rounds</c>): rests end it.</summary>
    public bool IsTimed => DurationOrDefault == SheetValues.Durations.Rounds;
}

/// <summary>
/// The spell held: <c>{"spell":"Circle of Power","level":5,"remaining_rounds":98,"note":"…"}</c>. <see cref="Spell"/> is
/// empty when the stored object names none (a later version's shape, kept as it was): it is still a concentration, ended
/// by what ends one.
/// </summary>
public sealed record SheetConcentration(string Spell, int? Level, int? RemainingRounds, string? Note)
{
    public string? Extra { get; init; }

    /// <summary>The spell's name for a note: the spell, or "an unnamed spell".</summary>
    public string Display => Spell.Length > 0 ? Spell : "an unnamed spell";
}

/// <summary>
/// Death saves: <c>{"successes":0,"failures":0,"stable":false}</c>. At 0 HP with fewer than 3 failures and not stable the
/// character is dying; 3 failures = dead.
/// </summary>
public sealed record SheetDeathSaves(int Successes, int Failures, bool Stable)
{
    /// <summary>The column default: no successes, no failures, not stable.</summary>
    public static SheetDeathSaves Reset { get; } = new(0, 0, false);

    public string? Extra { get; init; }

    public bool IsReset => Successes == 0 && Failures == 0 && !Stable;
}

/// <summary>
/// An item of an array column that this version could not read, kept verbatim: <see cref="Json"/> is its compact text and
/// <see cref="Index"/> its place in the stored array, where <see cref="SheetJson"/> writes it back.
/// </summary>
public sealed record SheetRawItem(int Index, string Json);

/// <summary>A feat, feature, spell or language: <c>{"name":"Tough"}</c> with optional <c>ref</c>, <c>source</c>, <c>prepared</c>.</summary>
public sealed record SheetEntry(string Name, string? Ref = null, string? Source = null, bool? Prepared = null)
{
    public string? Extra { get; init; }
}

/// <summary>
/// The insertion-ordered, read-only maps a sheet holds. Order matters: it is the order the columns are written (and so
/// displayed), and two sheets with the same entries must write the same text.
/// </summary>
public static class SheetMaps
{
    /// <summary>An empty map.</summary>
    public static IReadOnlyDictionary<string, T> Empty<T>() => new OrderedDictionary<string, T>(StringComparer.Ordinal);

    /// <summary>A map of the pairs, in order (a later duplicate key replaces the earlier value in its place).</summary>
    public static IReadOnlyDictionary<string, T> Of<T>(IEnumerable<KeyValuePair<string, T>> pairs)
    {
        var map = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            map[key] = value;
        }

        return map;
    }

    /// <summary>A copy with <paramref name="key"/> set (in place when present, else appended).</summary>
    public static IReadOnlyDictionary<string, T> With<T>(IReadOnlyDictionary<string, T> map, string key, T value)
    {
        var copy = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            copy[k] = v;
        }

        copy[key] = value;
        return copy;
    }

    /// <summary>A copy without <paramref name="key"/>.</summary>
    public static IReadOnlyDictionary<string, T> Without<T>(IReadOnlyDictionary<string, T> map, string key) =>
        Of(map.Where(p => !string.Equals(p.Key, key, StringComparison.Ordinal)));

    /// <summary>A copy with every value mapped, keys and order kept.</summary>
    public static IReadOnlyDictionary<string, TOut> Select<T, TOut>(IReadOnlyDictionary<string, T> map, Func<string, T, TOut> select) =>
        Of(map.Select(p => new KeyValuePair<string, TOut>(p.Key, select(p.Key, p.Value))));
}
