using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// What one normalization decided: notes (a default used, an override applied, a spell with no combat effect) and
/// warnings (something the simulator leaves out). Kept in order and de-duplicated, because a warning that repeats per
/// multiattack step or per form reads as a different problem each time.
/// </summary>
internal sealed class NormalizationLog
{
    private readonly List<string> _notes = [];
    private readonly List<NormalizationWarning> _warnings = [];

    public IReadOnlyList<string> Notes => _notes;

    public IReadOnlyList<NormalizationWarning> Warnings => _warnings;

    public void Note(string note)
    {
        if (!_notes.Contains(note, StringComparer.Ordinal))
        {
            _notes.Add(note);
        }
    }

    public void Warn(string code, string where, string message)
    {
        if (!_warnings.Any(w => w.Code == code && w.Where == where && w.Message == message))
        {
            _warnings.Add(new NormalizationWarning(code, where, message));
        }
    }

    public void NotModelled(string where, string message) => Warn(StatBlockValues.WarningCodes.NotModelled, where, message);

    public void Approximated(string where, string message) => Warn(StatBlockValues.WarningCodes.Approximated, where, message);

    public void Unparsed(string where, string message) => Warn(StatBlockValues.WarningCodes.Unparsed, where, message);

    public void Conflict(string where, string message) => Warn(StatBlockValues.WarningCodes.DataConflict, where, message);

    public void Unresolved(string where, string message) => Warn(StatBlockValues.WarningCodes.UnresolvedReference, where, message);
}
