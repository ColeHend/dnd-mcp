using System.ComponentModel;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Campaign.Ops;

/// <summary>One character's attendance at a session, as <c>campaign_session</c> receives it.</summary>
public sealed class AttendanceSpec
{
    [Description("Required. The character's handle, e.g. \"character:serif\".")]
    public string? Character { get; init; }

    [Description("They were at the table (true, the default) or absent (false).")]
    public bool? Present { get; init; }

    [Description("A note, e.g. \"joined halfway through\".")]
    public string? Note { get; init; }
}

/// <summary>
/// Validation of attendance lists.
///
/// <para>
/// Attendance decides whether a party row speaks for a character (<see cref="KnowledgeVerdicts"/>): a session with rows
/// but none for a character reads as "not listed" (uncertain), one with no rows as "not recorded" (knows). So a
/// character listed twice is refused (which row would count?), and an empty list is refused rather than read as
/// "recorded, nobody was there".
/// </para>
/// </summary>
public static class AttendanceSpecs
{
    /// <summary>The problems with an attendance list, each starting with "<paramref name="listName"/> item N"; empty when valid (or null).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<AttendanceSpec?>? attendance, string listName)
    {
        var problems = new List<string>();
        if (attendance is null)
        {
            return problems;
        }

        if (attendance.Count == 0)
        {
            problems.Add($"{listName} is empty; give [{{\"character\": \"character:belmakor\"}}, …] or leave it out (not recorded).");
            return problems;
        }

        if (attendance.Count > CampaignLimits.MaxAttendance)
        {
            problems.Add($"{listName} has {DslText.Number(attendance.Count)} characters; at most {DslText.Number(CampaignLimits.MaxAttendance)}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < attendance.Count; i++)
        {
            var where = $"{listName} item {DslText.Number(i + 1)}";
            var item = attendance[i];
            if (item is null)
            {
                problems.Add($"{where} is null; give {{\"character\": \"character:serif\", \"present\": false}}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Character))
            {
                problems.Add($"{where}: character is required, e.g. \"character:serif\".");
            }
            else if (!CampaignHandle.TryParse(item.Character, out var handle, out var problem))
            {
                problems.Add($"{where}: character: {problem}");
            }
            else if (handle is not (CampaignHandle.EntityBySlug or CampaignHandle.EntityBySeq or CampaignHandle.ByCode) ||
                     handle is CampaignHandle.EntityBySlug { Kind: not null and not CampaignValues.Kinds.Character })
            {
                problems.Add($"{where}: character \"{DslText.Echo(item.Character)}\" must name a character, e.g. \"character:serif\".");
            }
            else
            {
                var key = handle is CampaignHandle.EntityBySlug slug ? slug.Slug : handle.Text;
                if (!seen.Add(key))
                {
                    problems.Add($"{where}: {handle.Text} is listed twice.");
                }
            }

            if (item.Note is { Length: > CampaignLimits.MaxNoteLength })
            {
                problems.Add($"{where}: note is longer than {DslText.Number(CampaignLimits.MaxNoteLength)} characters.");
            }
        }

        return problems;
    }
}
