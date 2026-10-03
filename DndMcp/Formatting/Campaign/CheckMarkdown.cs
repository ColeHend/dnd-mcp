using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// <c>campaign_knowledge check</c>'s result: the "does Belmakor even know that?" pass over a draft, rendered for the
/// author. Hard flags first, then the things to review.
///
/// <para>
/// <b>Author-facing on purpose.</b> The check's job is to say what the speaker must not say, so it names true names,
/// author aliases, secret statements and who does know them. That is safe only because the check is an author tool (its
/// perspective argument is the speaker being checked, not the viewer), and the result says so in its first lines so the
/// model drafting the song does not paste the check's words into it.
/// </para>
/// <para>
/// <b>Hard flags vs to review.</b> A hard flag is a claim the draft makes that the record contradicts: a name the speaker
/// does not use for the thing (<c>other_name</c>), an entity the speaker does not know (<c>unknown_entity</c>), a name from
/// another campaign (<c>cross_campaign</c>, the firewall), a capitalised word of a name the speaker or the audience does
/// not use said on its own (<c>partial_name</c>: "Cage" of "Axiom Cage", printed with the whole name it belongs to; one
/// that opens a line or a sentence, where the capital may be the line's, is listed for review instead), a
/// word an active gate or reveal rule forbids, a name the audience of a diegetic text does not know
/// (<c>reveals_to_audience</c>), and a secret of the speaker's that the text touches in front of an audience that must not
/// hear it. A check spoken by the author has nothing to flag and says so, with how to check a character instead, never a
/// bare "pass" a model could take for a safe draft. A check with no hard flag that lists a possible partial name says so
/// in its title (the author's too, beside that advice) and never claims that every name is one the speaker uses (review
/// UR2): the word may give one away.
/// Everything else the check retrieves (related facts the speaker does not know, mistaken beliefs, stale facts,
/// capitalised words that match no name) is a word overlap or an absence, which the model must judge:
/// listing it as a failure would make every draft "fail" and teach the model to ignore the check. The order matches
/// <see cref="CheckResult.Pass"/>, which counts exactly the hard flags.
/// </para>
/// <para>
/// <b>One line per problem, not per occurrence.</b> The check reports every place a name or word occurs; a song whose
/// chorus sings "Keras" ten times has one problem, not ten. Identical flags (same kind, same words compared by
/// <see cref="CampaignText.Key"/>, same entities or rule) are printed once with their count, and the title counts problems.
/// When there are more problems than lines, each kind keeps a fair share of the lines (round-robin in the contract's
/// order) and says how many more it has, so a long list of one kind never hides that another kind exists: a forbidden
/// timespan must stay visible behind forty mis-named things.
/// </para>
/// <para>
/// <b>An author-only fact is never at risk.</b> Author visibility is absolute (contract §3.2): the reader counts an
/// author-only fact as known to no player-side view, whatever knowledge rows it has, so it is never a secret the speaker
/// could give away, and a risk's audience standing is never "knows" (a risk is a fact the speaker's reads show and the
/// audience's do not). One the text touches is listed for review among the facts the speaker does not know, as "author
/// only", with who holds rows on it.
/// </para>
/// </summary>
internal static class CheckMarkdown
{
    /// <summary>Lines per list (names, facts, …); the rest are counted.</summary>
    public const int MaxItems = 25;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// What an author-speaker check with nothing to flag says instead of "pass" (review U10): the author may say anything,
    /// so a pass means nothing about a draft, and a model asked to check a song for leaks without naming a speaker read the
    /// bare "pass" as the draft being safe.
    /// </summary>
    public const string AuthorNothingToFlag = AuthorView + AuthorPassPerspective;

    // The two halves of AuthorNothingToFlag: a diegetic check with a possible partial name for the audience counts it
    // between them ("author view: nothing to flag, 1 possible partial name to review — pass perspective …"), and keeps
    // the advice (review UR2's recheck): the plain "pass, 1 … to review" was the bare author-view pass U10 removed.
    private const string AuthorView = "author view: nothing to flag";
    private const string AuthorPassPerspective = " — pass perspective to check a character's, the party's or the public's knowledge";

    /// <summary>
    /// The To review section of words that may be a word of a name the speaker or audience does not use, but open a line
    /// or a sentence (<see cref="CheckResult.PossiblePartialNames"/>): their capital may be the line's, so they are judged,
    /// not failed.
    /// </summary>
    public const string PossiblePartialNamesTitle =
        "Possible partial names (each opens a line or sentence, so its capital may be the line's; reword it if it names the thing)";

    /// <summary>
    /// The To review section of capitalised words that match no name (<see cref="CheckResult.PossibleInventions"/>),
    /// advisory like the session checklist's (review UR01): many are ordinary words that open a line, and "accept or
    /// strike" under "no name matches" told a model to strike words the result itself listed as names the speaker uses.
    /// </summary>
    public const string PossibleInventionsTitle =
        "Capitalised words that match no name here (add any that is a new name; ignore ordinary words)";

    /// <summary>
    /// The hard-flags line when there is none but a possible partial name is listed for review (review UR2): "every name is
    /// one the speaker uses" would be false beside a word that may give away a name the speaker does not use.
    /// </summary>
    public const string NoneCertain = "None certain: see Possible partial names under To review.";

    /// <summary>The check, for the author.</summary>
    public static string Format(CampaignRow campaign, CheckResult result, bool diegetic, int? asOfSession)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        var hard = Flags(result);
        var occurrences = hard.Sum(f => f.Count);
        var possiblePartials = PossiblePartialLines(result);
        var b = new StringBuilder();
        b.Append("# Knowledge check: ")
            .Append(result.Pass
                ? AuthorSpeaker(campaign, result)
                    ? possiblePartials.Count > 0 ? $"{AuthorView}, {PossiblePartialCount(possiblePartials.Count)} to review{AuthorPassPerspective}" : AuthorNothingToFlag
                    : possiblePartials.Count > 0 ? $"pass, {PossiblePartialCount(possiblePartials.Count)} to review" : "pass"
                : $"{hard.Count.ToString(Invariant)} hard flag{(hard.Count == 1 ? string.Empty : "s")}")
            .Append(" (").Append(campaign.Slug).Append(")\n\n");
        b.Append("Speaker: ").Append(result.Speaker);
        if (diegetic)
        {
            b.Append(" · diegetic, audience: ").Append(result.Audience ?? "party");
        }

        if (asOfSession is { } asOf)
        {
            b.Append(" · as of session ").Append(asOf.ToString(Invariant));
        }

        b.Append(".\n_Author-facing: this names what the speaker must not say; never paste it into the draft._\n");

        b.Append("\n## Hard flags");
        if (hard.Count == 0 && possiblePartials.Count > 0)
        {
            b.Append('\n').Append(NoneCertain).Append('\n');
        }
        else if (hard.Count == 0)
        {
            b.Append("\nNone: every name is one the speaker uses")
                .Append(diegetic ? " and the audience knows" : string.Empty)
                .Append(", no forbidden words, no secret at risk.\n");
        }
        else
        {
            b.Append(" (").Append(hard.Count.ToString(Invariant))
                .Append(occurrences > hard.Count ? $"; {occurrences.ToString(Invariant)} places in the text" : string.Empty).Append(")\n");
            AppendFlags(b, hard);
        }

        AppendReview(b, result, possiblePartials);
        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n", "check a shorter part of the text at a time");
    }

    // "1 possible partial name", "2 possible partial names": one per review line (a word and the name it may give away).
    private static string PossiblePartialCount(int count) =>
        $"{count.ToString(Invariant)} possible partial name{(count == 1 ? string.Empty : "s")}";

    // The possible partial names' review lines: one per word, name and perspective, with how often the text has it.
    private static IReadOnlyList<string> PossiblePartialLines(CheckResult result) => result.PossiblePartialNames
        .GroupBy(p => $"{CampaignText.Key(p.Matched)}|{p.Source}|{p.Perspective}")
        .Select(g => $"{Quote(g.First().Matched)}{(g.Count() > 1 ? $" ({g.Count().ToString(Invariant)} times)" : string.Empty)}: {PartialLine(g.First())}")
        .ToList();

    /// <summary>Every distinct hard flag, one line each (with its count when it occurs more than once), in the class summary's order.</summary>
    public static IReadOnlyList<string> HardFlags(CheckResult result) => Flags(result).Select(f => f.Line).ToList();

    // The speaker is the author's view: the author, or the dm of a DM campaign.
    private static bool AuthorSpeaker(CampaignRow campaign, CheckResult result) =>
        result.Speaker == CampaignValues.PerspectiveKinds.Author ||
        (result.Speaker == CampaignValues.PerspectiveKinds.Dm && campaign.Role == CampaignValues.Roles.Dm);

    /// <summary>
    /// One problem the check found: its <paramref name="Kind"/> (the flag's label, which orders and groups the lines),
    /// the line to print, and how many places in the text raise it.
    /// </summary>
    internal sealed record HardFlag(string Kind, string Line, int Count);

    /// <summary>The distinct hard flags in the class summary's order (kind, then first place in the text).</summary>
    internal static IReadOnlyList<HardFlag> Flags(CheckResult result)
    {
        var flags = new List<(string Kind, string Key, string Head, string Body)>();
        foreach (var mention in result.Names.Where(m => m.Classification != NameClasses.Ok).OrderBy(m => Rank(m.Classification)).ThenBy(m => m.Start))
        {
            flags.Add((mention.Classification, $"{CampaignText.Key(mention.Matched)}|{Refs(mention)}",
                $"**{mention.Classification}** {Quote(mention.Matched)}", ": " + SpeakerReadings(result.Speaker, mention) + "."));
        }

        foreach (var partial in result.PartialNames.OrderBy(p => p.Start))
        {
            flags.Add((NameClasses.PartialName, $"{CampaignText.Key(partial.Matched)}|{partial.Source}|{partial.Perspective}",
                $"**{NameClasses.PartialName}** {Quote(partial.Matched)}", ": " + PartialLine(partial) + "."));
        }

        foreach (var finding in result.Forbidden.OrderBy(f => f.Start))
        {
            var until = finding.Until.Count == 0 ? string.Empty : $"; lifts when {string.Join(", ", finding.Until)} {(finding.Until.Count == 1 ? "is" : "are")} in play";
            var instead = finding.PreferredTerms.Count == 0 ? string.Empty : $"; say instead: {string.Join(", ", finding.PreferredTerms.Select(Quote))}";
            var note = string.IsNullOrWhiteSpace(finding.Note) ? string.Empty : $"; note: {OneLine(finding.Note).TrimEnd('.')}";
            var rule = finding.TermOrPattern.Equals(finding.Matched, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" (matches {Quote(finding.TermOrPattern)})";
            flags.Add(("forbidden", $"{CampaignText.Key(finding.Matched)}|{finding.Source}|{finding.TermOrPattern}",
                $"**forbidden** {Quote(finding.Matched)}{rule}", $": {finding.Source} forbids it{until}{instead}{note}."));
        }

        foreach (var mention in result.Names.Where(m => m.AudienceClassification is { } a && a != NameClasses.Ok).OrderBy(m => m.Start))
        {
            flags.Add((NameClasses.RevealsToAudience, $"{CampaignText.Key(mention.Matched)}|{Refs(mention)}",
                $"**{NameClasses.RevealsToAudience}** {Quote(mention.Matched)}", ": " + AudienceReadings(result.Audience ?? "the audience", mention) + "."));
        }

        foreach (var risk in result.SecretsAtRisk.Where(r => r.Reason == RiskReasons.RelatedToText))
        {
            flags.Add(("secret at risk", risk.Fact.Ref, $"**secret at risk** {FactLabel(risk.Fact)}",
                ": " + RiskLine(result, risk) + $" The text touches it ({risk.Fact.Why}); a song or speech is a public statement."));
        }

        return flags
            .GroupBy(f => (f.Kind, f.Key))
            .Select(g =>
            {
                var first = g.First();
                var count = g.Count();
                return new HardFlag(first.Kind, first.Head + (count > 1 ? $" ({count.ToString(Invariant)} times)" : string.Empty) + first.Body, count);
            })
            .ToList();
    }

    // The flags under the cap: all of them, or each kind's round-robin share of MaxItems lines with its own "and N more".
    private static void AppendFlags(StringBuilder b, IReadOnlyList<HardFlag> flags)
    {
        var kinds = flags.GroupBy(f => f.Kind).Select(g => g.ToList()).ToList();
        var quota = new int[kinds.Count];
        var lines = 0;
        while (lines < MaxItems && Enumerable.Range(0, kinds.Count).Any(k => quota[k] < kinds[k].Count))
        {
            for (var k = 0; k < kinds.Count && lines < MaxItems; k++)
            {
                if (quota[k] < kinds[k].Count)
                {
                    quota[k]++;
                    lines++;
                }
            }
        }

        for (var k = 0; k < kinds.Count; k++)
        {
            foreach (var flag in kinds[k].Take(quota[k]))
            {
                b.Append("- ").Append(flag.Line).Append('\n');
            }

            WriteMarkdown.AppendMore(b, quota[k], kinds[k].Count, kinds[k][0].Kind + " flags");
        }
    }

    // What a flag's readings point at, so two mentions of one word that stand for different things stay apart.
    private static string Refs(NameMention mention) => string.Join(",", mention.Candidates.Select(c => c.Ref).Order(StringComparer.Ordinal));

    // "a word of "Axiom Cage" (item:axiom-cage; its name), which character:belmakor knows as "the thing he wants"", with the
    // full name it belongs to (author-facing: the check names what must not be said), or the rule forbidding the term.
    private static string PartialLine(PartialName partial)
    {
        if (partial.SourceKind == PartialNameSources.ForbiddenTerm)
        {
            return $"a word of the term {Quote(partial.FullName)}, which {partial.Source} forbids";
        }

        var what = partial.SourceKind == PartialNameSources.Alias ? $"{partial.Visibility ?? "an"} alias" : "its name";
        var whose = partial.KnownAs is { Length: > 0 } known
            ? $"which {partial.Perspective} knows as {Quote(known)}"
            : $"a name {partial.Perspective} does not know";
        return $"a word of {Quote(partial.FullName)} ({partial.Source}; {what}), {whose}";
    }

    // "character:belmakor knows it; party: does not know (recorded)".
    private static string RiskLine(CheckResult result, SecretAtRisk risk) =>
        $"{result.Speaker} knows it; {result.Audience ?? "the audience"}: {Standing(risk.AudienceStanding)}.";

    // other_name, unknown_entity, cross_campaign: the speaker's name flags in the order the class summary gives them.
    private static int Rank(string classification) => classification switch
    {
        NameClasses.OtherName => 0,
        NameClasses.UnknownEntity => 1,
        _ => 2,
    };

    private static void AppendReview(StringBuilder b, CheckResult result, IReadOnlyList<string> possiblePartials)
    {
        // "has no record of knowing", never "does not know" (review U15, contract §3.3): the section lists "no record" and
        // "uncertain" facts too, and titled "does not know" it asserted what nobody recorded (Nadar's own plan, "a fact
        // character:nadar does not know").
        var sections = new List<(string Title, IReadOnlyList<string> Lines)>
        {
            (PossiblePartialNamesTitle, possiblePartials),
            ($"Facts {result.Speaker} has no record of knowing", result.UnknownFacts.Select(f =>
                $"{FactLabel(f)}: {Standing(f)}{KnownBy(f)} · why: {f.Why}").ToList()),
            ("Mistaken beliefs", result.MistakenBeliefs.Select(f =>
                $"{FactLabel(f)}: {f.State ?? "believes"}, but its truth is {f.Truth} · why: {f.Why}").ToList()),
            ("Stale facts", result.Stale.Select(s =>
                s.Depth == 0
                    ? $"{s.Ref} {Quote(Short(s.Statement))}: superseded"
                    : $"{s.Ref} {Quote(Short(s.Statement))}: rests on superseded {s.Superseded} (depth {s.Depth.ToString(Invariant)})").ToList()),
            ("Secrets about the speaker (not in the text; keep them out)", result.SecretsAtRisk.Where(r => r.Reason == RiskReasons.AboutSpeaker)
                .Select(r => $"{FactLabel(r.Fact)}: {result.Audience ?? "the audience"}: {Standing(r.AudienceStanding)}").ToList()),
            (PossibleInventionsTitle, result.PossibleInventions.Select(Quote).ToList()),
            ($"Related facts {result.Speaker} knows (context for judging how oblique a line is)", result.Related.Select(f =>
                $"{FactLabel(f)}: {f.State ?? "knows"} · why: {f.Why}").ToList()),
        };

        var ok = result.Names.Where(m => m.Classification == NameClasses.Ok && m.AudienceClassification is null or NameClasses.Ok)
            .Select(m => $"{Quote(m.Matched)} → {string.Join(" or ", m.Candidates.Select(c => c.Ref).Distinct(StringComparer.Ordinal))}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (sections.All(s => s.Lines.Count == 0) && ok.Count == 0)
        {
            b.Append("\n## To review\nNothing: no related facts, stale facts or unknown names.\n");
            return;
        }

        b.Append("\n## To review\n");
        foreach (var (title, lines) in sections.Where(s => s.Lines.Count > 0))
        {
            b.Append("\n**").Append(title).Append("** (").Append(lines.Count.ToString(Invariant)).Append(")\n");
            foreach (var line in lines.Take(MaxItems))
            {
                b.Append("- ").Append(line).Append('\n');
            }

            WriteMarkdown.AppendMore(b, MaxItems, lines.Count, "entries");
        }

        if (ok.Count > 0)
        {
            b.Append("\nNames used as the speaker uses them: ").Append(string.Join("; ", ok.Take(MaxItems)))
                .Append(ok.Count > MaxItems ? $"; … and {(ok.Count - MaxItems).ToString(Invariant)} more" : string.Empty).Append(".\n");
        }
    }

    private static string SpeakerReadings(string speaker, NameMention mention) => string.Join("; ", mention.Candidates.Select(c => Reading(speaker, c)));

    private static string Reading(string speaker, NameCandidate candidate)
    {
        var what = $"{candidate.Ref} ({candidate.Name}; {Matched(candidate)})";
        return candidate.Classification switch
        {
            NameClasses.Ok => $"{what}: fine for {speaker}",
            NameClasses.OtherName => $"{what}: {speaker} knows it as {Quote(candidate.SpeakerName ?? "another name")}",
            NameClasses.CrossCampaign => $"{what}: a name from campaign {candidate.Campaign}; nobody here knows it",
            _ => $"{what}: {speaker} does not know this {Kind(candidate.Ref)}",
        };
    }

    private static string AudienceReadings(string audience, NameMention mention) => string.Join("; ", mention.Candidates.Select(c =>
        c.AudienceName is { Length: > 0 } known
            ? $"{c.Ref}: {audience} knows it as {Quote(known)}"
            : $"{c.Ref}: {audience} does not know it"));

    private static string Matched(NameCandidate candidate) => candidate.MatchedAs switch
    {
        NameMatchKinds.Alias => $"{candidate.MatchedVisibility ?? "an"} alias",
        NameMatchKinds.KnownAs => $"{candidate.MatchedVisibility ?? "a knower"}'s name for it",
        _ => "its name",
    };

    private static string Kind(string reference)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        var start = slash >= 0 && slash < colon ? slash + 1 : 0;
        return colon > start && reference[start..colon] is not ("e" or "f") ? reference[start..colon] : "entity";
    }

    private static string FactLabel(FactFinding fact)
    {
        var code = fact.Code is { Length: > 0 } c ? $" ({c})" : string.Empty;
        return $"{fact.Ref}{code} {Quote(Short(fact.Statement))}";
    }

    private static string Standing(FactFinding fact) => fact.Standing switch
    {
        Standings.NotInPlay => $"not in play ({fact.CanonStatus})",
        Standings.DoesNotKnow when fact.State is { } state => $"{state} (recorded)",
        _ => Standing(fact.Standing) + (string.IsNullOrWhiteSpace(fact.Explanation) || fact.Standing == Standings.NoRecord ? string.Empty : $": {OneLine(fact.Explanation)}"),
    };

    private static string Standing(string standing) => standing switch
    {
        Standings.NoRecord => "no record",
        Standings.DoesNotKnow => "does not know (recorded)",
        Standings.NotInPlay => "not in play",
        _ => standing.Replace('_', ' '),
    };

    private static string KnownBy(FactFinding fact) =>
        fact.KnownBy.Count == 0 ? " · known by nobody on record" : $" · known by {string.Join(", ", fact.KnownBy)}";

    private static string Short(string text) => CampaignMarkdownText.Excerpt(text, 140);

    private static string Quote(string text) => $"\"{OneLine(text)}\"";

    private static string OneLine(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
}
