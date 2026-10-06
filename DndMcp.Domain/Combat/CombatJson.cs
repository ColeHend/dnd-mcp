using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The columns of <c>combatant</c> (contract §3), by name and in table order: what <see cref="CombatJson.Columns"/> writes
/// and <see cref="CombatJson.FromColumns"/> reads, and what the Repository's row record mirrors.
/// </summary>
public static class CombatantColumns
{
    public const string Id = "id";
    public const string EncounterId = "encounter_id";
    public const string EntityId = "entity_id";
    public const string Name = "name";
    public const string Side = "side";
    public const string InitGroup = "init_group";
    public const string SrdRef = "srd_ref";
    public const string Statblock = "statblock";
    public const string Initiative = "initiative";
    public const string InitBonus = "init_bonus";
    public const string Ac = "ac";
    public const string MaxHp = "max_hp";
    public const string MaxHpReduction = "max_hp_reduction";
    public const string Hp = "hp";
    public const string TempHp = "temp_hp";
    public const string DamageTaken = "damage_taken";
    public const string Conditions = "conditions";
    public const string Concentration = "concentration";
    public const string DeathSaves = "death_saves";
    public const string MakesDeathSaves = "makes_death_saves";
    public const string Exhaustion = "exhaustion";
    public const string Legendary = "legendary";
    public const string Resources = "resources";
    public const string SheetSnapshot = "sheet_snapshot";
    public const string ReactionUsed = "reaction_used";
    public const string Surprised = "surprised";
    public const string Hidden = "hidden";
    public const string Defeated = "defeated";
    public const string Dead = "dead";
    public const string Removed = "removed";
    public const string OrderKey = "order_key";
    public const string CreatedAt = "created_at";
    public const string UpdatedAt = "updated_at";

    /// <summary>Every column, in the order the migration creates them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Id, EncounterId, EntityId, Name, Side, InitGroup, SrdRef, Statblock, Initiative, InitBonus, Ac, MaxHp, MaxHpReduction, Hp,
        TempHp, DamageTaken, Conditions, Concentration, DeathSaves, MakesDeathSaves, Exhaustion, Legendary, Resources, SheetSnapshot,
        ReactionUsed, Surprised, Hidden, Defeated, Dead, Removed, OrderKey, CreatedAt, UpdatedAt,
    ];
}

/// <summary>
/// Reads and writes the JSON columns of <c>combatant</c> in the shapes of contract §4 (T owns them), a whole combatant to
/// and from its row, and a <c>turn</c> row's detail (<see cref="TurnRecord"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tolerant of what it does not know.</b> Unknown keys inside an object (a later build's, Phase 8's import) are kept in
/// the record's <c>Extra</c> and written back unchanged, so a step that rewrites a column never drops them. A value of
/// the wrong JSON type for a key this version reads is an <see cref="InvalidDataException"/> naming the column (never
/// quoting stored text: a combatant's names may be author-only): read as a default, it would be written over the stored
/// value by the next step.
/// </para>
/// <para>
/// <b>Writes are deterministic</b>: known keys in a fixed order, absent optional keys left out, compact text with relaxed
/// escaping (as <see cref="SheetJson"/> writes), so a step that changed nothing writes nothing
/// (<see cref="EncounterState.ChangedCombatants"/> compares this text).
/// </para>
/// </remarks>
public static class CombatJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly string[] ConditionKeys =
        ["id", "name", "source", "source_note", "duration", "skip_end", "expires", "save", "escape_dc", "held_by", "effect", "note", "applied", "knock_out"];

    private static readonly string[] ExpiryKeys = ["round", "at", "of"];
    private static readonly string[] SaveKeys = ["ability", "dc"];
    private static readonly string[] AppliedKeys = ["round", "turn_of"];
    private static readonly string[] EffectKeys = ["ac", "resist", "immune", "vulnerable", "except"];
    private static readonly string[] ConcentrationKeys = ["spell", "level", "duration", "expires", "pending", "applied", "note"];
    private static readonly string[] LegendaryKeys = ["actions", "used", "resistance", "resistance_used"];
    private static readonly string[] ResourceKeys = ["name", "level", "kind", "min", "ready", "max", "used", "state"];
    private static readonly string[] FactKeys = ["defenses", "con_save", "shadowed"];
    private const string Facts = "facts";

    // ---------------------------------------------------------------------------------------------------------------
    // Whole combatants

    /// <summary>
    /// Every column of the combatant as stored (string for text and JSON, long for integers and 0/1 flags, double for REAL,
    /// or null), in table order: the row the Repository inserts or updates. <see cref="CombatantState.EntityHandle"/> and
    /// <see cref="CombatantState.EntitySubtype"/> are not columns.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Columns(CombatantState c, string encounterId)
    {
        ArgumentNullException.ThrowIfNull(c);
        var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            [CombatantColumns.Id] = c.Id,
            [CombatantColumns.EncounterId] = encounterId,
            [CombatantColumns.EntityId] = c.EntityId,
            [CombatantColumns.Name] = c.Name,
            [CombatantColumns.Side] = c.Side,
            [CombatantColumns.InitGroup] = c.InitGroup,
            [CombatantColumns.SrdRef] = c.SrdRef,
            [CombatantColumns.Statblock] = c.StatBlock is null ? null : StatBlockSnapshotJson.Serialize(c.StatBlock),
            [CombatantColumns.Initiative] = c.Initiative,
            [CombatantColumns.InitBonus] = (long)c.InitBonus,
            [CombatantColumns.Ac] = Long(c.Ac),
            [CombatantColumns.MaxHp] = Long(c.MaxHp),
            [CombatantColumns.MaxHpReduction] = (long)c.MaxHpReduction,
            [CombatantColumns.Hp] = Long(c.Hp),
            [CombatantColumns.TempHp] = (long)c.TempHp,
            [CombatantColumns.DamageTaken] = (long)c.DamageTaken,
            [CombatantColumns.Conditions] = WriteConditions(c.Conditions),
            [CombatantColumns.Concentration] = WriteConcentration(c.Concentration),
            [CombatantColumns.DeathSaves] = WriteDeathSaves(c.DeathSaves),
            [CombatantColumns.MakesDeathSaves] = c.MakesDeathSaves ? 1L : 0L,
            [CombatantColumns.Exhaustion] = (long)c.Exhaustion,
            [CombatantColumns.Legendary] = WriteLegendary(c.Legendary),
            [CombatantColumns.Resources] = WriteResources(c.Resources),
            [CombatantColumns.SheetSnapshot] = WriteSnapshot(c.SheetSnapshot),
            [CombatantColumns.ReactionUsed] = c.ReactionUsed ? 1L : 0L,
            [CombatantColumns.Surprised] = c.Surprised ? 1L : 0L,
            [CombatantColumns.Hidden] = c.Hidden ? 1L : 0L,
            [CombatantColumns.Defeated] = c.Defeated ? 1L : 0L,
            [CombatantColumns.Dead] = c.Dead ? 1L : 0L,
            [CombatantColumns.Removed] = c.Removed ? 1L : 0L,
            [CombatantColumns.OrderKey] = c.OrderKey,
            [CombatantColumns.CreatedAt] = c.CreatedAt,
            [CombatantColumns.UpdatedAt] = c.UpdatedAt,
        };
        return row;
    }

    /// <summary>
    /// The combatant from its row's columns (name → value as SQLite returns it: string, long, double, or null; int is
    /// accepted). <c>id</c>, <c>name</c> and <c>side</c> are required.
    /// </summary>
    /// <param name="entityHandle">The linked entity's handle (not a column; the loader reads it from the entity).</param>
    /// <param name="entitySubtype">The linked character's subtype (not a column).</param>
    /// <exception cref="ArgumentException">A required column is missing, or a value is of another CLR type (a host bug).</exception>
    /// <exception cref="InvalidDataException">A stored JSON value is of the wrong type; the message names the column.</exception>
    public static CombatantState FromColumns(IReadOnlyDictionary<string, object?> row, string? entityHandle = null, string? entitySubtype = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var statblock = Text(row, CombatantColumns.Statblock);
        StatBlock? block = null;
        if (statblock is not null)
        {
            try
            {
                block = StatBlockSnapshotJson.Deserialize(statblock);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"combatant column {CombatantColumns.Statblock} is not a stat block snapshot.", ex);
            }
        }

        return new CombatantState
        {
            Id = Required(row, CombatantColumns.Id),
            EntityId = Text(row, CombatantColumns.EntityId),
            Name = Required(row, CombatantColumns.Name),
            Side = Required(row, CombatantColumns.Side),
            InitGroup = Text(row, CombatantColumns.InitGroup),
            SrdRef = Text(row, CombatantColumns.SrdRef),
            StatBlock = block,
            Initiative = Real(row, CombatantColumns.Initiative),
            InitBonus = Integer(row, CombatantColumns.InitBonus) ?? 0,
            Ac = Integer(row, CombatantColumns.Ac),
            MaxHp = Integer(row, CombatantColumns.MaxHp),
            MaxHpReduction = Integer(row, CombatantColumns.MaxHpReduction) ?? 0,
            Hp = Integer(row, CombatantColumns.Hp),
            TempHp = Integer(row, CombatantColumns.TempHp) ?? 0,
            DamageTaken = Integer(row, CombatantColumns.DamageTaken) ?? 0,
            Conditions = ReadConditions(Text(row, CombatantColumns.Conditions)),
            Concentration = ReadConcentration(Text(row, CombatantColumns.Concentration)),
            DeathSaves = ReadDeathSaves(Text(row, CombatantColumns.DeathSaves)),
            MakesDeathSaves = (Integer(row, CombatantColumns.MakesDeathSaves) ?? 0) != 0,
            Exhaustion = Integer(row, CombatantColumns.Exhaustion) ?? 0,
            Legendary = ReadLegendary(Text(row, CombatantColumns.Legendary)),
            Resources = ReadResources(Text(row, CombatantColumns.Resources)),
            SheetSnapshot = ReadSnapshot(Text(row, CombatantColumns.SheetSnapshot)),
            ReactionUsed = (Integer(row, CombatantColumns.ReactionUsed) ?? 0) != 0,
            Surprised = (Integer(row, CombatantColumns.Surprised) ?? 0) != 0,
            Hidden = (Integer(row, CombatantColumns.Hidden) ?? 0) != 0,
            Defeated = (Integer(row, CombatantColumns.Defeated) ?? 0) != 0,
            Dead = (Integer(row, CombatantColumns.Dead) ?? 0) != 0,
            Removed = (Integer(row, CombatantColumns.Removed) ?? 0) != 0,
            OrderKey = Real(row, CombatantColumns.OrderKey) ?? 0,
            CreatedAt = Text(row, CombatantColumns.CreatedAt),
            UpdatedAt = Text(row, CombatantColumns.UpdatedAt),
            EntityHandle = entityHandle,
            EntitySubtype = entitySubtype,
        };
    }

    /// <summary>
    /// Whether the two states store the same row: every column but the clock compared as stored text (the stat block
    /// snapshot, which no step changes, compared by reference first, so a step does not serialize every snapshot twice).
    /// </summary>
    public static bool SameState(CombatantState a, CombatantState b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var sameBlock = ReferenceEquals(a.StatBlock, b.StatBlock);
        var x = Columns(sameBlock ? a with { StatBlock = null } : a, string.Empty);
        var y = Columns(sameBlock ? b with { StatBlock = null } : b, string.Empty);
        foreach (var column in CombatantColumns.All)
        {
            if (column is CombatantColumns.CreatedAt or CombatantColumns.UpdatedAt)
            {
                continue;
            }

            if (!Equals(x[column], y[column]))
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // conditions

    /// <summary>The <c>conditions</c> array; NULL or empty reads as none.</summary>
    /// <exception cref="InvalidDataException">Not an array of objects with an id, a name and a duration, or a known key of the wrong type.</exception>
    public static IReadOnlyList<CombatCondition> ReadConditions(string? json)
    {
        var array = ArrayColumn(json, CombatantColumns.Conditions);
        var list = new List<CombatCondition>();
        if (array is null)
        {
            return list;
        }

        var item = 0;
        foreach (var node in array)
        {
            item++;
            var where = $"item {item.ToString(CultureInfo.InvariantCulture)}";
            if (node is not JsonObject o)
            {
                throw Bad(CombatantColumns.Conditions, where + " is not an object");
            }

            list.Add(ReadCondition(o, CombatantColumns.Conditions, where));
        }

        return list;
    }

    /// <summary>The <c>conditions</c> array text, in list order.</summary>
    public static string WriteConditions(IReadOnlyList<CombatCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var array = new JsonArray();
        foreach (var c in conditions)
        {
            array.Add(ConditionNode(c));
        }

        return Serialize(array);
    }

    private static CombatCondition ReadCondition(JsonObject o, string column, string where) =>
        new(ReqText(o, "id", column, where), ReqText(o, "name", column, where), ReqText(o, "duration", column, where))
        {
            Source = OptText(o, "source", column, where),
            SourceNote = OptText(o, "source_note", column, where),
            SkipEnd = OptBool(o, "skip_end", column, where) ?? false,
            Expires = OptObject(o, "expires", column, where) is { } e ? ReadExpiry(e, column, where) : null,
            Save = OptObject(o, "save", column, where) is { } s
                ? new ConditionSave(ReqText(s, "ability", column, where), ReqInt(s, "dc", column, where)) { Extra = Unknown(s, SaveKeys) }
                : null,
            EscapeDc = OptInt(o, "escape_dc", column, where),
            HeldBy = OptText(o, "held_by", column, where),
            Effect = OptObject(o, "effect", column, where) is { } effect ? ReadEffect(effect, column, where) : null,
            Note = OptText(o, "note", column, where),
            Applied = OptObject(o, "applied", column, where) is { } a ? ReadApplied(a, column, where) : null,
            KnockOut = OptBool(o, "knock_out", column, where) ?? false,
            Extra = Unknown(o, ConditionKeys),
        };

    private static JsonObject ConditionNode(CombatCondition c)
    {
        var o = new JsonObject { ["id"] = c.Id, ["name"] = c.Name };
        Put(o, "source", c.Source);
        Put(o, "source_note", c.SourceNote);
        o["duration"] = c.Duration;
        if (c.SkipEnd)
        {
            o["skip_end"] = true;
        }

        if (c.Expires is { } expires)
        {
            o["expires"] = ExpiryNode(expires);
        }

        if (c.Save is { } save)
        {
            o["save"] = WithExtra(new JsonObject { ["ability"] = save.Ability, ["dc"] = save.Dc }, save.Extra, SaveKeys);
        }

        Put(o, "escape_dc", c.EscapeDc);
        Put(o, "held_by", c.HeldBy);
        if (c.Effect is { IsEmpty: false } effect)
        {
            o["effect"] = EffectNode(effect);
        }

        Put(o, "note", c.Note);
        if (c.Applied is { } applied)
        {
            o["applied"] = AppliedNode(applied);
        }

        if (c.KnockOut)
        {
            o["knock_out"] = true;
        }

        return WithExtra(o, c.Extra, ConditionKeys);
    }

    private static ConditionExpiry ReadExpiry(JsonObject e, string column, string where) =>
        new(ReqInt(e, "round", column, where), ReqText(e, "at", column, where), OptText(e, "of", column, where)) { Extra = Unknown(e, ExpiryKeys) };

    private static JsonObject ExpiryNode(ConditionExpiry e)
    {
        var o = new JsonObject { ["round"] = e.Round, ["at"] = e.At };
        Put(o, "of", e.Of);
        return WithExtra(o, e.Extra, ExpiryKeys);
    }

    private static AppliedAt ReadApplied(JsonObject a, string column, string where) =>
        new(ReqInt(a, "round", column, where), OptText(a, "turn_of", column, where)) { Extra = Unknown(a, AppliedKeys) };

    private static JsonObject AppliedNode(AppliedAt a)
    {
        var o = new JsonObject { ["round"] = a.Round };
        Put(o, "turn_of", a.TurnOf);
        return WithExtra(o, a.Extra, AppliedKeys);
    }

    /// <summary>An effect object (<c>{"ac":5,"resist":["all"],"except":["psychic"]}</c>) from its text; null for NULL.</summary>
    /// <exception cref="InvalidDataException">Not an object, or a known key of the wrong type.</exception>
    public static CombatEffect? ReadEffect(string? json, string column = CombatantColumns.Conditions) =>
        ObjectColumn(json, column) is { } o ? ReadEffect(o, column, "effect") : null;

    /// <summary>The effect's compact text.</summary>
    public static string WriteEffect(CombatEffect effect) => Serialize(EffectNode(effect));

    private static CombatEffect ReadEffect(JsonObject o, string column, string where) =>
        new(OptInt(o, "ac", column, where), Strings(o, "resist", column, where), Strings(o, "immune", column, where),
            Strings(o, "vulnerable", column, where), Strings(o, "except", column, where))
        {
            Extra = Unknown(o, EffectKeys),
        };

    private static JsonObject EffectNode(CombatEffect e)
    {
        var o = new JsonObject();
        Put(o, "ac", e.Ac);
        PutList(o, "resist", e.Resist);
        PutList(o, "immune", e.Immune);
        PutList(o, "vulnerable", e.Vulnerable);
        PutList(o, "except", e.Except);
        return WithExtra(o, e.Extra, EffectKeys);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // concentration, death saves, legendary

    /// <summary>The <c>concentration</c> object, or null (NULL: not concentrating).</summary>
    /// <exception cref="InvalidDataException">Not an object with a spell, or a known key of the wrong type.</exception>
    public static CombatConcentration? ReadConcentration(string? json)
    {
        if (ObjectColumn(json, CombatantColumns.Concentration) is not { } o)
        {
            return null;
        }

        const string column = CombatantColumns.Concentration;
        var pending = new List<int>();
        if (o.TryGetPropertyValue("pending", out var p) && p is not null)
        {
            if (p is not JsonArray array)
            {
                throw Bad(column, "\"pending\" is not an array");
            }

            foreach (var dc in array)
            {
                pending.Add(IntValue(dc) ?? throw Bad(column, "\"pending\" holds a value that is not a whole number"));
            }
        }

        return new CombatConcentration(ReqText(o, "spell", column, string.Empty))
        {
            Level = OptInt(o, "level", column, string.Empty),
            Duration = OptText(o, "duration", column, string.Empty),
            Expires = OptObject(o, "expires", column, string.Empty) is { } e ? ReadExpiry(e, column, string.Empty) : null,
            Pending = pending,
            Applied = OptObject(o, "applied", column, string.Empty) is { } a ? ReadApplied(a, column, string.Empty) : null,
            Note = OptText(o, "note", column, string.Empty),
            Extra = Unknown(o, ConcentrationKeys),
        };
    }

    /// <summary>The object's text, or null (the column is NULL when nothing is held).</summary>
    public static string? WriteConcentration(CombatConcentration? c)
    {
        if (c is null)
        {
            return null;
        }

        var o = new JsonObject { ["spell"] = c.Spell };
        Put(o, "level", c.Level);
        Put(o, "duration", c.Duration);
        if (c.Expires is { } expires)
        {
            o["expires"] = ExpiryNode(expires);
        }

        if (c.Pending.Count > 0)
        {
            o["pending"] = new JsonArray(c.Pending.Select(d => (JsonNode)d).ToArray());
        }

        if (c.Applied is { } applied)
        {
            o["applied"] = AppliedNode(applied);
        }

        Put(o, "note", c.Note);
        return Serialize(WithExtra(o, c.Extra, ConcentrationKeys));
    }

    /// <summary><c>{"successes":0,"failures":0,"stable":false}</c>; NULL or a missing key reads as the reset state's.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong type.</exception>
    public static DeathSaveTally ReadDeathSaves(string? json)
    {
        if (ObjectColumn(json, CombatantColumns.DeathSaves) is not { } o)
        {
            return DeathSaveTally.Zero;
        }

        const string column = CombatantColumns.DeathSaves;
        return new DeathSaveTally(
            Math.Clamp(OptInt(o, "successes", column, string.Empty) ?? 0, 0, 3),
            Math.Clamp(OptInt(o, "failures", column, string.Empty) ?? 0, 0, 3),
            OptBool(o, "stable", column, string.Empty) ?? false);
    }

    /// <summary>All three keys, always (the column default's shape).</summary>
    public static string WriteDeathSaves(DeathSaveTally saves)
    {
        ArgumentNullException.ThrowIfNull(saves);
        return Serialize(new JsonObject { ["successes"] = saves.Successes, ["failures"] = saves.Failures, ["stable"] = saves.Stable });
    }

    /// <summary>The <c>legendary</c> object, or null.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong type.</exception>
    public static LegendaryState? ReadLegendary(string? json)
    {
        if (ObjectColumn(json, CombatantColumns.Legendary) is not { } o)
        {
            return null;
        }

        const string column = CombatantColumns.Legendary;
        return new LegendaryState(
            OptInt(o, "actions", column, string.Empty) ?? 0,
            OptInt(o, "used", column, string.Empty) ?? 0,
            OptInt(o, "resistance", column, string.Empty) ?? 0,
            OptInt(o, "resistance_used", column, string.Empty) ?? 0)
        {
            Extra = Unknown(o, LegendaryKeys),
        };
    }

    /// <summary>The object's text, or null.</summary>
    public static string? WriteLegendary(LegendaryState? l) => l is null
        ? null
        : Serialize(WithExtra(
            new JsonObject { ["actions"] = l.Actions, ["used"] = l.Used, ["resistance"] = l.Resistance, ["resistance_used"] = l.ResistanceUsed },
            l.Extra,
            LegendaryKeys));

    // ---------------------------------------------------------------------------------------------------------------
    // resources

    /// <summary>The <c>resources</c> object, key → entry, in stored order.</summary>
    /// <exception cref="InvalidDataException">Not an object of objects, or a known key of the wrong type.</exception>
    public static IReadOnlyDictionary<string, CombatResource> ReadResources(string? json)
    {
        var map = new OrderedDictionary<string, CombatResource>(StringComparer.Ordinal);
        if (ObjectColumn(json, CombatantColumns.Resources) is not { } o)
        {
            return map;
        }

        const string column = CombatantColumns.Resources;
        foreach (var (key, value) in o)
        {
            if (value is not JsonObject r)
            {
                throw Bad(column, "an entry is not an object");
            }

            map[key] = new CombatResource
            {
                Name = OptText(r, "name", column, "an entry"),
                Level = OptInt(r, "level", column, "an entry"),
                Kind = OptText(r, "kind", column, "an entry"),
                Min = OptInt(r, "min", column, "an entry"),
                Ready = OptBool(r, "ready", column, "an entry"),
                Max = OptInt(r, "max", column, "an entry"),
                Used = OptInt(r, "used", column, "an entry"),
                State = OptText(r, "state", column, "an entry"),
                Extra = Unknown(r, ResourceKeys),
            };
        }

        return map;
    }

    /// <summary>The object's text, entries in map order.</summary>
    public static string WriteResources(IReadOnlyDictionary<string, CombatResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var o = new JsonObject();
        foreach (var (key, r) in resources)
        {
            var item = new JsonObject();
            Put(item, "name", r.Name);
            Put(item, "level", r.Level);
            Put(item, "kind", r.Kind);
            Put(item, "min", r.Min);
            if (r.Ready is { } ready)
            {
                item["ready"] = ready;
            }

            Put(item, "max", r.Max);
            Put(item, "used", r.Used);
            Put(item, "state", r.State);
            o[key] = WithExtra(item, r.Extra, ResourceKeys);
        }

        return Serialize(o);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // the sheet snapshot

    /// <summary>
    /// The <c>sheet_snapshot</c>: its column members turned back into stored values (JSON columns as compact text, numbers
    /// as long, as <see cref="SheetJson.FromColumns"/> takes them) and its facts; null when NULL (not sheet-seeded).
    /// </summary>
    /// <exception cref="InvalidDataException">Not an object, or a member of the wrong type.</exception>
    public static SheetSnapshot? ReadSnapshot(string? json)
    {
        if (ObjectColumn(json, CombatantColumns.SheetSnapshot) is not { } o)
        {
            return null;
        }

        const string column = CombatantColumns.SheetSnapshot;
        var columns = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var defenses = SheetDefenses.None;
        var conSave = 0;
        IReadOnlyList<string> shadowed = [];
        var extra = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (key == Facts)
            {
                if (value is not JsonObject facts)
                {
                    throw Bad(column, "\"facts\" is not an object");
                }

                if (OptObject(facts, "defenses", column, "facts") is { } d)
                {
                    try
                    {
                        defenses = SheetJson.ReadDefenses(Serialize(d));
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException($"combatant column {column}: facts \"defenses\" is not a defences object.", ex);
                    }
                }

                conSave = OptInt(facts, "con_save", column, "facts") ?? 0;
                shadowed = Strings(facts, "shadowed", column, "facts");
                foreach (var (factKey, factValue) in facts)
                {
                    if (!FactKeys.Contains(factKey))
                    {
                        extra[factKey] = factValue?.DeepClone();
                    }
                }

                continue;
            }

            if (!SheetSnapshot.Captured.Contains(key))
            {
                continue;
            }

            columns[key] = value switch
            {
                null => null,
                JsonObject or JsonArray => Serialize(value),
                JsonValue v when v.TryGetValue(out long l) => l,
                JsonValue v when v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < long.MaxValue => (long)d,
                JsonValue v when v.TryGetValue(out string? s) => s,
                _ => throw Bad(column, $"\"{key}\" is not a stored column value"),
            };
        }

        return new SheetSnapshot
        {
            Columns = columns,
            Defenses = defenses,
            ConSaveBonus = conSave,
            Shadowed = shadowed,
            Extra = extra.Count == 0 ? null : Serialize(extra),
        };
    }

    /// <summary>
    /// The snapshot's text: each captured column in <see cref="SheetSnapshot.Captured"/> order (JSON columns nested as
    /// JSON, integers as numbers, NULL as null), then <c>"facts"</c> (its <c>"shadowed"</c> names only when a re-seed in a
    /// running fight left some, so every other snapshot keeps the text it always had). Null for no snapshot.
    /// </summary>
    public static string? WriteSnapshot(SheetSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var o = new JsonObject();
        foreach (var column in SheetSnapshot.Captured)
        {
            if (!snapshot.Columns.TryGetValue(column, out var value))
            {
                continue;
            }

            o[column] = value switch
            {
                null => null,
                long l => l,
                int i => i,
                string s when SheetColumns.Json.Contains(column) => JsonNode.Parse(s),
                string s => s,
                _ => throw new ArgumentException($"Snapshot column {column} holds a {value.GetType().Name}, not a stored value.", nameof(snapshot)),
            };
        }

        var facts = new JsonObject { ["defenses"] = JsonNode.Parse(SheetJson.WriteDefenses(snapshot.Defenses)), ["con_save"] = snapshot.ConSaveBonus };
        if (snapshot.Shadowed.Count > 0)
        {
            facts["shadowed"] = new JsonArray([.. snapshot.Shadowed.Select(n => (JsonNode?)JsonValue.Create(n))]);
        }

        if (snapshot.Extra is { } extra && JsonNode.Parse(extra) is JsonObject more)
        {
            foreach (var (key, value) in more)
            {
                facts[key] = value?.DeepClone();
            }
        }

        o[Facts] = facts;
        return Serialize(o);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // turn rows

    /// <summary>A <c>turn</c> row's detail (<see cref="TurnRecord"/>) as text.</summary>
    public static string WriteTurn(TurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var o = new JsonObject();
        if (turn.Prev)
        {
            o["prev"] = true;
            o["exact"] = turn.Exact;
        }

        if (turn.Start)
        {
            o["start"] = true;
        }

        Put(o, "from", turn.From);
        Put(o, "to", turn.To);
        o["round_before"] = turn.RoundBefore;
        o["round"] = turn.Round;
        if (turn.Wrapped)
        {
            o["wrapped"] = true;
        }

        if (turn.Restore.Count > 0)
        {
            var restore = new JsonArray();
            foreach (var r in turn.Restore)
            {
                restore.Add(new JsonObject
                {
                    ["id"] = r.CombatantId,
                    ["conditions"] = JsonNode.Parse(WriteConditions(r.Conditions)),
                    ["concentration"] = WriteConcentration(r.Concentration) is { } conc ? JsonNode.Parse(conc) : null,
                    ["legendary"] = WriteLegendary(r.Legendary) is { } legendary ? JsonNode.Parse(legendary) : null,
                    ["reaction_used"] = r.ReactionUsed,
                    ["surprised"] = r.Surprised,
                });
            }

            o["restore"] = restore;
        }

        if (turn.Changes.Count > 0)
        {
            o["changes"] = new JsonArray(turn.Changes.Select(c => (JsonNode)c).ToArray());
        }

        return Serialize(o);
    }

    /// <summary>A <c>turn</c> row's detail, or null when the text is not one (another kind's detail, NULL, not JSON).</summary>
    public static TurnRecord? ReadTurn(string? json)
    {
        JsonObject? o;
        try
        {
            o = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (o is null || !o.ContainsKey("round_before") || !o.ContainsKey("round"))
        {
            return null;
        }

        try
        {
            const string column = "combat_log.detail";
            var restore = new List<TurnRestore>();
            if (o.TryGetPropertyValue("restore", out var r) && r is JsonArray array)
            {
                foreach (var node in array)
                {
                    if (node is not JsonObject item)
                    {
                        return null;
                    }

                    restore.Add(new TurnRestore(
                        ReqText(item, "id", column, "restore"),
                        ReadConditions(item["conditions"] is { } cs ? Serialize(cs) : null),
                        item["concentration"] is JsonObject conc ? ReadConcentration(Serialize(conc)) : null,
                        item["legendary"] is JsonObject legendary ? ReadLegendary(Serialize(legendary)) : null,
                        OptBool(item, "reaction_used", column, "restore") ?? false,
                        OptBool(item, "surprised", column, "restore") ?? false));
                }
            }

            return new TurnRecord
            {
                Prev = OptBool(o, "prev", column, string.Empty) ?? false,
                Exact = OptBool(o, "exact", column, string.Empty) ?? false,
                Start = OptBool(o, "start", column, string.Empty) ?? false,
                From = OptText(o, "from", column, string.Empty),
                To = OptText(o, "to", column, string.Empty),
                RoundBefore = ReqInt(o, "round_before", column, string.Empty),
                Round = ReqInt(o, "round", column, string.Empty),
                Wrapped = OptBool(o, "wrapped", column, string.Empty) ?? false,
                Restore = restore,
                Changes = Strings(o, "changes", column, string.Empty),
            };
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // details

    /// <summary>Compact text of a JSON node, as every column and detail here is written.</summary>
    public static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(WriteOptions);

    // ---------------------------------------------------------------------------------------------------------------
    // helpers

    private static object? Long(int? value) => value is { } v ? (long)v : null;

    private static string? Text(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value)
        ? value switch
        {
            null => null,
            string s => s,
            _ => throw new ArgumentException($"Combatant column {column} holds a {value.GetType().Name}, not text.", nameof(row)),
        }
        : null;

    private static string Required(IReadOnlyDictionary<string, object?> row, string column) =>
        Text(row, column) is { Length: > 0 } text ? text : throw new ArgumentException($"A combatant row needs its {column}.", nameof(row));

    private static int? Integer(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value)
        ? value switch
        {
            null => null,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            int i => i,
            bool b => b ? 1 : 0,
            long => throw new InvalidDataException($"combatant column {column} is beyond a whole number's range."),
            _ => throw new ArgumentException($"Combatant column {column} holds a {value.GetType().Name}, not an integer.", nameof(row)),
        }
        : null;

    private static double? Real(IReadOnlyDictionary<string, object?> row, string column) => row.TryGetValue(column, out var value)
        ? value switch
        {
            null => null,
            double d => d,
            long l => l,
            int i => i,
            _ => throw new ArgumentException($"Combatant column {column} holds a {value.GetType().Name}, not a number.", nameof(row)),
        }
        : null;

    private static JsonObject? ObjectColumn(string? json, string column)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw Bad(column, "is not JSON");
        }

        return node switch
        {
            null => null,
            JsonObject o => o,
            _ => throw Bad(column, "is not a JSON object"),
        };
    }

    private static JsonArray? ArrayColumn(string? json, string column)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw Bad(column, "is not JSON");
        }

        return node switch
        {
            null => null,
            JsonArray a => a,
            _ => throw Bad(column, "is not a JSON array"),
        };
    }

    private static InvalidDataException Bad(string column, string what) => new($"combatant column {column}: {what}.");

    private static string At(string where, string key) => where.Length == 0 ? $"\"{key}\"" : $"{where}'s \"{key}\"";

    private static string ReqText(JsonObject o, string key, string column, string where) =>
        OptText(o, key, column, where) is { Length: > 0 } text ? text : throw Bad(column, $"{At(where, key)} is missing");

    private static int ReqInt(JsonObject o, string key, string column, string where) =>
        OptInt(o, key, column, where) ?? throw Bad(column, $"{At(where, key)} is missing");

    private static string? OptText(JsonObject o, string key, string column, string where)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue(out string? s) ? s : throw Bad(column, $"{At(where, key)} is not text");
    }

    private static int? OptInt(JsonObject o, string key, string column, string where)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return IntValue(node) ?? throw Bad(column, $"{At(where, key)} is not a whole number");
    }

    private static int? IntValue(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue(out int i))
        {
            return i;
        }

        if (v.TryGetValue(out long l) && l is >= int.MinValue and <= int.MaxValue)
        {
            return (int)l;
        }

        if (v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }

        return null;
    }

    private static bool? OptBool(JsonObject o, string key, string column, string where)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue v && v.TryGetValue(out bool b) ? b : throw Bad(column, $"{At(where, key)} is not true or false");
    }

    private static JsonObject? OptObject(JsonObject o, string key, string column, string where)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node as JsonObject ?? throw Bad(column, $"{At(where, key)} is not an object");
    }

    private static IReadOnlyList<string> Strings(JsonObject o, string key, string column, string where)
    {
        if (!o.TryGetPropertyValue(key, out var node) || node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw Bad(column, $"{At(where, key)} is not an array");
        }

        return array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : throw Bad(column, $"{At(where, key)} holds a value that is not text")).ToList();
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static void Put(JsonObject o, string key, int? value)
    {
        if (value is { } v)
        {
            o[key] = v;
        }
    }

    private static void PutList(JsonObject o, string key, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            o[key] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        }
    }

    private static string? Unknown(JsonObject o, IReadOnlyCollection<string> known)
    {
        var extra = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (!known.Contains(key))
            {
                extra[key] = value?.DeepClone();
            }
        }

        return extra.Count == 0 ? null : Serialize(extra);
    }

    private static JsonObject WithExtra(JsonObject o, string? extra, IReadOnlyCollection<string> known)
    {
        if (extra is null || JsonNode.Parse(extra) is not JsonObject more)
        {
            return o;
        }

        foreach (var (key, value) in more)
        {
            if (!known.Contains(key) && !o.ContainsKey(key))
            {
                o[key] = value?.DeepClone();
            }
        }

        return o;
    }
}

/// <summary>
/// A <c>turn</c> combat_log row's detail (contract §6.3, §6.9): which turn ended and which began, the round before and
/// after, and the automatic changes the step made, with each touched combatant's fields as they were BEFORE it
/// (<see cref="Restore"/>), so a <c>prev</c> that follows it can put them back exactly (FIX A28a).
/// </summary>
/// <param name="Prev">A row written by <c>prev</c> (two consecutive <c>prev</c>s move the pointer only).</param>
/// <param name="Exact">A <c>prev</c> that reverted the automatic changes exactly.</param>
/// <param name="Start">The row that began round 1 (the first <c>initiative</c>): nothing before it to go back to.</param>
public sealed record TurnRecord
{
    public bool Prev { get; init; }

    public bool Exact { get; init; }

    public bool Start { get; init; }

    /// <summary>The combatant whose turn ended (null when round 1 began).</summary>
    public string? From { get; init; }

    /// <summary>The combatant whose turn began.</summary>
    public string? To { get; init; }

    public int RoundBefore { get; init; }

    public int Round { get; init; }

    /// <summary>The order wrapped (a new round began).</summary>
    public bool Wrapped { get; init; }

    /// <summary>Each combatant the automatic processing changed, as it was before.</summary>
    public IReadOnlyList<TurnRestore> Restore { get; init; } = [];

    /// <summary>The automatic changes as lines ("frightened (Mummy) ended on Lieutenant James Torch").</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];
}

/// <summary>A combatant's automatically-changed fields as they were before a <c>next</c>.</summary>
public sealed record TurnRestore(
    string CombatantId,
    IReadOnlyList<CombatCondition> Conditions,
    CombatConcentration? Concentration,
    LegendaryState? Legendary,
    bool ReactionUsed,
    bool Surprised);
