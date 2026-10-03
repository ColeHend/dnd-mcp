using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// Paging for the list readers: a <c>limit</c> (default <see cref="CampaignLimits.DefaultListLimit"/>, at most
/// <see cref="CampaignLimits.MaxListLimit"/>) and an opaque <c>cursor</c> that is an offset into the list AFTER the
/// perspective filter (contract §3.9).
///
/// <para>
/// <b>Why after the filter:</b> an offset into the raw rows would let a perspective count what it cannot see (a page of
/// 15 that holds 12 results says 3 were hidden). The cursor also carries a fingerprint of the request it came from
/// (campaign, query, filters, perspective, point in time), so a cursor pasted into a different search, or into the same
/// search of another campaign, is refused with an actionable message instead of silently skipping into an unrelated list.
/// Every caller puts the campaign id first among its parts. The fingerprint is a stable hash (FNV-1a),
/// not <c>string.GetHashCode</c>, which is randomised per process: a cursor must survive a server restart.
/// </para>
/// </summary>
internal static class ReadCursor
{
    private const string Prefix = "c1.";

    /// <summary>The page size to use, or a <see cref="DndInputException"/> for one out of range.</summary>
    public static int Limit(int? limit)
    {
        var value = limit ?? CampaignLimits.DefaultListLimit;
        if (value < 1 || value > CampaignLimits.MaxListLimit)
        {
            throw new DndInputException(
                $"limit is {value.ToString(CultureInfo.InvariantCulture)}; give 1 to {CampaignLimits.MaxListLimit} " +
                $"(default {CampaignLimits.DefaultListLimit}), and page on with the cursor a result returns.");
        }

        return value;
    }

    /// <summary>A cursor for the page starting at <paramref name="offset"/> of the request <paramref name="fingerprint"/> names.</summary>
    public static string Encode(int offset, string fingerprint)
    {
        var payload = offset.ToString(CultureInfo.InvariantCulture) + ":" + fingerprint;
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The offset a cursor stands for (0 for none).</summary>
    /// <exception cref="DndInputException">The cursor is malformed or came from a different request.</exception>
    public static int Decode(string? cursor, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        var text = cursor.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || !TryPayload(text[Prefix.Length..], out var payload))
        {
            throw Refused();
        }

        var colon = payload.IndexOf(':');
        if (colon <= 0 ||
            !int.TryParse(payload[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            !string.Equals(payload[(colon + 1)..], fingerprint, StringComparison.Ordinal))
        {
            throw Refused();
        }

        return offset;
    }

    /// <summary>A stable fingerprint of the parts of a request that decide which list a cursor pages through.</summary>
    public static string Fingerprint(params object?[] parts)
    {
        var text = string.Join("\u001f", parts.Select(p => p switch
        {
            null => "\u0000",
            IEnumerable<string> list => string.Join("\u001e", list),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>The next cursor, or null when the page reaches the end of the list.</summary>
    public static string? Next(int offset, int limit, int total, string fingerprint) =>
        offset + limit < total ? Encode(offset + limit, fingerprint) : null;

    private static bool TryPayload(string encoded, out string payload)
    {
        payload = string.Empty;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DndInputException Refused() =>
        new("cursor is not one this request returned (cursors belong to one campaign, query, filter set, perspective and " +
            "as_of_session). Repeat the call without cursor to start from the first page.");
}
