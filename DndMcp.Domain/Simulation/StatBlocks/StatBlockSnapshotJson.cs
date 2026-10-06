using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// The JSON of a whole <see cref="StatBlock"/> as the combat tracker snapshots it (<c>combatant.statblock</c>, contract
/// §4): written when a monster is added to a fight, read back for every damage, reminder, XP award and simulation of that
/// fight, so the fight keeps the stat block it started with even if the SRD data or the normalizer changes later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The options.</b> Snake_case names and nulls omitted, as every stored JSON in campaigns.db; relaxed escaping so a
/// trait's text stays readable (and shorter) in the database; read-only (computed) members such as
/// <see cref="MultiattackRoutine.TotalUses"/> left out, since a reader could not set them and they are recomputed; unknown
/// fields ignored on read, so a snapshot written by a later build still loads; and the two converters that make the
/// non-record members round-trip (<see cref="DamageFormulaJsonConverter"/>, <see cref="ChallengeRatingJsonConverter"/>).
/// <b>Not</b> <see cref="DslJson.Options"/>: those refuse unknown fields (right for user input, wrong for stored data)
/// and are the DSL's binding contract with the host.
/// </para>
/// <para>
/// <b>Corrupt text is refused on read, not later.</b> A member the model says is never null given as null
/// (<c>"hit_dice": null</c>, <c>"traits": null</c>; the nullable annotations are enforced) or a positional record's
/// never-null constructor argument left out is a <see cref="JsonException"/> here, instead of a stat block that throws a
/// <see cref="NullReferenceException"/> in the middle of a later damage step or simulation. A null ITEM inside a list is
/// not checked (the serializer does not read element annotations; <see cref="Serialize"/> never writes one).
/// </para>
/// <para>
/// <b>What breaks without it.</b> A formula without its converter cannot be read back at all; a Challenge Rating reads
/// back as CR 0 and the fight's XP is wrong. <c>StatBlockSnapshotTests</c> round-trips every SRD monster of both editions
/// field by field and pins the largest snapshot's size.
/// </para>
/// <para>
/// The snapshot stores the stat block as normalized: the in-lair counts (<see cref="StatBlock.XpInLair"/>,
/// <see cref="LegendaryActions.UsesInLair"/>, <see cref="StatBlock.LegendaryResistanceInLair"/>) stay alongside the plain
/// ones, and the encounter's <c>lair</c> picks (<c>StatBlockFacts</c>).
/// </para>
/// </remarks>
public static class StatBlockSnapshotJson
{
    /// <summary>The snapshot options (read-only).</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>The snapshot of <paramref name="block"/>: one JSON object.</summary>
    public static string Serialize(StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return JsonSerializer.Serialize(block, Options);
    }

    /// <summary>A stat block from its snapshot.</summary>
    /// <exception cref="JsonException">
    /// The text is not a stat block snapshot: malformed, a required member or never-null constructor argument missing, a
    /// member that is never null given as null, or the whole value null.
    /// </exception>
    public static StatBlock Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<StatBlock>(json, Options) ?? throw new JsonException("A stat block snapshot is a JSON object, not null.");
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IgnoreReadOnlyProperties = true,
            RespectNullableAnnotations = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireNonNullConstructorArguments } },
            WriteIndented = false,
        };
        options.Converters.Add(new DamageFormulaJsonConverter());
        options.Converters.Add(new ChallengeRatingJsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// A positional record's constructor argument of a never-null reference type with no default (a damage roll's
    /// <c>dice</c>, an adjustment's <c>text</c>) must be in the JSON: the serializer would otherwise pass null. Nullable ones
    /// stay optional, since <see cref="JsonIgnoreCondition.WhenWritingNull"/> leaves them out
    /// (<c>RespectRequiredConstructorParameters</c> would refuse those too).
    /// </summary>
    private static void RequireNonNullConstructorArguments(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (property.AssociatedParameter is { HasDefaultValue: false, IsNullable: false } parameter && !parameter.ParameterType.IsValueType)
            {
                property.IsRequired = true;
            }
        }
    }
}
