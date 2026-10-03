using DndMcp.Domain.Campaign;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Hand-written attendance over (character, session) → present, with the sessions that have any attendance rows. It
/// answers exactly as the repository's implementation must: a session with no rows is NotRecorded, a session with rows
/// but none for the character is NotListed (never Present).
/// </summary>
internal sealed class FakeAttendance : IAttendance
{
    private readonly Dictionary<(string Character, int Session), bool> _rows = [];

    /// <summary>Attendance where every session is <paramref name="answer"/> for everyone (a table-driven shortcut).</summary>
    public static IAttendance Always(AttendanceAnswer answer) => new Constant(answer);

    public FakeAttendance Row(int session, string character, bool present)
    {
        _rows[(character, session)] = present;
        return this;
    }

    public AttendanceAnswer Of(string characterId, int sessionNumber)
    {
        if (!_rows.Keys.Any(k => k.Session == sessionNumber))
        {
            return AttendanceAnswer.NotRecorded;
        }

        return _rows.TryGetValue((characterId, sessionNumber), out var present)
            ? present ? AttendanceAnswer.Present : AttendanceAnswer.Absent
            : AttendanceAnswer.NotListed;
    }

    private sealed class Constant(AttendanceAnswer answer) : IAttendance
    {
        public AttendanceAnswer Of(string characterId, int sessionNumber) => answer;
    }
}

/// <summary>Short builders for perspectives and knowledge rows.</summary>
internal static class Knowers
{
    public static PerspectiveContext Author(string role = "player") => PerspectiveContext.For(Perspective.Author, role);

    public static PerspectiveContext Of(string perspective, string role = "player") => PerspectiveContext.For(Perspective.Parse(perspective), role);

    public static PerspectiveContext Character(string id, string name, PartyMembership? membership, string role = "player") =>
        new(Perspective.Parse("character:" + id), role, id, name, membership);

    public static KnowledgeEntry Row(string knowerKind, string state, int? learned = null, string? knownAs = null, string? knowerId = null,
        int? validUntil = null) =>
        new(knowerKind, knowerKind == "character" ? knowerId ?? throw new ArgumentException("character rows need an id") : null, state, knownAs,
            learned, validUntil);
}
