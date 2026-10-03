using DndMcp.Domain.Core;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Whose view a read takes: <c>author</c> (Cole: everything), <c>dm</c>, <c>table</c> (the players out of character),
/// <c>party</c> (the party in character), <c>public</c> (in-world common knowledge) or <c>character:&lt;handle&gt;</c>.
///
/// <para>
/// Every campaign read takes one, and every non-author view is filtered: rows the perspective cannot see are left out
/// without a trace (no "3 hidden" count, which would itself tell the table a secret exists), names are the ones that
/// perspective knows, and author-only text never appears. This is what lets the model write a song in Belmakor's voice
/// from a view that cannot contain "the Axiom Cage".
/// </para>
/// </summary>
public sealed record Perspective
{
    private Perspective(string kind, CampaignHandle? character)
    {
        Kind = kind;
        Character = character;
    }

    /// <summary>The author's view: everything, including secret text and author-only aliases. The default.</summary>
    public static Perspective Author { get; } = new(CampaignValues.PerspectiveKinds.Author, null);

    /// <summary>One of <see cref="CampaignValues.PerspectiveKinds"/>.</summary>
    public string Kind { get; }

    /// <summary>The character's handle when <see cref="Kind"/> is <c>character</c>; null otherwise. Not yet resolved.</summary>
    public CampaignHandle? Character { get; }

    public bool IsAuthor => Kind == CampaignValues.PerspectiveKinds.Author;

    /// <summary>The wire form: <c>party</c>, <c>character:belmakor</c>.</summary>
    public string Text => Character is null ? Kind : CampaignValues.PerspectiveKinds.CharacterPrefix + Character.Text;

    public override string ToString() => Text;

    /// <summary>A character perspective for an already-parsed handle.</summary>
    public static Perspective ForCharacter(CampaignHandle character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return new Perspective(CampaignValues.PerspectiveKinds.Character, character);
    }

    /// <summary>
    /// Parses a perspective. Null or blank is <see cref="Author"/>. The kind is matched forgivingly ("Party", "PUBLIC");
    /// <c>character</c> needs a handle after the colon (<c>character:belmakor</c>, <c>character:e:12</c>).
    /// </summary>
    /// <exception cref="DndInputException">Anything else, with every accepted form listed.</exception>
    public static Perspective Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Author;
        }

        var colon = trimmed.IndexOf(':');
        var head = colon < 0 ? trimmed : trimmed[..colon];
        if (!CampaignValues.PerspectiveKinds.Set.TryMatch(head, out var kind))
        {
            throw new DndInputException(
                $"perspective \"{Echo(trimmed)}\" is not one of {CampaignValues.PerspectiveKinds.List}.");
        }

        if (kind != CampaignValues.PerspectiveKinds.Character)
        {
            if (colon >= 0)
            {
                throw new DndInputException(
                    $"perspective \"{Echo(trimmed)}\": only \"character:<slug>\" takes a handle. Perspectives: " +
                    $"{CampaignValues.PerspectiveKinds.List}.");
            }

            return kind == CampaignValues.PerspectiveKinds.Author ? Author : new Perspective(kind, null);
        }

        var rest = colon < 0 ? string.Empty : trimmed[(colon + 1)..].Trim();
        if (rest.Length == 0)
        {
            throw new DndInputException(
                "perspective \"character\" needs the character after a colon, e.g. \"character:belmakor\".");
        }

        if (!CampaignHandle.TryParse(rest, out var handle, out var problem) ||
            handle is not (CampaignHandle.EntityBySlug or CampaignHandle.EntityBySeq or CampaignHandle.ByCode))
        {
            throw new DndInputException(
                $"perspective \"{Echo(trimmed)}\": the part after \"character:\" must name a character, e.g. " +
                $"\"character:belmakor\" or \"character:e:12\".{(problem.Length > 0 ? " " + problem : string.Empty)}");
        }

        if (handle is CampaignHandle.EntityBySlug { Kind: not null and not CampaignValues.Kinds.Character } bySlug)
        {
            throw new DndInputException(
                $"perspective \"{Echo(trimmed)}\": a perspective is a character, not a {bySlug.Kind}.");
        }

        return new Perspective(CampaignValues.PerspectiveKinds.Character,
            handle is CampaignHandle.EntityBySlug slug ? slug with { Kind = null } : handle);
    }

    private static string Echo(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
