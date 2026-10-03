using DndMcp.Formatting.Srd;

namespace DndMcp.Formatting;

/// <summary>
/// The one-line notes a rules, encounter or balance result carries when the active campaign, not the call, decided a
/// value (which rules edition, or the effective level offset), or would have but its settings could not be read. One
/// wording for every tool, so the model learns one sentence.
///
/// <para>
/// <b>Why a result must say so.</b> The default is invisible in the call: the same <c>{"name": "Fireball"}</c> answers
/// with the 2014 spell in a 2014 campaign and the 2024 one elsewhere, and the campaign can change between two calls
/// (<c>campaign use</c>, or another Claude session). Without the note a model that expected 2024 text would quote 2014
/// rules as 2024, or read an effective-level label as the book's. The note names the campaign by slug (never secret: the
/// user chose or created it) and is added only when the campaign actually supplied the value, so every result of a call
/// with no campaign active, or with the value given explicitly, stays byte-identical to what it was before campaigns
/// existed (the existing tool tests pin that).
/// </para>
/// </summary>
internal static class CampaignDefaultNotes
{
    /// <summary>"2014 rules: the active campaign's (belmakor) ruleset."</summary>
    public static string Edition(string edition, string campaignSlug) =>
        $"{edition} rules: the active campaign's ({campaignSlug}) ruleset.";

    /// <summary>
    /// "Effective level +1: the active campaign's (one-piece) effective_level_offset; pass effective_level_offset 0 for the
    /// book levels alone." The override is named because an effective-level reading changes how the result is labelled.
    /// </summary>
    public static string LevelOffset(int offset, string campaignSlug) =>
        $"Effective level {SrdMarkdownText.Signed(offset)}: the active campaign's ({campaignSlug}) effective_level_offset; " +
        "pass effective_level_offset 0 for the book levels alone.";

    /// <summary>
    /// "Could not read the active campaign's settings (campaigns.db at …); using 2024." when campaigns.db exists but could
    /// not be read, so the campaign could not decide what it would have. Without it a 2014 campaign's rules lookup answered
    /// with 2024 rules and the usual campaign note simply went missing. The file is named (the campaign tools say what is
    /// wrong with it); <paramref name="used"/> says what the result used instead ("2024", "2024 and no
    /// effective_level_offset").
    /// </summary>
    public static string Unreadable(string campaignsDbPath, string used) =>
        $"Could not read the active campaign's settings (campaigns.db at {campaignsDbPath}); using {used}.";
}
