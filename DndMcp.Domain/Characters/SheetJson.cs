using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// Reads and writes every JSON column of <c>character_sheet</c> in the shapes of contract §4, and a whole sheet to and from
/// its row's columns.
///
/// <para>
/// <b>Reads keep everything they can, and refuse what they cannot keep.</b> Rewriting a column must never lose what was
/// stored, because the next change to the sheet writes the column back whole (or, for the map columns, key by key):
/// </para>
/// <list type="bullet">
/// <item>Keys this version does not know (a later version's, the Phase 8 import's) are kept: in the entry's <c>Extra</c>
/// or, at the top of a map column, in <see cref="CharacterSheet.Extras"/>, and written back unchanged.</item>
/// <item>An entry this version cannot read (a class with no name or a level below 1; a condition, feat, feature, spell or
/// language with no name) is kept verbatim with its place (<see cref="CharacterSheet.Unreadable"/>) and written back
/// where it was. A concentration object that names no spell reads with an empty <see cref="SheetConcentration.Spell"/>
/// and is written back without one.</item>
/// <item>A value of the wrong JSON type (text that is not JSON; an array where the column holds an object; a level, a
/// count or a name of the wrong type; a fractional count) is REFUSED with an <see cref="InvalidDataException"/> naming
/// the column: read as a default, it would be written over the stored value by the next change. A value of the wrong CLR
/// type in the row (not text, an integer or null) is an <see cref="ArgumentException"/>: a host bug.</item>
/// </list>
/// <para>
/// The refusals name the column and the place ("item 2's \"level\"") but never quote stored text: a sheet is read for the
/// party's views too, and a refusal must not carry author text to them.
/// </para>
/// <para>
/// <b>Writes are deterministic.</b> The same sheet always writes the same text: known keys in a fixed order (abilities
/// str…cha; slot levels 1…9 then pact; hit dice largest die first; resources and lists in their own order), absent
/// optional keys left out, then the kept unknown members, compact with relaxed escaping (the text the Repository's
/// <c>CampaignLogJson</c> writes, so "Björn" stays readable in history). Change detection and the combat drift check
/// compare this text (structurally), so two writers of one value would log changes that never happened.
/// </para>
/// </summary>
public static class SheetJson
{
    /// <summary>The empty JSON object, the default of every object column.</summary>
    public const string EmptyObject = "{}";

    /// <summary>The empty JSON array, the default of every array column.</summary>
    public const string EmptyArray = "[]";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Whole sheets

    /// <summary>
    /// The sheet from a row's columns (column name → value as SQLite returns it: string, long, or null; int is accepted).
    /// Missing columns and NULL read as their defaults; <c>entity_id</c> is required.
    /// </summary>
    /// <exception cref="ArgumentException">There is no <c>entity_id</c>, or a column's value is not text, an integer or null (a host bug: a <c>JsonElement</c> from a snapshot must be turned into the stored text first).</exception>
    /// <exception cref="InvalidDataException">A stored value is of the wrong type for its column (see the class summary); the message names the column.</exception>
    public static CharacterSheet FromColumns(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entityId = Text(row, SheetColumns.EntityId);
        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("A sheet row needs its entity_id.", nameof(row));
        }

        var extras = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var (abilities, abilityExtra) = ReadAbilities(Text(row, SheetColumns.Abilities));
        var (hitDice, hitDiceExtra) = ReadHitDice(Text(row, SheetColumns.HitDice));
        var (slots, slotExtra) = ReadSpellSlots(Text(row, SheetColumns.SpellSlots));
        var (resources, resourceExtra) = ReadResources(Text(row, SheetColumns.Resources));
        AddExtra(extras, SheetColumns.Abilities, abilityExtra);
        AddExtra(extras, SheetColumns.HitDice, hitDiceExtra);
        AddExtra(extras, SheetColumns.SpellSlots, slotExtra);
        AddExtra(extras, SheetColumns.Resources, resourceExtra);

        var unreadable = new OrderedDictionary<string, IReadOnlyList<SheetRawItem>>(StringComparer.Ordinal);
        var classes = Keep(unreadable, SheetColumns.Classes, ClassItems(Text(row, SheetColumns.Classes)));
        var conditions = Keep(unreadable, SheetColumns.Conditions, ConditionItems(Text(row, SheetColumns.Conditions)));
        var feats = Keep(unreadable, SheetColumns.Feats, EntryItems(Text(row, SheetColumns.Feats), SheetColumns.Feats));
        var features = Keep(unreadable, SheetColumns.Features, EntryItems(Text(row, SheetColumns.Features), SheetColumns.Features));
        var spells = Keep(unreadable, SheetColumns.Spells, EntryItems(Text(row, SheetColumns.Spells), SheetColumns.Spells));
        var languages = Keep(unreadable, SheetColumns.Languages, EntryItems(Text(row, SheetColumns.Languages), SheetColumns.Languages));

        return new CharacterSheet
        {
            EntityId = entityId,
            Player = Text(row, SheetColumns.Player),
            Ruleset = Text(row, SheetColumns.Ruleset),
            Species = Text(row, SheetColumns.Species),
            Lineage = Text(row, SheetColumns.Lineage),
            Background = Text(row, SheetColumns.Background),
            Size = Text(row, SheetColumns.Size),
            Classes = classes,
            Level = Integer(row, SheetColumns.Level),
            Xp = Integer(row, SheetColumns.Xp),
            Abilities = abilities,
            Saves = ReadSaves(Text(row, SheetColumns.Saves)),
            Skills = ReadRawObject(Text(row, SheetColumns.Skills), SheetColumns.Skills),
            Ac = Integer(row, SheetColumns.Ac),
            MaxHp = Integer(row, SheetColumns.MaxHp),
            MaxHpReduction = Integer(row, SheetColumns.MaxHpReduction) ?? 0,
            Hp = Integer(row, SheetColumns.Hp),
            TempHp = Integer(row, SheetColumns.TempHp) ?? 0,
            Speed = Integer(row, SheetColumns.Speed),
            Movement = ReadRawObject(Text(row, SheetColumns.Movement), SheetColumns.Movement),
            Senses = ReadRawObject(Text(row, SheetColumns.Senses), SheetColumns.Senses),
            InitiativeBonus = Integer(row, SheetColumns.InitiativeBonus),
            PassivePerception = Integer(row, SheetColumns.PassivePerception),
            SpellSaveDc = Integer(row, SheetColumns.SpellSaveDc),
            SpellAttack = Integer(row, SheetColumns.SpellAttack),
            Defenses = ReadDefenses(Text(row, SheetColumns.Defenses)),
            HitDice = hitDice,
            SpellSlots = slots,
            Resources = resources,
            Conditions = conditions,
            Concentration = ReadConcentration(Text(row, SheetColumns.Concentration)),
            DeathSaves = ReadDeathSaves(Text(row, SheetColumns.DeathSaves)),
            Exhaustion = Integer(row, SheetColumns.Exhaustion) ?? 0,
            Inspiration = (Integer(row, SheetColumns.Inspiration) ?? 0) != 0,
            Feats = feats,
            Features = features,
            Spells = spells,
            Languages = languages,
            SimProfile = ReadSimProfile(Text(row, SheetColumns.SimProfile)),
            NotesMd = Text(row, SheetColumns.NotesMd) ?? string.Empty,
            SheetSource = Text(row, SheetColumns.SheetSource),
            CreatedAt = Text(row, SheetColumns.CreatedAt),
            UpdatedAt = Text(row, SheetColumns.UpdatedAt),
            Extras = extras,
            Unreadable = unreadable,
        };
    }

    /// <summary>Every column of the sheet as it is stored (<see cref="Column"/>), in table order: a row to insert.</summary>
    public static IReadOnlyDictionary<string, object?> ToColumns(CharacterSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var row = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in SheetColumns.All)
        {
            row[column] = Column(sheet, column);
        }

        return row;
    }

    /// <summary>
    /// One column's stored value: a string (text and JSON columns), a long (integer columns; inspiration 0/1) or null.
    /// What the Repository writes, what <see cref="SheetDiff"/> compares, and what a combatant's <c>sheet_snapshot</c> holds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a <see cref="SheetColumns"/> name.</exception>
    public static object? Column(CharacterSheet sheet, string column)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return column switch
        {
            SheetColumns.EntityId => sheet.EntityId,
            SheetColumns.Player => sheet.Player,
            SheetColumns.Ruleset => sheet.Ruleset,
            SheetColumns.Species => sheet.Species,
            SheetColumns.Lineage => sheet.Lineage,
            SheetColumns.Background => sheet.Background,
            SheetColumns.Size => sheet.Size,
            SheetColumns.Classes => WriteClasses(sheet.Classes, UnreadableOf(sheet, SheetColumns.Classes)),
            SheetColumns.Level => Long(sheet.Level),
            SheetColumns.Xp => Long(sheet.Xp),
            SheetColumns.Abilities => WriteAbilities(sheet.Abilities, ExtraOf(sheet, SheetColumns.Abilities)),
            SheetColumns.Saves => WriteSaves(sheet.Saves),
            SheetColumns.Skills => sheet.Skills,
            SheetColumns.Ac => Long(sheet.Ac),
            SheetColumns.MaxHp => Long(sheet.MaxHp),
            SheetColumns.MaxHpReduction => (long)sheet.MaxHpReduction,
            SheetColumns.Hp => Long(sheet.Hp),
            SheetColumns.TempHp => (long)sheet.TempHp,
            SheetColumns.Speed => Long(sheet.Speed),
            SheetColumns.Movement => sheet.Movement,
            SheetColumns.Senses => sheet.Senses,
            SheetColumns.InitiativeBonus => Long(sheet.InitiativeBonus),
            SheetColumns.PassivePerception => Long(sheet.PassivePerception),
            SheetColumns.SpellSaveDc => Long(sheet.SpellSaveDc),
            SheetColumns.SpellAttack => Long(sheet.SpellAttack),
            SheetColumns.Defenses => WriteDefenses(sheet.Defenses),
            SheetColumns.HitDice => WriteHitDice(sheet.HitDice, ExtraOf(sheet, SheetColumns.HitDice)),
            SheetColumns.SpellSlots => WriteSpellSlots(sheet.SpellSlots, ExtraOf(sheet, SheetColumns.SpellSlots)),
            SheetColumns.Resources => WriteResources(sheet.Resources, ExtraOf(sheet, SheetColumns.Resources)),
            SheetColumns.Conditions => WriteConditions(sheet.Conditions, UnreadableOf(sheet, SheetColumns.Conditions)),
            SheetColumns.Concentration => WriteConcentration(sheet.Concentration),
            SheetColumns.DeathSaves => WriteDeathSaves(sheet.DeathSaves),
            SheetColumns.Exhaustion => (long)sheet.Exhaustion,
            SheetColumns.Inspiration => sheet.Inspiration ? 1L : 0L,
            SheetColumns.Feats => WriteEntries(sheet.Feats, UnreadableOf(sheet, SheetColumns.Feats)),
            SheetColumns.Features => WriteEntries(sheet.Features, UnreadableOf(sheet, SheetColumns.Features)),
            SheetColumns.Spells => WriteEntries(sheet.Spells, UnreadableOf(sheet, SheetColumns.Spells)),
            SheetColumns.Languages => WriteEntries(sheet.Languages, UnreadableOf(sheet, SheetColumns.Languages)),
            SheetColumns.SimProfile => sheet.SimProfile,
            SheetColumns.NotesMd => sheet.NotesMd,
            SheetColumns.SheetSource => sheet.SheetSource,
            SheetColumns.CreatedAt => sheet.CreatedAt,
            SheetColumns.UpdatedAt => sheet.UpdatedAt,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a character_sheet column."),
        };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // classes

    /// <summary>
    /// <c>[{"class":"wizard","subclass":"bladesinger","level":12,"hit_die":6}]</c>: the classes this version can read (an
    /// item without a class name or a level of at least 1 is not one; <see cref="FromColumns"/> keeps it verbatim).
    /// </summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static IReadOnlyList<SheetClass> ReadClasses(string? json) => ClassItems(json).Items;

    /// <summary>The classes, with the unreadable items written back where they were.</summary>
    public static string WriteClasses(IReadOnlyList<SheetClass> classes, IReadOnlyList<SheetRawItem>? unreadable = null)
    {
        var array = new JsonArray();
        foreach (var c in classes)
        {
            var o = new JsonObject { ["class"] = c.Class };
            Put(o, "subclass", c.Subclass);
            o["level"] = c.Level;
            Put(o, "hit_die", c.HitDie);
            array.Add(WithExtra(o, c.Extra, ClassKeys));
        }

        return Serialize(WithUnreadable(array, unreadable));
    }

    private static (IReadOnlyList<SheetClass> Items, IReadOnlyList<SheetRawItem> Unreadable) ClassItems(string? json) =>
        Items(json, SheetColumns.Classes, (node, item) =>
        {
            var o = ItemObject(node, SheetColumns.Classes, item);
            var name = OptText(o, "class", SheetColumns.Classes, item);
            var subclass = OptText(o, "subclass", SheetColumns.Classes, item);
            var level = OptInt(o, "level", SheetColumns.Classes, item);
            var hitDie = OptInt(o, "hit_die", SheetColumns.Classes, item);
            return name is { Length: > 0 } && level is >= 1
                ? new SheetClass(name, subclass, level.Value, hitDie) { Extra = Unknown(o, ClassKeys) }
                : null;
        });

    // ---------------------------------------------------------------------------------------------------------------
    // abilities

    /// <summary><c>{"str":11,"dex":20,…}</c>: scores by ability key; any other member (or a score that is not a whole number) is returned as the column's extra.</summary>
    /// <exception cref="InvalidDataException">The column is not a JSON object.</exception>
    public static (IReadOnlyDictionary<string, int> Scores, string? Extra) ReadAbilities(string? json)
    {
        var scores = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        var extra = new JsonObject();
        foreach (var (name, value) in Members(json, SheetColumns.Abilities))
        {
            if (V.Abilities.All.Contains(name) && Int(value) is { } score)
            {
                scores[name] = score;
            }
            else
            {
                extra[name] = value?.DeepClone();
            }
        }

        return (Ordered(scores, V.Abilities.All), ExtraText(extra));
    }

    public static string WriteAbilities(IReadOnlyDictionary<string, int> scores, string? extra = null)
    {
        var o = new JsonObject();
        foreach (var ability in V.Abilities.All)
        {
            if (scores.TryGetValue(ability, out var score))
            {
                o[ability] = score;
            }
        }

        return Serialize(WithExtra(o, extra, V.Abilities.All));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // saves, defenses, death saves, concentration

    /// <summary>
    /// <c>{"proficient":["int","wis"],"bonus":{"con":1}}</c>. Both are read as stored (a later version's word in
    /// <c>proficient</c>, a bonus under another key): they are written back as they were, and the users of
    /// <see cref="SheetSaves.Proficient"/> take the ability keys from it.
    /// </summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static SheetSaves ReadSaves(string? json)
    {
        if (ObjectColumn(json, SheetColumns.Saves) is not { } o)
        {
            return SheetSaves.None;
        }

        var proficient = OptStrings(o, "proficient", SheetColumns.Saves, string.Empty) ?? [];
        var bonus = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        if (OptObject(o, "bonus", SheetColumns.Saves, string.Empty) is { } bonusObject)
        {
            foreach (var (name, value) in bonusObject)
            {
                if (value is not null)
                {
                    bonus[name] = IntValue(value, SheetColumns.Saves, "a \"bonus\" value");
                }
            }
        }

        return new SheetSaves(proficient, Ordered(bonus, V.Abilities.All)) { Extra = Unknown(o, SaveKeys) };
    }

    public static string WriteSaves(SheetSaves saves)
    {
        var o = new JsonObject();
        if (saves.Proficient.Count > 0)
        {
            o["proficient"] = StringArray(saves.Proficient);
        }

        if (saves.Bonus.Count > 0)
        {
            var bonus = new JsonObject();
            foreach (var (ability, amount) in Ordered(new OrderedDictionary<string, int>(saves.Bonus, StringComparer.Ordinal), V.Abilities.All))
            {
                bonus[ability] = amount;
            }

            o["bonus"] = bonus;
        }

        return Serialize(WithExtra(o, saves.Extra, SaveKeys));
    }

    /// <summary><c>{"resist":[…],"immune":[…],"vulnerable":[…],"condition_immune":[…]}</c>, each list as stored.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static SheetDefenses ReadDefenses(string? json)
    {
        var o = ObjectColumn(json, SheetColumns.Defenses);
        return o is null
            ? SheetDefenses.None
            : new SheetDefenses(
                OptStrings(o, "resist", SheetColumns.Defenses, string.Empty) ?? [],
                OptStrings(o, "immune", SheetColumns.Defenses, string.Empty) ?? [],
                OptStrings(o, "vulnerable", SheetColumns.Defenses, string.Empty) ?? [],
                OptStrings(o, "condition_immune", SheetColumns.Defenses, string.Empty) ?? [])
            {
                Extra = Unknown(o, DefenseKeys),
            };
    }

    /// <summary>Empty lists are left out, so a sheet with no defences writes <c>{}</c> (the column default).</summary>
    public static string WriteDefenses(SheetDefenses defenses)
    {
        var o = new JsonObject();
        PutList(o, "resist", defenses.Resist);
        PutList(o, "immune", defenses.Immune);
        PutList(o, "vulnerable", defenses.Vulnerable);
        PutList(o, "condition_immune", defenses.ConditionImmune);
        return Serialize(WithExtra(o, defenses.Extra, DefenseKeys));
    }

    /// <summary><c>{"successes":0,"failures":0,"stable":false}</c>; a missing key reads as the reset state's.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type (a dead character must never read as alive).</exception>
    public static SheetDeathSaves ReadDeathSaves(string? json)
    {
        var o = ObjectColumn(json, SheetColumns.DeathSaves);
        return o is null
            ? SheetDeathSaves.Reset
            : new SheetDeathSaves(
                OptInt(o, "successes", SheetColumns.DeathSaves, string.Empty) ?? 0,
                OptInt(o, "failures", SheetColumns.DeathSaves, string.Empty) ?? 0,
                OptBool(o, "stable", SheetColumns.DeathSaves, string.Empty) ?? false)
            {
                Extra = Unknown(o, DeathSaveKeys),
            };
    }

    /// <summary>All three keys, always (the column default's shape).</summary>
    public static string WriteDeathSaves(SheetDeathSaves saves)
    {
        var o = new JsonObject { ["successes"] = saves.Successes, ["failures"] = saves.Failures, ["stable"] = saves.Stable };
        return Serialize(WithExtra(o, saves.Extra, DeathSaveKeys));
    }

    /// <summary>
    /// <c>{"spell":"Circle of Power","level":5,"remaining_rounds":98,"note":"…"}</c>, or null (the column is NULL). An object
    /// that names no spell is still a concentration: it reads with an empty spell and is written back as it was.
    /// </summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static SheetConcentration? ReadConcentration(string? json)
    {
        if (ObjectColumn(json, SheetColumns.Concentration) is not { } o)
        {
            return null;
        }

        return new SheetConcentration(
            OptText(o, "spell", SheetColumns.Concentration, string.Empty) ?? string.Empty,
            OptInt(o, "level", SheetColumns.Concentration, string.Empty),
            OptInt(o, "remaining_rounds", SheetColumns.Concentration, string.Empty),
            OptText(o, "note", SheetColumns.Concentration, string.Empty))
        {
            Extra = Unknown(o, ConcentrationKeys),
        };
    }

    /// <summary>The object's text, or null (the column is NULL when nothing is held). An empty spell is left out.</summary>
    public static string? WriteConcentration(SheetConcentration? concentration)
    {
        if (concentration is null)
        {
            return null;
        }

        var o = new JsonObject();
        if (concentration.Spell.Length > 0)
        {
            o["spell"] = concentration.Spell;
        }

        Put(o, "level", concentration.Level);
        Put(o, "remaining_rounds", concentration.RemainingRounds);
        Put(o, "note", concentration.Note);
        return Serialize(WithExtra(o, concentration.Extra, ConcentrationKeys));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // hit dice, spell slots, resources (maps logged per key)

    /// <summary><c>{"d6":{"max":12,"used":0}}</c>; a member that is not an object is returned as the column's extra.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static (IReadOnlyDictionary<string, HitDiceEntry> Dice, string? Extra) ReadHitDice(string? json)
    {
        var dice = new OrderedDictionary<string, HitDiceEntry>(StringComparer.Ordinal);
        var extra = new JsonObject();
        foreach (var (name, value) in Members(json, SheetColumns.HitDice))
        {
            if (value is JsonObject o)
            {
                dice[name] = new HitDiceEntry(
                    OptInt(o, "max", SheetColumns.HitDice, AnEntry) ?? 0,
                    OptInt(o, "used", SheetColumns.HitDice, AnEntry) ?? 0)
                {
                    Extra = Unknown(o, HitDiceKeys),
                };
            }
            else
            {
                extra[name] = value?.DeepClone();
            }
        }

        return (Ordered(dice, HitDiceOrder(dice.Keys)), ExtraText(extra));
    }

    /// <summary>Largest die first.</summary>
    public static string WriteHitDice(IReadOnlyDictionary<string, HitDiceEntry> dice, string? extra = null)
    {
        var o = new JsonObject();
        foreach (var key in HitDiceOrder(dice.Keys))
        {
            var entry = dice[key];
            o[key] = WithExtra(new JsonObject { ["max"] = entry.Max, ["used"] = entry.Used }, entry.Extra, HitDiceKeys);
        }

        return Serialize(WithExtra(o, extra, dice.Keys));
    }

    /// <summary>"d6" → 6; null when the key is not "d" and a number.</summary>
    public static int? DieOf(string key) =>
        key.Length > 1 && key[0] == 'd' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var sides) ? sides : null;

    /// <summary>6 → "d6".</summary>
    public static string DieKey(int sides) => "d" + sides.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>{"1":{"max":4,"used":1},"pact":{"level":3,"max":2,"used":1}}</c>; a member that is not an object is the column's extra.</summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static (IReadOnlyDictionary<string, SpellSlotEntry> Slots, string? Extra) ReadSpellSlots(string? json)
    {
        var slots = new OrderedDictionary<string, SpellSlotEntry>(StringComparer.Ordinal);
        var extra = new JsonObject();
        foreach (var (name, value) in Members(json, SheetColumns.SpellSlots))
        {
            if (value is JsonObject o)
            {
                slots[name] = new SpellSlotEntry(
                    OptInt(o, "max", SheetColumns.SpellSlots, AnEntry) ?? 0,
                    OptInt(o, "used", SheetColumns.SpellSlots, AnEntry) ?? 0,
                    OptInt(o, "level", SheetColumns.SpellSlots, AnEntry))
                {
                    Extra = Unknown(o, SlotKeys),
                };
            }
            else
            {
                extra[name] = value?.DeepClone();
            }
        }

        return (Ordered(slots, SlotOrder(slots.Keys)), ExtraText(extra));
    }

    /// <summary>Spell levels 1…9, then pact, then any other key.</summary>
    public static string WriteSpellSlots(IReadOnlyDictionary<string, SpellSlotEntry> slots, string? extra = null)
    {
        var o = new JsonObject();
        foreach (var key in SlotOrder(slots.Keys))
        {
            var entry = slots[key];
            var item = new JsonObject();
            Put(item, "level", entry.Level);
            item["max"] = entry.Max;
            item["used"] = entry.Used;
            o[key] = WithExtra(item, entry.Extra, SlotKeys);
        }

        return Serialize(WithExtra(o, extra, slots.Keys));
    }

    /// <summary>
    /// <c>{"bladesong":{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest"},"contingency":{"name":"Contingency","state":"set"}}</c>;
    /// a resource without a name takes its key as the name.
    /// </summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static (IReadOnlyDictionary<string, SheetResource> Resources, string? Extra) ReadResources(string? json)
    {
        var resources = new OrderedDictionary<string, SheetResource>(StringComparer.Ordinal);
        var extra = new JsonObject();
        foreach (var (name, value) in Members(json, SheetColumns.Resources))
        {
            if (value is JsonObject o)
            {
                resources[name] = new SheetResource(
                    OptText(o, "name", SheetColumns.Resources, AnEntry) is { Length: > 0 } display ? display : name,
                    OptInt(o, "max", SheetColumns.Resources, AnEntry),
                    OptInt(o, "used", SheetColumns.Resources, AnEntry),
                    OptText(o, "recharge", SheetColumns.Resources, AnEntry),
                    OptText(o, "state", SheetColumns.Resources, AnEntry),
                    OptText(o, "note", SheetColumns.Resources, AnEntry))
                {
                    Extra = Unknown(o, ResourceKeys),
                };
            }
            else
            {
                extra[name] = value?.DeepClone();
            }
        }

        return (resources, ExtraText(extra));
    }

    /// <summary>
    /// In the map's order; a counted resource writes max and used (0 when absent), a tracker its state (and a <c>used</c>
    /// it was stored with, kept as it was).
    /// </summary>
    public static string WriteResources(IReadOnlyDictionary<string, SheetResource> resources, string? extra = null)
    {
        var o = new JsonObject();
        foreach (var (key, resource) in resources)
        {
            var item = new JsonObject { ["name"] = resource.Name };
            if (resource.Max is { } max)
            {
                item["max"] = max;
                item["used"] = resource.Used ?? 0;
            }
            else
            {
                Put(item, "used", resource.Used);
            }

            Put(item, "recharge", resource.Recharge);
            Put(item, "state", resource.State);
            Put(item, "note", resource.Note);
            o[key] = WithExtra(item, resource.Extra, ResourceKeys);
        }

        return Serialize(WithExtra(o, extra, resources.Keys));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // conditions and lists

    /// <summary>
    /// <c>[{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"…"}]</c>; a string item reads
    /// as a condition of that name. An item with no name is not one (<see cref="FromColumns"/> keeps it verbatim).
    /// </summary>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static IReadOnlyList<SheetCondition> ReadConditions(string? json) => ConditionItems(json).Items;

    /// <summary>The conditions, with the unreadable items written back where they were.</summary>
    public static string WriteConditions(IReadOnlyList<SheetCondition> conditions, IReadOnlyList<SheetRawItem>? unreadable = null)
    {
        var array = new JsonArray();
        foreach (var c in conditions)
        {
            var o = new JsonObject { ["name"] = c.Name };
            Put(o, "source", c.Source);
            if (c.Effect is not null && AsObject(c.Effect) is { } effect)
            {
                o["effect"] = effect;
            }

            Put(o, "duration", c.Duration);
            Put(o, "remaining_rounds", c.RemainingRounds);
            Put(o, "note", c.Note);
            array.Add(WithExtra(o, c.Extra, ConditionKeys));
        }

        return Serialize(WithUnreadable(array, unreadable));
    }

    private static (IReadOnlyList<SheetCondition> Items, IReadOnlyList<SheetRawItem> Unreadable) ConditionItems(string? json) =>
        Items(json, SheetColumns.Conditions, (node, item) =>
        {
            if (TextValue(node) is { } plain)
            {
                return plain.Length > 0 ? new SheetCondition(plain, null, null, null, null) : null;
            }

            var o = ItemObject(node, SheetColumns.Conditions, item);
            var name = OptText(o, "name", SheetColumns.Conditions, item);
            var source = OptText(o, "source", SheetColumns.Conditions, item);
            var duration = OptText(o, "duration", SheetColumns.Conditions, item);
            var remaining = OptInt(o, "remaining_rounds", SheetColumns.Conditions, item);
            var note = OptText(o, "note", SheetColumns.Conditions, item);
            var effect = OptObject(o, "effect", SheetColumns.Conditions, item);
            return name is { Length: > 0 }
                ? new SheetCondition(name, source, duration, remaining, note, effect is null ? null : Serialize(effect)) { Extra = Unknown(o, ConditionKeys) }
                : null;
        });

    /// <summary>
    /// feats, features, spells, languages: <c>[{"name":"Tough"}]</c>; a string item reads as <c>{"name": s}</c>. An item
    /// with no name is not one (<see cref="FromColumns"/> keeps it verbatim).
    /// </summary>
    /// <param name="column">The column read (named in a refusal).</param>
    /// <exception cref="InvalidDataException">A value of the wrong JSON type.</exception>
    public static IReadOnlyList<SheetEntry> ReadEntries(string? json, string column = SheetColumns.Feats) => EntryItems(json, column).Items;

    /// <summary>The entries, with the unreadable items written back where they were.</summary>
    public static string WriteEntries(IReadOnlyList<SheetEntry> entries, IReadOnlyList<SheetRawItem>? unreadable = null)
    {
        var array = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject { ["name"] = e.Name };
            Put(o, "ref", e.Ref);
            Put(o, "source", e.Source);
            if (e.Prepared is { } prepared)
            {
                o["prepared"] = prepared;
            }

            array.Add(WithExtra(o, e.Extra, EntryKeys));
        }

        return Serialize(WithUnreadable(array, unreadable));
    }

    private static (IReadOnlyList<SheetEntry> Items, IReadOnlyList<SheetRawItem> Unreadable) EntryItems(string? json, string column) =>
        Items(json, column, (node, item) =>
        {
            if (TextValue(node) is { } plain)
            {
                return plain.Length > 0 ? new SheetEntry(plain) : null;
            }

            var o = ItemObject(node, column, item);
            var name = OptText(o, "name", column, item);
            var reference = OptText(o, "ref", column, item);
            var source = OptText(o, "source", column, item);
            var prepared = OptBool(o, "prepared", column, item);
            return name is { Length: > 0 } ? new SheetEntry(name, reference, source, prepared) { Extra = Unknown(o, EntryKeys) } : null;
        });

    // ---------------------------------------------------------------------------------------------------------------
    // raw columns

    /// <summary>An object column kept whole (skills, movement, senses): compact text, or <c>{}</c> when NULL.</summary>
    /// <param name="column">The column read (named in a refusal).</param>
    /// <exception cref="InvalidDataException">The text is not a JSON object.</exception>
    public static string ReadRawObject(string? json, string column = SheetColumns.Skills) =>
        ObjectColumn(json, column) is { } o ? Serialize(o) : EmptyObject;

    /// <summary>The stored sim_profile's compact text, or null when NULL (it is parsed only where it is used).</summary>
    /// <exception cref="InvalidDataException">The text is not a JSON object.</exception>
    public static string? ReadSimProfile(string? json) => ObjectColumn(json, SheetColumns.SimProfile) is { } o ? Serialize(o) : null;

    /// <summary>Compact text of a JSON node, as every column here is written.</summary>
    public static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(WriteOptions);

    /// <summary>The text as a JSON object, or null when it is not one (tolerant: for comparing, never for reading a column).</summary>
    public static JsonObject? AsObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // helpers

    private const string AnEntry = "an entry";

    private static readonly string[] ClassKeys = ["class", "subclass", "level", "hit_die"];
    private static readonly string[] SaveKeys = ["proficient", "bonus"];
    private static readonly string[] DefenseKeys = ["resist", "immune", "vulnerable", "condition_immune"];
    private static readonly string[] DeathSaveKeys = ["successes", "failures", "stable"];
    private static readonly string[] ConcentrationKeys = ["spell", "level", "remaining_rounds", "note"];
    private static readonly string[] HitDiceKeys = ["max", "used"];
    private static readonly string[] SlotKeys = ["level", "max", "used"];
    private static readonly string[] ResourceKeys = ["name", "max", "used", "recharge", "state", "note"];
    private static readonly string[] ConditionKeys = ["name", "source", "effect", "duration", "remaining_rounds", "note"];
    private static readonly string[] EntryKeys = ["name", "ref", "source", "prepared"];

    private static string? ExtraOf(CharacterSheet sheet, string column) => sheet.Extras.TryGetValue(column, out var text) ? text : null;

    private static IReadOnlyList<SheetRawItem>? UnreadableOf(CharacterSheet sheet, string column) =>
        sheet.Unreadable.TryGetValue(column, out var items) ? items : null;

    private static void AddExtra(OrderedDictionary<string, string> extras, string column, string? extra)
    {
        if (extra is not null)
        {
            extras[column] = extra;
        }
    }

    private static IReadOnlyList<T> Keep<T>(
        OrderedDictionary<string, IReadOnlyList<SheetRawItem>> unreadable, string column, (IReadOnlyList<T> Items, IReadOnlyList<SheetRawItem> Unreadable) read)
    {
        if (read.Unreadable.Count > 0)
        {
            unreadable[column] = read.Unreadable;
        }

        return read.Items;
    }

    /// <summary>
    /// The array column's items read one by one: an item <paramref name="read"/> returns null for (or a JSON null) is kept
    /// verbatim with its index; <paramref name="read"/> throws for a value of the wrong type.
    /// </summary>
    private static (IReadOnlyList<T> Items, IReadOnlyList<SheetRawItem> Unreadable) Items<T>(string? json, string column, Func<JsonNode, string, T?> read)
        where T : class
    {
        var items = new List<T>();
        var unreadable = new List<SheetRawItem>();
        if (ArrayColumn(json, column) is { } array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var node = array[i];
                if (node is not null && read(node, $"item {N(i + 1)}") is { } item)
                {
                    items.Add(item);
                }
                else
                {
                    unreadable.Add(new SheetRawItem(i, Serialize(node)));
                }
            }
        }

        return (items, unreadable);
    }

    /// <summary>The array with the kept unreadable items put back at their indexes (clamped to the array as it is now).</summary>
    private static JsonArray WithUnreadable(JsonArray array, IReadOnlyList<SheetRawItem>? unreadable)
    {
        foreach (var item in (unreadable ?? []).OrderBy(i => i.Index))
        {
            array.Insert(Math.Clamp(item.Index, 0, array.Count), JsonNode.Parse(item.Json));
        }

        return array;
    }

    private static JsonNode? Parse(string? json, string column)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw Corrupt(column, "it is not JSON");
        }
    }

    /// <summary>The column's object, or null for NULL (or JSON null); anything else is refused.</summary>
    private static JsonObject? ObjectColumn(string? json, string column) => Parse(json, column) switch
    {
        null => null,
        JsonObject o => o,
        var node => throw Corrupt(column, $"it holds {Kind(node)}, not an object"),
    };

    private static JsonArray? ArrayColumn(string? json, string column) => Parse(json, column) switch
    {
        null => null,
        JsonArray a => a,
        var node => throw Corrupt(column, $"it holds {Kind(node)}, not an array"),
    };

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Members(string? json, string column) =>
        ObjectColumn(json, column) is { } o ? o.ToList() : [];

    private static JsonObject ItemObject(JsonNode node, string column, string item) =>
        node as JsonObject ?? throw Corrupt(column, $"{item} is {Kind(node)}, not an object");

    private static string? OptText(JsonObject o, string key, string column, string where) => o[key] switch
    {
        null => null,
        var node => TextValue(node) ?? throw Corrupt(column, $"{Place(where, key)} is {Kind(node)}, not text"),
    };

    private static int? OptInt(JsonObject o, string key, string column, string where) =>
        o[key] is { } node ? IntValue(node, column, Place(where, key)) : null;

    private static bool? OptBool(JsonObject o, string key, string column, string where) => o[key] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        var node => throw Corrupt(column, $"{Place(where, key)} is {Kind(node)}, not true or false"),
    };

    private static JsonObject? OptObject(JsonObject o, string key, string column, string where) => o[key] switch
    {
        null => null,
        JsonObject value => value,
        var node => throw Corrupt(column, $"{Place(where, key)} is {Kind(node)}, not an object"),
    };

    /// <summary>A list of text as stored (every item kept, none reworded).</summary>
    private static IReadOnlyList<string>? OptStrings(JsonObject o, string key, string column, string where)
    {
        switch (o[key])
        {
            case null:
                return null;
            case JsonArray array:
                var list = new List<string>();
                for (var i = 0; i < array.Count; i++)
                {
                    list.Add(TextValue(array[i]) ?? throw Corrupt(column, $"item {N(i + 1)} of {Place(where, key)} is {Kind(array[i])}, not text"));
                }

                return list;
            case var node:
                throw Corrupt(column, $"{Place(where, key)} is {Kind(node)}, not a list");
        }
    }

    private static int IntValue(JsonNode node, string column, string what) =>
        Int(node) ?? throw Corrupt(
            column,
            node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? $"{what} is not a whole number" : $"{what} is {Kind(node)}, not a whole number");

    private static string Place(string where, string key) => where.Length == 0 ? $"\"{key}\"" : $"{where}'s \"{key}\"";

    private static string? TextValue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "a list",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "text",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "true or false",
            _ => "a value",
        },
        _ => "a value",
    };

    /// <summary>A stored value that cannot be read without losing it. The message names the column and the place, never stored text.</summary>
    private static InvalidDataException Corrupt(string column, string problem) =>
        new($"The character sheet's {column} column is not readable: {problem}. It is refused rather than read as empty, " +
            "because the next change would write that over it.");

    /// <summary>The members of <paramref name="o"/> not in <paramref name="known"/>, as compact text, or null when none.</summary>
    private static string? Unknown(JsonObject o, IReadOnlyCollection<string> known)
    {
        var extra = new JsonObject();
        foreach (var (name, value) in o)
        {
            if (!known.Contains(name))
            {
                extra[name] = value?.DeepClone();
            }
        }

        return ExtraText(extra);
    }

    private static string? ExtraText(JsonObject extra) => extra.Count == 0 ? null : Serialize(extra);

    /// <summary>The object with the kept unknown members appended (a member whose name is now known is not re-added).</summary>
    private static JsonObject WithExtra(JsonObject o, string? extra, IEnumerable<string> known)
    {
        if (AsObject(extra) is not { } members)
        {
            return o;
        }

        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in members.ToList())
        {
            if (!knownSet.Contains(name) && !o.ContainsKey(name))
            {
                o[name] = value?.DeepClone();
            }
        }

        return o;
    }

    private static void Put(JsonObject o, string name, string? value)
    {
        if (value is not null)
        {
            o[name] = value;
        }
    }

    private static void Put(JsonObject o, string name, int? value)
    {
        if (value is not null)
        {
            o[name] = value.Value;
        }
    }

    private static void PutList(JsonObject o, string name, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            o[name] = StringArray(values);
        }
    }

    private static JsonArray StringArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>A JSON number that is a whole number within int's range (4 and 4.0 alike), else null.</summary>
    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Floor(d) == d && d is >= int.MinValue and <= int.MaxValue
            ? (int)d
            : null;
    }

    /// <summary>A text column's value: a string, or null for NULL or a missing column.</summary>
    /// <exception cref="InvalidDataException">The column holds an integer.</exception>
    /// <exception cref="ArgumentException">The value is not a stored type (a host bug).</exception>
    private static string? Text(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            long or int => throw Corrupt(column, "it holds a number, not text"),
            _ => throw WrongClrType(column, value),
        };
    }

    /// <summary>An integer column's value (long as SQLite returns it, int, or bool for 0/1), or null for NULL or a missing column.</summary>
    /// <exception cref="InvalidDataException">The column holds text, or a number beyond the integer range.</exception>
    /// <exception cref="ArgumentException">The value is not a stored type (a host bug).</exception>
    private static int? Integer(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            long => throw Corrupt(column, "it holds a number beyond the integer range"),
            int i => i,
            bool b => b ? 1 : 0,
            string => throw Corrupt(column, "it holds text, not an integer"),
            _ => throw WrongClrType(column, value),
        };
    }

    private static ArgumentException WrongClrType(string column, object value) =>
        new($"The {column} column was given a {value.GetType().Name}; FromColumns takes each column as SQLite stores it: " +
            "text, an integer (long or int) or null.", "row");

    private static long? Long(int? value) => value is null ? null : value.Value;

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A copy of the map with its keys in <paramref name="order"/>'s order first, then the rest in their own order.</summary>
    private static IReadOnlyDictionary<string, T> Ordered<T>(OrderedDictionary<string, T> map, IEnumerable<string> order)
    {
        var result = new OrderedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            if (map.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }

        foreach (var (key, value) in map)
        {
            result.TryAdd(key, value);
        }

        return result;
    }

    private static IReadOnlyList<string> HitDiceOrder(IEnumerable<string> keys) =>
        keys.OrderBy(k => DieOf(k) is null ? 1 : 0).ThenByDescending(k => DieOf(k) ?? 0).ThenBy(k => k, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> SlotOrder(IEnumerable<string> keys) =>
        keys.OrderBy(SlotRank).ThenBy(k => k, StringComparer.Ordinal).ToList();

    private static int SlotRank(string key) =>
        key == SpellSlotEntry.PactKey ? 10
        : key.Length == 1 && key[0] is >= '1' and <= '9' ? key[0] - '0'
        : 11;
}
