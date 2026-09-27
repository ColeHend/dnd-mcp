using System.Text.Json;
using DndMcp.Domain.Encounters;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// A monster document's Challenge Rating and XP, as its stat block gives them: what encounter maths needs from the SRD.
///
/// <para>
/// Read from the index's document (<see cref="SrdDocument.Json"/>), never from the vendored files, because srd.db carries
/// the curated corrections: upstream prints the 2014 Dretch at 25 XP and the 2024 Archmage at 8,000, where their stat
/// blocks say 50 and 8,400, and an encounter built from the vendored JSON would be judged on the wrong totals. Every
/// monster in both editions is read here by a test that also holds its XP to the CR table.
/// </para>
/// </summary>
/// <param name="XpInLair">
/// The 2024 stat block's "or N in lair" XP (29 legendary monsters, the next CR's XP); null when it gives none, as no 2014
/// stat block does.
/// </param>
public sealed record SrdMonsterChallenge(ChallengeRating ChallengeRating, int Xp, int? XpInLair)
{
    /// <exception cref="ArgumentException">The document is not a monster.</exception>
    /// <exception cref="InvalidDataException">Its CR or XP is missing or not a table value: damaged data, not input.</exception>
    public static SrdMonsterChallenge Read(SrdDocument monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        if (monster.Kind != SrdKinds.Monster)
        {
            throw new ArgumentException($"`{monster.Ref}` is a {monster.Kind}, not a monster.", nameof(monster));
        }

        var root = monster.Root;
        var cr = root.TryGetProperty("challenge_rating", out var crValue) && crValue.ValueKind == JsonValueKind.Number
            ? ChallengeRating.FromNumber(crValue.GetDouble())
            : null;
        if (cr is null)
        {
            throw new InvalidDataException($"`{monster.Ref}` has no Challenge Rating the CR tables define ({Raw(root, "challenge_rating")}).");
        }

        return new SrdMonsterChallenge(cr.Value, WholeXp(monster, root, "xp") ?? throw Missing(monster, "xp"), WholeXp(monster, root, "xp_in_lair"));
    }

    private static int? WholeXp(SrdDocument monster, JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var xp) && xp >= 0
            ? xp
            : throw new InvalidDataException($"`{monster.Ref}` has {property} {value.GetRawText()}, not a whole number of XP.");
    }

    private static InvalidDataException Missing(SrdDocument monster, string property) =>
        new($"`{monster.Ref}` has no {property}.");

    private static string Raw(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? value.GetRawText() : "missing";
}
