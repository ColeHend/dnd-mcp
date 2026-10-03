namespace DndMcp.Domain.Campaign;

/// <summary>
/// Every bound the campaign tools enforce, in one place so the validators, the tool descriptions and the tests quote the
/// same numbers.
///
/// <para>
/// <b>Why these bounds:</b> a tool result has to fit a model's context (Claude Code warns at 10,000 tokens and spills
/// anything over 25,000 to a file), and one malformed call must fail with a message rather than write a megabyte into
/// campaigns.db that every later read drags along. They are generous for real campaigns: a 50,000-character body holds a
/// long run-sheet, 50 ops hold a full session recap. Raising one is a decision about output size, so the formatters'
/// caps must be checked with it.
/// </para>
/// </summary>
public static class CampaignLimits
{
    /// <summary>Ops in one <c>campaign_write</c> call (one batch, one undo unit).</summary>
    public const int MaxOpsPerCall = 50;

    /// <summary>Handles one <c>campaign_get</c> call reads.</summary>
    public const int MaxRefsPerGet = 10;

    public const int DefaultListLimit = 15;
    public const int MaxListLimit = 50;

    /// <summary>An entity name: one line.</summary>
    public const int MaxNameLength = 200;

    public const int MaxSummaryLength = 1_000;

    /// <summary><c>body_md</c> and <c>secret_md</c>, and other markdown bodies (an answer, a recap).</summary>
    public const int MaxBodyLength = 50_000;

    /// <summary>A fact's statement.</summary>
    public const int MaxStatementLength = 4_000;

    /// <summary>An alias: one line.</summary>
    public const int MaxAliasLength = 120;

    public const int MaxAliasesPerOp = 20;

    /// <summary>A tag: one line.</summary>
    public const int MaxTagLength = 60;

    public const int MaxTagsPerOp = 20;

    /// <summary>Knowers in one op's <c>known_by</c> (or one campaign_knowledge call's list).</summary>
    public const int MaxKnowersPerOp = 30;

    /// <summary><c>about</c>, <c>links</c> and <c>depends_on</c>, each.</summary>
    public const int MaxFactLinksPerOp = 20;

    /// <summary>An entity's, relation's or session's <c>data</c> object, serialized.</summary>
    public const int MaxDataLength = 20_000;

    /// <summary>A key of a <c>data</c> object.</summary>
    public const int MaxDataKeyLength = 100;

    /// <summary>The text <c>campaign_knowledge check</c> scans.</summary>
    public const int MaxCheckTextLength = 50_000;

    /// <summary>A search query, as the SRD index caps it.</summary>
    public const int MaxQueryLength = 500;

    public const int MaxQueryWords = 32;

    /// <summary>Items in each list of a gate (<c>after</c>, <c>with</c>, <c>routes</c>, a route's <c>clues</c>, …).</summary>
    public const int MaxGateListItems = 20;

    /// <summary>A forbidden or preferred term, or a forbidden pattern.</summary>
    public const int MaxTermLength = 60;

    /// <summary>Words and placeholders in one forbidden pattern.</summary>
    public const int MaxPatternElements = 8;

    /// <summary>A note, a label, a knower's <c>how</c>, an objective's text and other short free text.</summary>
    public const int MaxNoteLength = 1_000;

    /// <summary>A relation label, a known_as phrasing: one line.</summary>
    public const int MaxLabelLength = 200;

    /// <summary>A <c>source</c> reference ("party-and-band.md:19").</summary>
    public const int MaxSourceLength = 500;

    /// <summary>A relation name after normalisation to snake_case.</summary>
    public const int MaxRelLength = 40;

    /// <summary>The largest session number a handle or field accepts.</summary>
    public const int MaxSessionNumber = 100_000;

    /// <summary>A relation's attitude, −100 (hostile) to 100 (devoted).</summary>
    public const int MaxAttitude = 100;

    /// <summary>A clock's segments.</summary>
    public const int MaxClockSegments = 100;

    /// <summary>One tick's amount, either way.</summary>
    public const int MaxTickAmount = 100;

    /// <summary>An objective's 1-based position.</summary>
    public const int MaxObjectiveIndex = 1_000;

    /// <summary>An objective's progress and progress_max.</summary>
    public const int MaxProgress = 1_000_000;

    /// <summary>Characters in one session's attendance list.</summary>
    public const int MaxAttendance = 30;
}
