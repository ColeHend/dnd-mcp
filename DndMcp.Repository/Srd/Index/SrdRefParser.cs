using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// Parses a ref the model typed or copied: <c>kind/slug</c> (<c>spell/fireball</c>), <c>edition/kind/slug</c>
/// (<c>2024/spell/fireball</c>), or an upstream API URL (<c>/api/2014/spells/fireball</c>,
/// <c>/api/2014/classes/fighter/levels/5</c>, with or without the host), which is how the vendored records link to
/// each other and so how the raw JSON the model sees in <c>format: full</c> names things.
///
/// <para>
/// The kind goes through <see cref="SrdKindNames.Normalize(string?, string)"/>, so plurals and the race/species
/// synonyms work here too and an unknown kind gets the same list of valid kinds. The slug is lower-cased (every
/// upstream slug is lower case) but otherwise kept, because some real slugs contain characters a stricter rule would
/// reject (<c>stone-of-good-luck-(luckstone)</c>, <c>dragon-ancestor-black---acid-damage</c>). Surrounding backticks
/// and quotes are dropped: refs are printed as <c>`2024/spell/fireball`</c>, and a model copying one often keeps them.
/// </para>
/// <para>
/// Whether the text carried its own edition is reported, because the caller decides what an explicit edition that
/// disagrees with the tool's <c>edition</c> argument means.
/// </para>
/// </summary>
public static class SrdRefParser
{
    /// <summary>
    /// The longest ref accepted. Real refs are short (the longest, an API URL with its host, is about 100 characters), so
    /// anything this long is pasted text, not a ref: it is refused before it is parsed, echoed, or turned into a name to
    /// suggest from, each of which cost time and message length in proportion to the text (a 60,000-character "ref" used
    /// to come back twice, whole, in the error). Generous on purpose: an over-long slug below it still gets the ordinary
    /// "no such slug" answer from the tool, with the echo shortened there.
    /// </summary>
    public const int MaxLength = 1_000;

    private const string AcceptedForms =
        "Use kind/slug (spell/fireball), edition/kind/slug (2024/spell/fireball) or an API URL (/api/2024/spells/fireball).";

    /// <inheritdoc cref="Parse(string?, string, out bool)"/>
    public static SrdRef Parse(string? text, string defaultEdition) => Parse(text, defaultEdition, out _);

    /// <summary>
    /// The ref <paramref name="text"/> names; <paramref name="defaultEdition"/> applies when the text has no edition.
    /// <paramref name="editionGiven"/> is true when it did (an <c>edition/kind/slug</c> ref or an API URL).
    /// </summary>
    /// <exception cref="DndInputException">The text is not one of the accepted forms, or names an unknown edition or kind.</exception>
    public static SrdRef Parse(string? text, string defaultEdition, out bool editionGiven)
    {
        if (!SrdEdition.All.Contains(defaultEdition))
        {
            throw new ArgumentException($"defaultEdition must be one of {string.Join(", ", SrdEdition.All)}.", nameof(defaultEdition));
        }

        var trimmed = (text ?? string.Empty).Trim().Trim('`', '"', '\'').Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new DndInputException(
                $"The ref is {trimmed.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; refs are at most " +
                $"{MaxLength} characters, like 2024/spell/fireball. {AcceptedForms}");
        }

        if (trimmed.Length == 0)
        {
            throw new DndInputException($"The ref is empty. {AcceptedForms}");
        }

        var apiStart = trimmed.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (apiStart >= 0 || trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            editionGiven = true;
            return ParseApiUrl(trimmed, apiStart >= 0 ? trimmed[(apiStart + 1)..] : trimmed);
        }

        var parts = trimmed.Split('/');
        if (parts.Any(p => p.Trim().Length == 0))
        {
            throw NotARef(trimmed);
        }

        switch (parts.Length)
        {
            case 2 when SrdEdition.All.Contains(parts[0].Trim()):
                throw new DndInputException($"'{trimmed}' has an edition and a kind but no slug. {AcceptedForms}");

            case 2:
                editionGiven = false;
                return Build(defaultEdition, parts[0], parts[1]);

            case 3:
                var edition = parts[0].Trim();
                if (!SrdEdition.All.Contains(edition))
                {
                    throw new DndInputException(
                        $"Unknown edition '{edition}' in '{trimmed}'. Editions are 2014 and 2024, for example 2024/spell/fireball.");
                }

                editionGiven = true;
                return Build(edition, parts[1], parts[2]);

            default:
                throw NotARef(trimmed);
        }
    }

    // "api/{edition}/{resource}/{slug}" or "api/{edition}/{classes|subclasses}/{name}/levels/{n}", query and fragment dropped.
    private static SrdRef ParseApiUrl(string original, string path)
    {
        var end = path.IndexOfAny(['?', '#']);
        var segments = (end >= 0 ? path[..end] : path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !SrdEdition.All.Contains(segments[1]))
        {
            throw NotARef(original);
        }

        var edition = segments[1];
        if (segments.Length == 6 && segments[2].ToLowerInvariant() is "classes" or "subclasses" &&
            segments[4].Equals("levels", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return new SrdRef(edition, SrdKinds.Level, $"{segments[3].ToLowerInvariant()}-{level.ToString(CultureInfo.InvariantCulture)}");
        }

        if (segments.Length != 4)
        {
            throw NotARef(original);
        }

        return Build(edition, segments[2], segments[3]);
    }

    private static SrdRef Build(string edition, string kind, string slug) =>
        new(edition, SrdKindNames.Normalize(kind.Trim(), edition), slug.Trim().ToLowerInvariant());

    private static DndInputException NotARef(string text) => new($"'{text}' is not a rules ref. {AcceptedForms}");
}
