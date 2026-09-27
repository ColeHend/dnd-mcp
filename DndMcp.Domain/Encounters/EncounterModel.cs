using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// One line of an encounter: <see cref="Count"/> creatures sharing one stat block.
/// </summary>
/// <param name="Name">What the result calls them: the stat block's name, or the caller's label for a CR-only entry.</param>
/// <param name="StatBlock">
/// Identifies the stat block, so two lines with the same one count once toward the 2024 "number of stat blocks" advice
/// (an SRD ref, or a label for a monster given only by CR).
/// </param>
/// <param name="Xp">XP for one creature, as its stat block (or the CR table) gives it.</param>
/// <param name="Excluded">
/// 2014 only: left out of the monster count that picks the group multiplier, because its CR is "significantly below" the
/// others'. The DMG gives no number for "significantly", so the caller decides; the 2024 method has no such rule.
/// </param>
public sealed record EncounterMonster(string Name, string StatBlock, ChallengeRating ChallengeRating, int Xp, int Count, bool Excluded = false)
{
    public long TotalXp => (long)Xp * Count;
}

/// <summary>Difficulty labels. Wire values are string constants, not enums, per the repo convention.</summary>
public static class EncounterDifficulty
{
    /// <summary>2014: below the Easy threshold. Not a DMG term; the DMG's scale starts at Easy.</summary>
    public const string Trivial = "trivial";

    public const string Easy = "easy";
    public const string Medium = "medium";
    public const string Hard = "hard";
    public const string Deadly = "deadly";

    public const string Low = "low";
    public const string Moderate = "moderate";
    public const string High = "high";

    /// <summary>2024: more XP than the High budget. Not an SRD term; the SRD's difficulties stop at High.</summary>
    public const string BeyondHigh = "beyond-high";

    public static string Display(string difficulty) => difficulty switch
    {
        Trivial => "Trivial",
        Easy => "Easy",
        Medium => "Medium",
        Hard => "Hard",
        Deadly => "Deadly",
        Low => "Low",
        Moderate => "Moderate",
        High => "High",
        BeyondHigh => "Beyond High",
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Not a difficulty label."),
    };
}

/// <summary>
/// The limits on an encounter's inputs, and the checks both editions' assessments and the tool share. The messages go to
/// the model verbatim (<see cref="DndInputException"/>), so each names the bad value and what is accepted, and each
/// counts a monsters item the way the argument guard does ("monsters item 2"), so the model reads one vocabulary.
/// </summary>
public static class EncounterLimits
{
    /// <summary>Character levels, the rows of every per-level table in both editions.</summary>
    public const int MinLevel = 1;

    public const int MaxLevel = 20;

    /// <summary>Characters in a party. Far above any real table; it only bounds the input.</summary>
    public const int MaxCharacters = 50;

    /// <summary>Items in one encounter's monsters list; the 2024 advice already flags more than three stat blocks.</summary>
    public const int MaxMonsterEntries = 50;

    /// <summary>Creatures on one item (a horde of 1,000 zombies is the ceiling).</summary>
    public const int MaxCount = 1_000;

    /// <summary>
    /// The effective-level offset's bounds. Campaign skills budget at about +1 ("roughly printed level +1"); a larger
    /// offset in either direction stops meaning "the party is stronger than its level" and starts meaning another party.
    /// </summary>
    public const int MaxEffectiveLevelOffset = 10;

    public static void ValidateParty(IReadOnlyList<int> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            throw new DndInputException("party needs at least one character's level, e.g. [5, 5, 5, 5] for four level 5 characters.");
        }

        if (levels.Count > MaxCharacters)
        {
            throw new DndInputException($"party has {levels.Count} characters; at most {MaxCharacters} are accepted.");
        }

        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] is < MinLevel or > MaxLevel)
            {
                throw new DndInputException(
                    $"party: character {(i + 1).ToString(CultureInfo.InvariantCulture)}'s level is " +
                    $"{levels[i].ToString(CultureInfo.InvariantCulture)}; character levels are {MinLevel} to {MaxLevel}. Give one " +
                    "level per character, e.g. [5, 5, 4].");
            }
        }
    }

    /// <summary>The monsters list's length: at least one item, at most <see cref="MaxMonsterEntries"/>.</summary>
    /// <param name="example">A complete example call to end the "at least one" message with, or null for a bare list.</param>
    public static void ValidateMonsterCount(int items, string? example = null)
    {
        if (items == 0)
        {
            throw new DndInputException(
                "monsters needs at least one item. " + (example ?? "Example: [{\"name\": \"Ogre\", \"count\": 3}]."));
        }

        if (items > MaxMonsterEntries)
        {
            throw new DndInputException(
                $"monsters has {items.ToString(CultureInfo.InvariantCulture)} items; at most {MaxMonsterEntries} are accepted. Put " +
                "identical creatures on one item with count.");
        }
    }

    /// <summary>One item's count: 1 to <see cref="MaxCount"/>.</summary>
    /// <param name="position">1-based, as the argument guard counts items.</param>
    public static void ValidateCount(int position, int count)
    {
        if (count is < 1 or > MaxCount)
        {
            throw new DndInputException(
                $"monsters item {position.ToString(CultureInfo.InvariantCulture)}: count is {count.ToString(CultureInfo.InvariantCulture)}; " +
                $"it is 1 to {MaxCount.ToString("N0", CultureInfo.InvariantCulture)}.");
        }
    }

    public static void ValidateMonsters(IReadOnlyList<EncounterMonster> monsters)
    {
        ArgumentNullException.ThrowIfNull(monsters);
        ValidateMonsterCount(monsters.Count);
        for (var i = 0; i < monsters.Count; i++)
        {
            ValidateCount(i + 1, monsters[i].Count);
            if (monsters[i].Xp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monsters), monsters[i].Xp, "A monster's XP cannot be negative.");
            }
        }
    }

    public static void ValidateOffset(int offset)
    {
        if (offset is < -MaxEffectiveLevelOffset or > MaxEffectiveLevelOffset)
        {
            throw new DndInputException(
                $"effective_level_offset is {offset.ToString(CultureInfo.InvariantCulture)}; it is a whole number from " +
                $"-{MaxEffectiveLevelOffset} to +{MaxEffectiveLevelOffset}, e.g. 1 for a party that fights about a level above its own.");
        }
    }
}

/// <summary>
/// A party read at an effective level: each character's level plus an offset, kept within 1–20. A campaign that runs
/// its party about a level above its printed one ("budget against effective level, roughly printed level +1") asks both
/// "what does the book call this fight?" and "what is it for a party that plays like level N+1?".
/// </summary>
/// <param name="Levels">The effective level of each character, in the party's order.</param>
/// <param name="ClampedCount">How many characters the 1–20 range stopped short of level + offset.</param>
public sealed record EffectiveParty(IReadOnlyList<int> Levels, int Offset, int ClampedCount)
{
    public static EffectiveParty Of(IReadOnlyList<int> levels, int offset)
    {
        EncounterLimits.ValidateParty(levels);
        EncounterLimits.ValidateOffset(offset);

        var clamped = 0;
        var effective = new int[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            var raw = levels[i] + offset;
            effective[i] = Math.Clamp(raw, EncounterLimits.MinLevel, EncounterLimits.MaxLevel);
            if (effective[i] != raw)
            {
                clamped++;
            }
        }

        return new EffectiveParty(effective, offset, clamped);
    }
}

/// <summary>
/// Advice that goes with a result: what the rules say to watch for in an encounter like this one. <see cref="Code"/> is
/// a stable string for tests and callers; <see cref="Text"/> is written for the reader.
/// </summary>
public sealed record EncounterWarning(string Code, string Text);
