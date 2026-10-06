using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// One typed text an author step puts where a party view may read it (contract D20b), and what the party view shows
/// instead when it fails the view-text check.
/// </summary>
/// <param name="Text">The typed text (a combatant's typed name, an effect's name, a spell's name).</param>
/// <param name="What">What it names, for the warning ("this combatant", "this effect", "this concentration").</param>
/// <param name="Instead">What the party view shows in its place ("Aboleth", "an effect", "no spell name").</param>
internal sealed record TypedText(string Text, string What, string Instead);

/// <summary>
/// The D20b warnings of an author step (<c>add</c>, <c>set</c>, <c>condition</c>, <c>concentration</c>): a typed name the
/// party view would not show, or one it shows that starts with a possible name of hidden prose (F3, review F2R02). Phase 7 filters player-readable text at READ time (§6.12: the board prints a failing name
/// as the monster's name, "an unknown creature" or "an effect"), so nothing is refused; the author is told what the party
/// will see and why, with the fix when there is one ("add it with character:the-nester to show the name the party
/// knows"). The check is C's <see cref="ViewTextCheck"/> for the party, one render over every typed text of the call, on a
/// read connection BEFORE the step's transaction (it cannot run inside one).
/// </summary>
internal static class CombatWarnings
{
    /// <summary>The warnings for <paramref name="texts"/> (empty when every one passes, or there are none).</summary>
    public static IReadOnlyList<string> For(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<TypedText> texts)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var check = ViewTextCheck.Check(connection, campaign, Perspective.Parse(CV.PerspectiveKinds.Party), texts.Select(t => (string?)t.Text).ToList());
        var warnings = new List<string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var verdict = check.Texts[i];
            if (!verdict.Passes)
            {
                warnings.Add(Warning(texts[i], verdict));
            }
            else if (verdict.PossibleNames.Count > 0)
            {
                warnings.Add(PossibleNameWarning(texts[i], verdict));
            }
        }

        return warnings.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// "'Baalite cultist' starts with 'Baal', a name in hidden text (f:12): the party may read it as that name; party views
    /// show this combatant as written." (F3, review F2R02): a typed word starting with a word that hidden prose capitalises
    /// mid-sentence is printed (prose is warn-only), and the author is told what the party may read in it.
    /// </summary>
    public static string PossibleNameWarning(TypedText text, ViewTextVerdict verdict)
    {
        var finding = verdict.PossibleNames[0];
        var sources = verdict.PossibleNames.SelectMany(f => f.Sources).Distinct(StringComparer.Ordinal).Take(5).ToList();
        var cited = sources.Count == 0 ? string.Empty : $" ({string.Join(", ", sources)})";
        return $"'{text.Text}' starts with '{finding.Classification}', a name in hidden text{cited}: the party may read it as that name; " +
               $"party views show {text.What} as written.";
    }

    /// <summary>
    /// "'The Nester' is a name the party does not use (character:the-nester): party views show this combatant as 'Aboleth';
    /// add it with character:the-nester to show the name the party knows."
    /// </summary>
    public static string Warning(TypedText text, ViewTextVerdict verdict)
    {
        var finding = verdict.Findings.FirstOrDefault();
        var sources = verdict.Findings.SelectMany(f => f.Sources).Distinct(StringComparer.Ordinal).Take(5).ToList();
        var cited = sources.Count == 0 ? string.Empty : $" ({string.Join(", ", sources)})";
        var reason = finding?.Kind switch
        {
            ViewTextFindingKinds.Name => "is a name the party does not use",
            ViewTextFindingKinds.PartialName => "holds part of a name the party does not use",
            ViewTextFindingKinds.Forbidden => "holds a word the party may not read yet",
            ViewTextFindingKinds.HiddenWord => "holds a word only text the party cannot see holds",
            _ => "is text the party may not read",
        };
        var entity = verdict.Findings
            .Where(f => f.Kind is ViewTextFindingKinds.Name or ViewTextFindingKinds.PartialName)
            .SelectMany(f => f.Sources)
            .FirstOrDefault(s => !s.Contains('/', StringComparison.Ordinal) && s.Contains(':', StringComparison.Ordinal) &&
                                 !s.StartsWith("f:", StringComparison.Ordinal) && !s.StartsWith("rule:", StringComparison.Ordinal));
        var fix = text.What == "this combatant" && entity is not null
            ? $"; add it with {entity} to show the name the party knows"
            : string.Empty;
        return $"'{text.Text}' {reason}{cited}: party views show {text.What} as '{text.Instead}'{fix}.";
    }
}
