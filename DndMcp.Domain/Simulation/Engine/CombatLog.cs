using System.Text;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// The replay's combat log: one line per event, cut at <see cref="SimulationLimits.ReplayLogChars"/> with a closing
/// "… truncated" line, so a 20-round dragon fight cannot flood the model's context. The fight's summary is added after
/// the cut (<see cref="Finish"/>), so the outcome is always shown.
///
/// <para>
/// Only a replay has a log. Every call site guards with <c>if (_log is not null)</c> before formatting anything, so a
/// normal run pays nothing for the log's existence.
/// </para>
/// </summary>
internal sealed class CombatLog
{
    private readonly StringBuilder _text = new();
    private readonly int _limit;
    private bool _truncated;

    public CombatLog(int limit = SimulationLimits.ReplayLogChars)
    {
        _limit = limit;
    }

    public void Line(string line)
    {
        if (_truncated)
        {
            return;
        }

        if (_text.Length + line.Length + 1 > _limit)
        {
            _text.AppendLine("… truncated (the log is capped; the summary below is complete).");
            _truncated = true;
            return;
        }

        _text.AppendLine(line);
    }

    public bool Truncated => _truncated;

    /// <summary>The log with the summary appended (never cut).</summary>
    public string Finish(string summary) => _text + summary;
}
