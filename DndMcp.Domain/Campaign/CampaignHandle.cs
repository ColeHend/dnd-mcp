using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// A parsed reference to something in a campaign, as the tools accept it. Internal ids (UUIDv7) never appear in tool
/// input or output; these handles do.
///
/// <list type="bullet">
/// <item><c>character:iron-guts</c> (kind and slug) or <c>iron-guts</c> (slug alone; slugs are unique per campaign).</item>
/// <item><c>e:12</c>: an entity by its sequence number. It is what non-author perspectives are shown instead of
/// <c>kind:slug</c> when the name they know differs from the entity's own, because the slug spells the true name.</item>
/// <item><c>f:12</c>: a fact by its sequence number.</item>
/// <item><c>Q22</c>, <c>F36</c>, <c>F56a</c>: a register code (letters, digits, an optional one-letter suffix, kept
/// lower-case), on an entity or a fact.</item>
/// <item><c>session:12</c>, <c>session:live</c> (the session at the table now), <c>session:last</c> (the highest-numbered
/// played session).</item>
/// <item><c>one-piece/character:keras</c>: an entity of another campaign; only a <c>same_as</c> link takes one.</item>
/// </list>
///
/// <para>
/// Sequence numbers are AUTOINCREMENT columns, so <c>e:</c>/<c>f:</c> handles are never reused, even after an undo deletes
/// the row they named. Parsing is forgiving where it cannot change the meaning (surrounding spaces, the kind's spelling,
/// upper-case slugs, a name typed where a slug was expected is slugified); anything else is refused with every accepted
/// form listed, because a guessed handle that resolves to the wrong entity writes to the wrong place.
/// </para>
/// </summary>
public abstract partial record CampaignHandle
{
    /// <summary>For messages: every form a handle may take.</summary>
    public const string Forms =
        "kind:slug (\"character:iron-guts\"), a slug (\"iron-guts\"), e:<n>, f:<n>, a code (\"Q22\", \"F36\"), " +
        "session:<n>, session:live or session:last";

    private CampaignHandle()
    {
    }

    /// <summary>The canonical spelling, as tools print it.</summary>
    public abstract string Text { get; }

    /// <summary>
    /// <see cref="Text"/>. Sealed so the derived records do not synthesize their own ToString ("EntityBySlug { Text = …,
    /// Kind = … }"), which an interpolated handle in a message or a result would otherwise print.
    /// </summary>
    public sealed override string ToString() => Text;

    /// <summary>An entity by slug, optionally checked against a kind.</summary>
    public sealed record EntityBySlug(string? Kind, string Slug) : CampaignHandle
    {
        public override string Text => Kind is null ? Slug : $"{Kind}:{Slug}";
    }

    /// <summary>An entity by sequence number (<c>e:12</c>).</summary>
    public sealed record EntityBySeq(long Seq) : CampaignHandle
    {
        public override string Text => "e:" + Seq.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A fact by sequence number (<c>f:12</c>).</summary>
    public sealed record FactBySeq(long Seq) : CampaignHandle
    {
        public override string Text => "f:" + Seq.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An entity or a fact by register code (<c>Q22</c>, <c>F56a</c>).</summary>
    public sealed record ByCode(string Code) : CampaignHandle
    {
        public override string Text => Code;
    }

    /// <summary>A session by number (<c>session:12</c>).</summary>
    public sealed record SessionByNumber(int Number) : CampaignHandle
    {
        public override string Text => "session:" + Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The live session (<c>session:live</c>).</summary>
    public sealed record SessionLive : CampaignHandle
    {
        public override string Text => "session:live";
    }

    /// <summary>The highest-numbered played session (<c>session:last</c>).</summary>
    public sealed record SessionLast : CampaignHandle
    {
        public override string Text => "session:last";
    }

    /// <summary>An entity of another campaign (<c>one-piece/character:keras</c>).</summary>
    public sealed record CrossCampaign(string CampaignSlug, CampaignHandle Inner) : CampaignHandle
    {
        public override string Text => $"{CampaignSlug}/{Inner.Text}";
    }

    /// <summary>
    /// A register code: 1-4 letters, 1-6 digits, and at most one letter after (F56a). The letters are upper-cased when
    /// parsed ("q22" is Q22) and the suffix lower-cased ("F56A" is F56a): a code typed in capitals, suffix and all, is the
    /// same code (review C11), where it used to fall through to the slug "f56a", which names nothing.
    /// </summary>
    public static bool IsCode(string? text) => text is not null && CodePattern().IsMatch(text);

    /// <summary>The canonical spelling of a code: letters upper-case, suffix lower-case.</summary>
    public static string CanonicalCode(string code)
    {
        var match = CodePattern().Match(code.Trim());
        if (!match.Success)
        {
            throw new ArgumentException($"\"{code}\" is not a register code.", nameof(code));
        }

        return match.Groups["letters"].Value.ToUpperInvariant() + match.Groups["digits"].Value +
               match.Groups["suffix"].Value.ToLowerInvariant();
    }

    /// <summary>Parses a handle, or throws <see cref="DndInputException"/> naming the text and the accepted forms.</summary>
    public static CampaignHandle Parse(string? text)
    {
        if (TryParse(text, out var handle, out var problem))
        {
            return handle;
        }

        throw new DndInputException(problem);
    }

    /// <summary>Parses a handle; on failure <paramref name="problem"/> is a message for the model.</summary>
    public static bool TryParse(string? text, out CampaignHandle handle, out string problem)
    {
        handle = null!;
        problem = string.Empty;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            problem = $"A handle is required: {Forms}.";
            return false;
        }

        if (trimmed.Length > 200)
        {
            problem = $"\"{trimmed[..60]}…\" is too long for a handle: {Forms}.";
            return false;
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            var campaign = trimmed[..slash].Trim().ToLowerInvariant();
            if (!CampaignSlugs.IsValid(campaign))
            {
                problem = $"\"{trimmed}\": \"{campaign}\" is not a campaign slug. Another campaign's entity is written " +
                          "campaign-slug/kind:slug, e.g. \"one-piece/character:keras\".";
                return false;
            }

            if (!TryParse(trimmed[(slash + 1)..], out var inner, out problem))
            {
                return false;
            }

            if (inner is not (EntityBySlug or EntityBySeq or ByCode))
            {
                problem = $"\"{trimmed}\": only an entity of another campaign can be named, e.g. \"one-piece/character:keras\".";
                return false;
            }

            handle = new CrossCampaign(campaign, inner);
            return true;
        }

        var colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            if (IsCode(trimmed))
            {
                handle = new ByCode(CanonicalCode(trimmed));
                return true;
            }

            return TrySlug(null, trimmed, trimmed, out handle, out problem);
        }

        var prefix = trimmed[..colon].Trim();
        var rest = trimmed[(colon + 1)..].Trim();
        if (rest.Length == 0)
        {
            problem = $"\"{trimmed}\" has nothing after the colon: {Forms}.";
            return false;
        }

        switch (prefix.ToLowerInvariant())
        {
            case "e":
                return TrySeq(rest, trimmed, s => new EntityBySeq(s), out handle, out problem);
            case "f":
                return TrySeq(rest, trimmed, s => new FactBySeq(s), out handle, out problem);
        }

        if (!CampaignValues.Kinds.Set.TryMatch(prefix, out var kind))
        {
            problem = $"\"{trimmed}\": \"{prefix}\" is not a kind. Kinds: {CampaignValues.Kinds.Set.List}. Handles: {Forms}.";
            return false;
        }

        if (kind == CampaignValues.Kinds.Session)
        {
            switch (rest.ToLowerInvariant())
            {
                case "live":
                    handle = new SessionLive();
                    return true;
                case "last":
                    handle = new SessionLast();
                    return true;
            }

            if (rest.All(char.IsAsciiDigit))
            {
                if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 100_000)
                {
                    problem = $"\"{trimmed}\": the session number is too large.";
                    return false;
                }

                handle = new SessionByNumber(number);
                return true;
            }
        }

        return TrySlug(kind, rest, trimmed, out handle, out problem);
    }

    private static bool TrySeq(string digits, string whole, Func<long, CampaignHandle> make, out CampaignHandle handle, out string problem)
    {
        handle = null!;
        problem = string.Empty;
        if (!digits.All(char.IsAsciiDigit) ||
            !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 1)
        {
            problem = $"\"{whole}\": expected a positive whole number after the colon, e.g. \"{whole[..2]}12\".";
            return false;
        }

        handle = make(seq);
        return true;
    }

    private static bool TrySlug(string? kind, string text, string whole, out CampaignHandle handle, out string problem)
    {
        handle = null!;
        problem = string.Empty;
        var lowered = text.ToLowerInvariant();
        var slug = CampaignSlugs.IsValid(lowered) ? lowered : CampaignSlugs.From(text, string.Empty);
        if (slug.Length == 0)
        {
            problem = $"\"{whole}\" is not a handle: {Forms}.";
            return false;
        }

        handle = new EntityBySlug(kind, slug);
        return true;
    }

    [GeneratedRegex("^(?<letters>[A-Za-z]{1,4})(?<digits>[0-9]{1,6})(?<suffix>[A-Za-z]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
