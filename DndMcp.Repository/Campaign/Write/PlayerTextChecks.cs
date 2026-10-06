using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using CV = DndMcp.Domain.Campaign.CampaignValues;
using F = DndMcp.Repository.Campaign.Write.PlayerTextFields;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// The write-time player-text check (review L12 and U09): when a batch finishes, the text it made readable to a
/// player-side view is scanned for (a) words an active gate or reveal rule forbids and (b) names that view does not use:
/// an author or restricted alias (and the author's own known_as), or the true name of an entity the view knows only under
/// a disguise. Each hit is a <see cref="PlayerTextWarning"/> naming the field, the word and what to do instead. Warn and
/// apply, as gate warnings do: the author may mean it, and a refusal would sink a whole recap over one word.
///
/// <para>
/// <b>Why at write time.</b> Only campaign_knowledge check looked at vocabulary and names, and only at a draft someone
/// thought to check. Everything else went straight to the table: a fact told to the party without a known_as shows its
/// statement ("Morwen Vashkar poisoned the well." on the page of the woman the party knows as "the veiled woman"), a
/// played session's recap and title are party-visible ("Keras told us the white lines are a seal." while a gate forbids
/// both words), a party-visible place is named "Keras's tomb", and retracting the party's known_as row on Keras shows
/// the party his true name. The tools' own descriptions promise that forbidden words stay out of player-facing text; this
/// is where that promise is checked.
/// </para>
/// <para>
/// <b>What "made readable" means.</b> Every writer notes each entity and fact it is about to change (its row, its
/// aliases, a knowledge row on it, a session's status or recap) BEFORE the first change, and this class snapshots what
/// each player-side view reads of it then; at the end of the batch it reads them again and scans only text that is new
/// for some view. So re-recording what a view already reads repeats nothing, while a new reader (a knower given an aware
/// row, a disguise retracted, a session started or played) has everything it now reads checked. The views are the
/// player-side ones a reader can take: party, table, public, dm in a player campaign (in a DM campaign the DM is the
/// author), the party's current members, and every character with a knowledge row of its own on the target: the
/// non-author-view knowers of §3.4's reveal checks, NPCs included, since a vocabulary rule keeps its words out of every
/// non-author text and an NPC's view is what the DM speaks as that NPC from. What a view reads follows the read path's
/// rules: the verdicts (<see cref="KnowledgeVerdicts"/>, dated by the target's session), the disguised view and its
/// aliases (<see cref="EntityViews"/>), author visibility, and nothing that is not in play (a planned fact, a session not
/// yet live or played).
/// </para>
/// <para>
/// <b>Rules are judged as the batch leaves them</b>, not as of the session a recap belongs to: the players read the text
/// now, so a word a rule forbade in session 1 but no longer forbids is not reported in session 1's recap written today,
/// and a word a rule forbids today is.
/// </para>
/// <para>
/// <b>Names.</b> A name hit is fine when any entity it may name is one the reading view uses by that name, or by a
/// longer name that contains it (<see cref="EntityView.UsedNames"/>: "Valdris" when the party calls him "Captain
/// Valdris"). Otherwise it is flagged only for the two leaks the contract's leak rule names: the true name of an entity
/// the view knows under another name (or does not recognise), and an author or restricted alias (the author's known_as
/// counts as one). The name of an entity the view simply has not met is not flagged: a recap naming the NPC the party met
/// tonight is the normal case, not a leak. So a known_as that contains the true name ("Captain Valdris", "Valdris's
/// ghost") is the knower's own name and leaks nothing, and neither does the bare "Valdris" in a recap the party reads.
/// </para>
/// <para>
/// <b>A name must be written as one</b> (unlike forbidden words, which §3.4 matches in any case). The scanner ignores
/// case, and a campaign names people and powers with ordinary words ("Raven", "the Protector", "the Peaceful One"), so
/// "a raven watched the docks" and "the protector of the harbour" would be reported as true names, with advice to say
/// "the cloaked stranger" instead. A hit counts only when the stored name capitalises none of its words, or the text
/// capitalises at least one of the words the stored name does. The read path's check still matches in any case: there
/// the author asked about one draft, here every write is scanned.
/// </para>
/// <para>
/// <b>Undo and redo are covered</b> (review U09): <see cref="UndoEngine"/> reverses rows, not ops, so no op notes
/// anything; <see cref="HistoryWriter"/> instead hands this class the batch's rows before the first is reversed
/// (<see cref="Reversing"/>), and finishes it after. Undoing the batch that recorded the party's known_as for Keras then
/// warns that the party now reads "Keras", as the retract of that row does.
/// </para>
/// <para>
/// <b>A hidden name has a second remedy.</b> When the true name of an entity the reader knows under a disguise is in the
/// text, the author may mean it: the batch is how the party learns the name ("The ancient sorcerer king's name is Keras.").
/// Rewording it to the known_as is then wrong, and leaving the old known_as in place leaves a record that contradicts
/// itself: the party knows the fact that states the name, yet every check forbids the party the name. So the warning
/// also prints the campaign_knowledge record call that sets the reader's known_as to the true name (which ends the
/// disguise), naming the campaign and keeping the reader's state, ready to send as printed.
/// </para>
/// <para>
/// <b>Not covered:</b> objectives, tags and relation labels; a change of who reads (a member joining or leaving the
/// party, attendance) beyond the targets a writer notes (an undo notes a session whose attendance it reverses, not the
/// facts learned in it); and other text that merely mentions a name whose disguise the batch changed (only the noted
/// targets' own text is scanned, not the whole campaign).
/// </para>
/// </summary>
internal sealed class PlayerTextChecks
{
    private readonly WriteBatch _batch;
    private readonly Dictionary<(bool IsFact, string Id), Noted> _noted = new();
    private readonly List<Noted> _order = [];
    private Lens? _before;

    public PlayerTextChecks(WriteBatch batch) => _batch = batch;

    /// <summary>Notes an entity the batch is about to change (its row, aliases or knowledge rows). Call before the first change.</summary>
    public void Entity(string entityId, int? opIndex) => Note(isFact: false, entityId, opIndex, created: false);

    /// <summary>Notes an entity the batch has just created (nothing of it was readable before).</summary>
    public void EntityCreated(string entityId, int? opIndex) => Note(isFact: false, entityId, opIndex, created: true);

    /// <summary>Notes a fact the batch is about to change (its row or knowledge rows). Call before the first change.</summary>
    public void Fact(string factId, int? opIndex) => Note(isFact: true, factId, opIndex, created: false);

    /// <summary>Notes a fact the batch has just created.</summary>
    public void FactCreated(string factId, int? opIndex) => Note(isFact: true, factId, opIndex, created: true);

    /// <summary>
    /// Notes, before an undo reverses <paramref name="rows"/> (the original batch's change_log rows), every entity and fact
    /// whose player-readable text they can change: an entity's own row, its aliases and tags; a fact's row; a knowledge
    /// row's target (the entity or fact it is about, read from the logged row, or from the row itself for a field
    /// update); a session's row and attendance (the session entity, whose title and recap they show or hide). A row of
    /// any other table changes no text this class scans. Call it before the first row is reversed: what each view reads
    /// now is the "before" of the comparison.
    /// </summary>
    public void Reversing(IReadOnlyList<ChangeRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.TargetTable == CampaignTables.Entity.Name)
            {
                Entity(row.TargetId, null);
            }
            else if (row.TargetTable == CampaignTables.Fact.Name)
            {
                Fact(row.TargetId, null);
            }
            else if (row.TargetTable == CampaignTables.Knowledge.Name)
            {
                var (factId, entityId) = KnowledgeTargetOf(row);
                if (factId is not null)
                {
                    Fact(factId, null);
                }
                else if (entityId is not null)
                {
                    Entity(entityId, null);
                }
            }
            else if ((row.TargetTable == CampaignTables.EntityAlias.Name || row.TargetTable == CampaignTables.EntityTag.Name ||
                      row.TargetTable == CampaignTables.Session.Name || row.TargetTable == CampaignTables.SessionAttendance.Name) &&
                     row.EntityId is { } entity)
            {
                Entity(entity, null);
            }
        }
    }

    // A knowledge row's target: from the logged row (a create's new value, a delete's old one), else from the row as it
    // stands (a field update leaves fact_id and entity_id as they were).
    private (string? FactId, string? EntityId) KnowledgeTargetOf(ChangeRow row)
    {
        if (row.FieldPath is null && (row.NewValue ?? row.OldValue) is { } logged && JsonNode.Parse(logged) is JsonObject json)
        {
            return (json["fact_id"]?.GetValue<string>(), json["entity_id"]?.GetValue<string>());
        }

        return _batch.Connection.QueryFirstOrDefault<(string? FactId, string? EntityId)>(
            "SELECT fact_id, entity_id FROM knowledge WHERE id = @id", new { id = row.TargetId }, _batch.Transaction);
    }

    /// <summary>Scans the text the batch made readable and adds a warning per hit (see the class summary).</summary>
    public void Finish()
    {
        if (_order.Count == 0)
        {
            return;
        }

        var after = new Lens(_batch);
        var fresh = new List<NewText>();
        foreach (var target in _order)
        {
            var now = target.IsFact ? after.FactItems(target.Id) : after.EntityItems(target.Id);
            var byText = new Dictionary<(string Field, string Text), NewText>();
            foreach (var item in now.Where(i => !target.Before.Contains(i.Key)))
            {
                if (!byText.TryGetValue((item.Key.Field, item.Key.Text), out var text))
                {
                    text = new NewText(target, item.Ref, item.Key.Field, item.Key.Text, []);
                    byText.Add((item.Key.Field, item.Key.Text), text);
                    fresh.Add(text);
                }

                text.Readers.Add(item.Reader);
            }
        }

        if (fresh.Count == 0)
        {
            return;
        }

        var rules = ActiveVocabulary();
        var names = after.Names();
        foreach (var text in fresh)
        {
            Forbidden(after, text, rules);
            HiddenNames(after, text, names.For(text.Target.IsFact ? null : text.Target.Id, text.Text));
        }
    }

    private void Note(bool isFact, string id, int? opIndex, bool created)
    {
        if (_noted.ContainsKey((isFact, id)))
        {
            return;
        }

        HashSet<TextKey> before = [];
        if (!created)
        {
            _before ??= new Lens(_batch);
            before = (isFact ? _before.FactItems(id) : _before.EntityItems(id)).Select(i => i.Key).ToHashSet();
        }

        var noted = new Noted(isFact, id, opIndex, before);
        _noted.Add((isFact, id), noted);
        _order.Add(noted);
    }

    // ---- (a) forbidden vocabulary -------------------------------------------------------------------------------------

    // Every vocabulary rule active once the batch is done (the batch may have lifted one: a gate with no forbidden_until
    // lifts when the party learns its fact): gates by FactGates' status, reveal rules while an until fact is not in play.
    private List<ForbiddenRule> ActiveVocabulary()
    {
        var rules = new List<ForbiddenRule>();
        var evaluation = new GateEvaluation(_batch);
        foreach (var (factId, gate) in evaluation.Gates)
        {
            if (FactGates.VocabularyRule(gate, evaluation.Ref(factId)) is { } rule && evaluation.Status(factId, null).ForbiddenActive)
            {
                rules.Add(rule);
            }
        }

        foreach (var ruleEntity in evaluation.RevealRules())
        {
            if (RevealRuleData.TryParse(ruleEntity.Data, out var data) && data is not null &&
                data.IsActive(handle => evaluation.UntilFactId(handle) is { } id && evaluation.InPlay(id, null)))
            {
                rules.Add(data.ToRule(ruleEntity.Handle));
            }
        }

        return rules;
    }

    private void Forbidden(Lens lens, NewText text, IReadOnlyList<ForbiddenRule> rules)
    {
        var seen = new HashSet<(string Source, string Term)>();
        foreach (var hit in ForbiddenVocabulary.Scan(text.Text, rules))
        {
            if (!seen.Add((hit.Rule.Source, hit.TermOrPattern)))
            {
                continue;
            }

            var reader = text.Readers[0];
            var preferred = hit.Rule.PreferredTerms.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            var remedy = Remedy(lens, text, reader, preferred.Count == 0 ? null : SayInstead(preferred[0], preferred.Skip(1)));
            var by = hit.Rule.Source.StartsWith("f:", StringComparison.Ordinal) ? $"{hit.Rule.Source}'s gate" : hit.Rule.Source;
            Add(new PlayerTextWarning(WarningKinds.ForbiddenWord,
                $"{Where(text, reader)} uses \"{hit.Surface}\", forbidden by {by} while it holds: {remedy}.",
                text.Target.OpIndex, text.Ref, text.Field, hit.Surface, reader.Key, hit.Rule.Source, remedy));
        }
    }

    // ---- (b) names the reader does not use ----------------------------------------------------------------------------

    private void HiddenNames(Lens lens, NewText text, NameScanner scanner)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in scanner.Scan(text.Text))
        {
            // Only a reading the text writes as a name can leak (class summary); a name the reader uses is fine in any case.
            var readings = hit.Entries.Select(e => (Surface: e.Surface, Reading: (NameReading)e.Payload)).ToList();
            var named = readings.Where(r => WrittenAsName(hit.Matched, r.Surface)).ToList();
            if (named.Count == 0)
            {
                continue;
            }

            foreach (var reader in text.Readers)
            {
                if (readings.Any(r => lens.Uses(reader, r.Reading.EntityId, r.Surface)) || Flag(lens, reader, named) is not { } flag ||
                    !seen.Add(flag.Entity.Id))
                {
                    continue;
                }

                var remedy = Remedy(lens, text, reader, flag.ShownAs is null ? null : SayInstead(flag.ShownAs, [])) +
                             (flag.Learned is null ? string.Empty : "; " + flag.Learned);
                Add(new PlayerTextWarning(WarningKinds.HiddenName,
                    $"{Where(text, reader)} uses \"{hit.Matched}\", {flag.What}: {remedy}.",
                    text.Target.OpIndex, text.Ref, text.Field, hit.Matched, reader.Key, flag.Entity.Ref, remedy));
                break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="matched"/>, a hit on the stored name <paramref name="surface"/>, is written as that name
    /// (class summary): the stored name capitalises none of its words (after a leading article), or the text capitalises
    /// at least one of the words it does. "Raven" and "the Protector" are names; "a raven" and "the protector of the
    /// harbour" are ordinary words; "the old king", stored in lower case, is a name in any case.
    /// </summary>
    internal static bool WrittenAsName(string matched, string surface)
    {
        var stored = CampaignWords.Split(surface);
        if (stored.Count > 1 && CampaignWords.IsArticle(stored[0].Key))
        {
            stored = stored.Skip(1).ToList();
        }

        // The scanner's hit has the stored name's words one for one; anything else is a fold this method does not know,
        // and a missed name is a leak, so it counts.
        var written = CampaignWords.Split(matched);
        if (written.Count != stored.Count)
        {
            return true;
        }

        var capitalised = false;
        for (var k = 0; k < stored.Count; k++)
        {
            if (!stored[k].Capitalised)
            {
                continue;
            }

            capitalised = true;
            if (written[k].Capitalised)
            {
                return true;
            }
        }

        return !capitalised;
    }

    // Why a name the reader does not use is a leak, or null when it is not one (an entity the reader has not met). The true
    // name of a disguised entity first: it says the most ("which party knows as …").
    private static Flagged? Flag(Lens lens, Reader reader, IReadOnlyList<(string Surface, NameReading Reading)> readings)
    {
        foreach (var (_, reading) in readings.Where(r => r.Reading.MatchedAs == NameMatches.Name))
        {
            if (lens.Entity(reading.EntityId) is { } entity && lens.ViewOf(reader, entity) is { View: { Visible: true, Disguised: true } } seen)
            {
                var known = seen.Verdict.KnownAs is { Length: > 0 } knownAs
                    ? $"which {reader.Key} knows as \"{knownAs.Trim()}\""
                    : $"which {reader.Key} does not recognise";
                return new Flagged(entity, $"the true name of {entity.Ref}, {known}", seen.Verdict.KnownAs?.Trim(),
                    Learned(lens.CampaignSlug, reader, entity, seen.Verdict));
            }
        }

        foreach (var (_, reading) in readings)
        {
            var kind = reading.MatchedAs switch
            {
                NameMatches.Alias when reading.Detail is CV.Visibilities.Author => "an author alias",
                NameMatches.Alias when reading.Detail is CV.Visibilities.Restricted => "a restricted alias",
                NameMatches.KnownAs when reading.Detail is CV.KnowerKinds.Author || (reading.Detail is CV.KnowerKinds.Dm && lens.IsDmCampaign) =>
                    "the author's own name",
                _ => null,
            };
            if (kind is null || lens.Entity(reading.EntityId) is not { } entity)
            {
                continue;
            }

            // The name to say instead: the one the reader shows the entity under, never the "an unrecognized …" stand-in.
            var seen = lens.ViewOf(reader, entity);
            var shown = !seen.View.Visible ? null
                : !seen.View.Disguised ? seen.View.DisplayName
                : string.IsNullOrWhiteSpace(seen.Verdict.KnownAs) ? null : seen.Verdict.KnownAs.Trim();
            return new Flagged(entity, $"{kind} of {entity.Ref}, which {reader.Key} does not use", shown);
        }

        return null;
    }

    // ---- messages -----------------------------------------------------------------------------------------------------

    // "location:kerass-tomb's name", "f:1's statement", "party's known_as for character:keras".
    private static string Where(NewText text, Reader reader) => text.Field switch
    {
        F.Alias => $"{text.Ref}'s alias \"{text.Text}\"",
        F.KnownAs => $"{reader.Key}'s known_as for {text.Ref}",
        _ => $"{text.Ref}'s {text.Field}",
    };

    // What to do, by field. A free-text field takes the specific advice ("say \"shell\" instead", "say \"the veiled
    // woman\" instead") when there is one; a statement told through a knowledge row is fixed with the phrasing of the knower
    // whose row it is (the read path then shows the known_as, never the statement); a name or an alias cannot be reworded
    // in place.
    private static string Remedy(Lens lens, NewText text, Reader reader, string? specific)
    {
        switch (text.Field)
        {
            case F.Statement when lens.PhrasingOwner(text.Target.Id, reader) is { } owner:
                return $"give known_as with {Whose(owner)} phrasing" + (specific is null ? string.Empty : "; " + specific);
            case F.Name:
                return "rename it to what the players call it and keep this name as an author alias";
            case F.Alias:
                return "mark the alias author";
        }

        if (specific is not null)
        {
            return specific;
        }

        return text.Field switch
        {
            F.Statement => "reword the statement, or make the fact restricted and record who knows it",
            F.KnownAs => "reword the known_as",
            F.Title => "pass title to end or record_past to change it",
            F.Recap => "reword the recap (record_past changes it)",
            _ => "reword it (secret_md is the place for what only the author may read)",
        };
    }

    /// <summary>
    /// The hidden-name warning's second remedy (class summary): "or, if party now knows the name, record it:
    /// campaign_knowledge {…}", the record call that sets the reader's known_as to the entity's true name, which ends the
    /// disguise (a known_as that is the entity's own name disguises nothing). It names the campaign, since a printed call
    /// goes to whichever campaign is current when it is sent. It keeps the state the reader knows the entity in (met,
    /// heard …): a call that changed the state would also move the row's learned session to the batch's session, and a
    /// row that said "met" would say "knows". It leaves the state out (the call's default, knows) when that is knows, or
    /// when it is unrecognized, which disguises the entity whatever its known_as says.
    /// </summary>
    private static string Learned(string campaignSlug, Reader reader, EntityState entity, KnowledgeVerdict verdict)
    {
        var keep = verdict.State is null or CV.KnowledgeStates.Knows or CV.KnowledgeStates.Unrecognized
            ? string.Empty
            : $", \"state\": \"{verdict.State}\"";
        var trueName = CampaignLogJson.Serialize(JsonValue.Create(entity.Row.Name));
        return $"or, if {reader.Key} now knows the name, record it: campaign_knowledge {{\"action\": \"record\", " +
               $"\"targets\": [\"{entity.Ref}\"], \"knowers\": [{{\"who\": \"{reader.Key}\"{keep}, \"known_as\": {trueName}}}], \"campaign\": \"{campaignSlug}\"}}";
    }

    // "the party's", "the table's"; a character by its handle: "character:serif's".
    private static string Whose(string owner) =>
        owner.StartsWith(CV.PerspectiveKinds.CharacterPrefix, StringComparison.Ordinal) ? owner + "'s" : "the " + owner + "'s";

    // say "shell" instead (or "wrapping", "what keeps it in")
    private static string SayInstead(string first, IEnumerable<string> others)
    {
        var rest = others.Select(o => $"\"{o}\"").ToList();
        return $"say \"{first}\" instead" + (rest.Count == 0 ? string.Empty : $" (or {string.Join(", ", rest)})");
    }

    private void Add(PlayerTextWarning warning) => _batch.Warnings.Add(warning);

    /// <summary>
    /// The names a text can say, indexed by their words, and for each text a scanner over only the names whose every word
    /// the text contains.
    ///
    /// <para>
    /// <b>Why a scanner per text.</b> The work runs inside the write transaction of every batch that changes player-visible
    /// text, holding the write lock, so it must not grow with the campaign: a scanner over every name, alias and known_as,
    /// rebuilt for each name-like field, made a batch of 50 new places three to four times slower once the campaign had a
    /// thousand entities. The names a text can contain are a handful, found by word lookup; the index is built once per
    /// batch, and only when the batch made some text newly readable.
    /// </para>
    /// <para>
    /// <b>A name-like field is scanned without that very name of its own entity.</b> The scanner takes the longest name at
    /// each position, so "Keras's tomb" scanned with the tomb's own name in the index is the tomb, and the "Keras" inside
    /// it is never seen. A longer text keeps every name, so an author alias quoted in the entity's own summary is still
    /// found. (A known_as needs nothing more: whatever of its entity's names it contains is part of a name its reader uses,
    /// which <see cref="Lens.Uses"/> accepts.)
    /// </para>
    /// </summary>
    private sealed class NameIndex
    {
        private readonly Dictionary<string, List<Indexed>> _byFirstWord = new(StringComparer.Ordinal);

        public NameIndex(IEnumerable<NameEntry> entries)
        {
            var order = 0;
            foreach (var entry in entries)
            {
                // The words the scanner compares: folded, without a leading article (kept when it is the whole name).
                var words = CampaignWords.Keys(entry.Surface);
                if (words.Count > 1 && CampaignWords.IsArticle(words[0]))
                {
                    words = words.Skip(1).ToList();
                }

                if (words.Count == 0)
                {
                    continue;
                }

                if (!_byFirstWord.TryGetValue(words[0], out var list))
                {
                    list = [];
                    _byFirstWord.Add(words[0], list);
                }

                list.Add(new Indexed(order++, entry, words));
            }
        }

        /// <summary>
        /// The scanner for <paramref name="text"/>, a field of <paramref name="entityId"/> (null for a fact): every name
        /// whose words all occur in the text (a superset of the names it says), in index order, less the entity's own name
        /// that is the whole text.
        /// </summary>
        public NameScanner For(string? entityId, string text)
        {
            // A word may end in a plural or possessive s the name does not have ("kings", "Keras's"): the scanner reads
            // both, so both are looked up.
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in CampaignWords.Keys(text))
            {
                present.Add(key);
                if (key.Length > 1 && key.EndsWith('s'))
                {
                    present.Add(key[..^1]);
                }
            }

            var ownKey = CampaignText.KeyWithoutArticle(text);
            var candidates = new List<Indexed>();
            foreach (var word in present)
            {
                if (_byFirstWord.TryGetValue(word, out var list))
                {
                    candidates.AddRange(list.Where(c => c.Words.All(present.Contains) && !Own(c)));
                }
            }

            candidates.Sort((a, b) => a.Order.CompareTo(b.Order));
            return new NameScanner(candidates.Select(c => c.Entry));

            bool Own(Indexed candidate) =>
                entityId is not null && ((NameReading)candidate.Entry.Payload).EntityId == entityId &&
                CampaignText.KeyWithoutArticle(candidate.Entry.Surface) == ownKey;
        }

        /// <summary>A name with its place in the index (the scanner reports entries of one name in this order) and its words.</summary>
        private sealed record Indexed(int Order, NameEntry Entry, IReadOnlyList<string> Words);
    }

    /// <summary>How a scanned name matched an entity.</summary>
    private static class NameMatches
    {
        public const string Name = "name";
        public const string Alias = "alias";
        public const string KnownAs = "known_as";
    }

    /// <summary>A name the scanner looks for: the entity it names, how (name, alias, known_as), and the alias's visibility or the known_as's knower kind.</summary>
    private sealed record NameReading(string EntityId, string MatchedAs, string? Detail);

    /// <summary>Why a name is a leak (<see cref="Flag"/>), the name to say instead, and for a true name the call that records it as known.</summary>
    private sealed record Flagged(EntityState Entity, string What, string? ShownAs, string? Learned = null);

    /// <summary>A reader's view of an entity and the verdict it came from.</summary>
    private sealed record Seen(EntityView View, KnowledgeVerdict Verdict);

    /// <summary>A target the batch noted, and what every view read of it before the batch changed it.</summary>
    private sealed record Noted(bool IsFact, string Id, int? OpIndex, HashSet<TextKey> Before);

    /// <summary>A piece of text one view reads (the key the before/after comparison uses).</summary>
    private readonly record struct TextKey(string Reader, string Field, string Text);

    /// <summary>A <see cref="TextKey"/> with the reader and the target's ref.</summary>
    private sealed record TextItem(TextKey Key, Reader Reader, string Ref);

    /// <summary>Text that is new for at least one view, with every view it is new for, in reader order.</summary>
    private sealed record NewText(Noted Target, string Ref, string Field, string Text, List<Reader> Readers);

    /// <summary>A player-side view: its key as printed (party, character:belmakor) and its verdict context.</summary>
    private sealed record Reader(string Key, PerspectiveContext Who);

    /// <summary>An entity as the lens reads it: the row, its aliases, its knowledge rows, its dated session and its ref.</summary>
    private sealed record EntityState(
        EntityRow Row,
        IReadOnlyList<(string Alias, string Visibility)> Aliases,
        IReadOnlyList<KnowledgeEntry> Entries,
        int? Since,
        string Ref,
        bool OutOfPlay)
    {
        public string Id => Row.Id;

        public bool IsSession => Row.Kind == CV.Kinds.Session;
    }

    /// <summary>
    /// What player-side views read, at one moment of the batch: one per snapshot (before the first change to each target,
    /// and once at the end), each with its own <see cref="KnowledgeLoader"/>, since a loader caches session numbers and
    /// attendance that the batch may change.
    /// </summary>
    private sealed class Lens
    {
        private readonly WriteBatch _batch;
        private readonly KnowledgeLoader _loader;
        private readonly List<Reader> _groups;
        private readonly Dictionary<string, Reader?> _characters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EntityState?> _entities = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Reader, string Entity), Seen> _views = new();
        private readonly Dictionary<(string Fact, string Reader), string> _factBases = new();
        private List<Reader>? _members;

        public Lens(WriteBatch batch)
        {
            _batch = batch;
            _loader = new KnowledgeLoader(batch.Connection, batch.Campaign, batch.Transaction);
            var kinds = new List<string> { CV.PerspectiveKinds.Party, CV.PerspectiveKinds.Table, CV.PerspectiveKinds.Public };
            if (!batch.IsDmCampaign)
            {
                kinds.Add(CV.PerspectiveKinds.Dm);
            }

            _groups = kinds.Select(k => new Reader(k, PerspectiveContext.For(Perspective.Parse(k), batch.Campaign.Role))).ToList();
        }

        public bool IsDmCampaign => _batch.IsDmCampaign;

        /// <summary>The campaign's slug, which every call a warning prints names.</summary>
        public string CampaignSlug => _batch.Campaign.Slug;

        /// <summary>What each view reads of an entity: its name, summary, body and aliases, or under a disguise its known_as and shared aliases.</summary>
        public List<TextItem> EntityItems(string entityId)
        {
            var items = new List<TextItem>();
            if (Entity(entityId) is not { OutOfPlay: false } entity)
            {
                return items;
            }

            foreach (var reader in ReadersOf(entity.Entries))
            {
                var (view, verdict) = ViewOf(reader, entity);
                if (!view.Visible)
                {
                    continue;
                }

                if (view.Disguised)
                {
                    if (verdict.KnownAs is { } knownAs && !string.IsNullOrWhiteSpace(knownAs))
                    {
                        Add(items, reader, entity.Ref, F.KnownAs, knownAs.Trim());
                    }
                }
                else
                {
                    Add(items, reader, entity.Ref, entity.IsSession ? F.Title : F.Name, entity.Row.Name);
                    Add(items, reader, entity.Ref, F.Summary, entity.Row.Summary);
                    Add(items, reader, entity.Ref, entity.IsSession ? F.Recap : F.Body, entity.Row.BodyMd);
                }

                foreach (var (alias, _) in EntityViews.ShownAliases(view, entity.Aliases))
                {
                    Add(items, reader, entity.Ref, F.Alias, alias);
                }
            }

            return items;
        }

        /// <summary>What each view reads of a fact: the deciding row's known_as, else the statement (contract §3.2).</summary>
        public List<TextItem> FactItems(string factId)
        {
            var items = new List<TextItem>();
            var fact = _batch.FactById(factId);
            if (fact is null || fact.IsDeleted || fact.Visibility == CV.Visibilities.Author ||
                KnowledgeLoader.NotInPlayCanonStatuses.Contains(fact.CanonStatus))
            {
                return items;
            }

            var entries = _loader.EntriesForFacts([factId])[factId];
            var since = _loader.EstablishedSession(fact);
            foreach (var reader in ReadersOf(entries))
            {
                var verdict = KnowledgeVerdicts.Evaluate(reader.Who, entries, fact.Visibility, _loader.Attendance, null, since);
                if (!verdict.Knows)
                {
                    continue;
                }

                _factBases[(factId, reader.Key)] = verdict.Basis;
                if (verdict.KnownAs is { } knownAs && !string.IsNullOrWhiteSpace(knownAs))
                {
                    Add(items, reader, fact.SeqHandle, F.KnownAs, knownAs.Trim());
                }
                else
                {
                    Add(items, reader, fact.SeqHandle, F.Statement, fact.Statement);
                }
            }

            return items;
        }

        /// <summary>
        /// The knower whose row shows the reader the fact's statement, whose known_as would change what it reads (the
        /// reader's own row; the party's, the table's, the dm's or the public's read for it); null when the reader reads it
        /// by its visibility alone.
        /// </summary>
        public string? PhrasingOwner(string factId, Reader reader) =>
            !_factBases.TryGetValue((factId, reader.Key), out var basis) ? null : basis switch
            {
                KnowledgeBases.Visibility => null,
                KnowledgeBases.Party or KnowledgeBases.PartyPresent or KnowledgeBases.PartyAttendanceNotRecorded => CV.KnowerKinds.Party,
                KnowledgeBases.Table => CV.KnowerKinds.Table,
                KnowledgeBases.Dm => CV.KnowerKinds.Dm,
                KnowledgeBases.Public => CV.KnowerKinds.Public,
                _ => reader.Key,
            };

        /// <summary>
        /// Whether <paramref name="surface"/> is a name the reader uses for the entity (<see cref="EntityView.UsedNames"/>),
        /// or a run of whole words of one, the last perhaps with a possessive or plural s: the party that calls him
        /// "Captain Valdris" or "Valdris's ghost" says "Valdris" too, so it is no true name the party does not use, and a
        /// known_as never leaks its own entity's names to its knower.
        /// </summary>
        public bool Uses(Reader reader, string entityId, string surface)
        {
            if (Entity(entityId) is not { } entity || ViewOf(reader, entity) is not { View: { Visible: true } view })
            {
                return false;
            }

            var words = CampaignText.KeyWithoutArticle(surface).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 && view.UsedNames.Any(used => ContainsRun(used.Split(' ', StringSplitOptions.RemoveEmptyEntries), words));
        }

        // Whether `words` occur in `used` one after another, the last as written or with an s after it ("valdriss").
        private static bool ContainsRun(string[] used, string[] words)
        {
            for (var start = 0; start + words.Length <= used.Length; start++)
            {
                var k = 0;
                while (k < words.Length && (used[start + k] == words[k] || (k == words.Length - 1 && used[start + k] == words[k] + "s")))
                {
                    k++;
                }

                if (k == words.Length)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The reader's view of an entity (hidden when it is out of play: not canon yet, or a session not yet played).</summary>
        public Seen ViewOf(Reader reader, EntityState entity)
        {
            if (!_views.TryGetValue((reader.Key, entity.Id), out var seen))
            {
                var verdict = KnowledgeVerdicts.Evaluate(reader.Who, entity.Entries, entity.Row.Visibility, _loader.Attendance, null, entity.Since);
                var view = entity.OutOfPlay
                    ? EntityView.Hidden
                    : EntityViews.For(reader.Who, entity.Row.Kind, entity.Row.Name, entity.Row.Visibility, entity.Aliases, verdict);
                seen = new Seen(view, verdict);
                _views[(reader.Key, entity.Id)] = seen;
            }

            return seen;
        }

        /// <summary>An entity of this campaign by id (deleted ones are out of play), or null.</summary>
        public EntityState? Entity(string entityId)
        {
            if (_entities.TryGetValue(entityId, out var cached))
            {
                return cached;
            }

            EntityState? state = null;
            if (_batch.EntityById(entityId) is { } row)
            {
                var aliases = _batch.Connection.Query<(string Alias, string Visibility)>(
                    "SELECT alias, visibility FROM entity_alias WHERE entity_id = @entityId ORDER BY alias", new { entityId }, _batch.Transaction).ToList();
                var entries = _loader.EntriesForEntities([entityId])[entityId];
                var session = row.Kind == CV.Kinds.Session
                    ? _batch.Connection.QueryFirstOrDefault<SessionRow>(
                        $"SELECT {SessionRow.Columns} FROM session WHERE entity_id = @entityId", new { entityId }, _batch.Transaction)
                    : null;
                var number = session is null ? (int?)null : checked((int)session.Number);
                var outOfPlay = row.IsDeleted || KnowledgeLoader.NotInPlayCanonStatuses.Contains(row.CanonStatus) ||
                                (row.Kind == CV.Kinds.Session && session?.Status is not (CV.SessionStatuses.Played or CV.SessionStatuses.Live));
                state = new EntityState(row, aliases, entries, number ?? _loader.SessionNumber(row.IntroducedSessionId),
                    number is { } n ? "session:" + WriteBatch.Number(n) : row.Handle, outOfPlay);
            }

            _entities[entityId] = state;
            return state;
        }

        /// <summary>
        /// Every live entity's name, aliases (any visibility) and known_as rows: what a text can name, and how
        /// (<see cref="NameReading"/>).
        /// </summary>
        public NameIndex Names()
        {
            var campaignId = _batch.Campaign.Id;
            var entries = new List<NameEntry>();
            entries.AddRange(_batch.Connection.Query<(string Id, string Name)>(
                    "SELECT id, name FROM entity WHERE campaign_id = @campaignId AND deleted_at IS NULL", new { campaignId }, _batch.Transaction)
                .Select(e => new NameEntry(e.Name, new NameReading(e.Id, NameMatches.Name, null))));
            entries.AddRange(_batch.Connection.Query<(string EntityId, string Alias, string Visibility)>(
                    "SELECT a.entity_id, a.alias, a.visibility FROM entity_alias a JOIN entity e ON e.id = a.entity_id " +
                    "WHERE e.campaign_id = @campaignId AND e.deleted_at IS NULL", new { campaignId }, _batch.Transaction)
                .Select(a => new NameEntry(a.Alias, new NameReading(a.EntityId, NameMatches.Alias, a.Visibility))));
            entries.AddRange(_batch.Connection.Query<(string EntityId, string KnowerKind, string KnownAs)>(
                    "SELECT k.entity_id, k.knower_kind, k.known_as FROM knowledge k JOIN entity e ON e.id = k.entity_id " +
                    "WHERE k.campaign_id = @campaignId AND k.known_as IS NOT NULL AND e.deleted_at IS NULL", new { campaignId }, _batch.Transaction)
                .Select(k => new NameEntry(k.KnownAs, new NameReading(k.EntityId, NameMatches.KnownAs, k.KnowerKind))));
            return new NameIndex(entries);
        }

        // The group views, the party's current members, and each character with a row of its own on the target.
        private IEnumerable<Reader> ReadersOf(IReadOnlyList<KnowledgeEntry> entries)
        {
            var readers = new List<Reader>(_groups);
            readers.AddRange(Members());
            foreach (var characterId in entries.Where(e => e.KnowerKind == CV.KnowerKinds.Character && e.KnowerId is not null)
                         .Select(e => e.KnowerId!).Distinct(StringComparer.Ordinal))
            {
                if (Character(characterId) is { } reader && readers.All(r => r.Key != reader.Key))
                {
                    readers.Add(reader);
                }
            }

            return readers;
        }

        private List<Reader> Members() => _members ??= _batch.Campaign.PartyId is not { } partyId
            ? []
            : _batch.Connection.Query<string>(
                    "SELECT e.id FROM relation r JOIN entity e ON e.id = r.from_id WHERE r.campaign_id = @campaignId AND r.rel = @memberOf " +
                    "AND r.to_id = @partyId AND r.status = @current AND e.kind = @character AND e.deleted_at IS NULL ORDER BY e.seq",
                    new
                    {
                        campaignId = _batch.Campaign.Id, memberOf = CV.Rels.MemberOf, partyId, current = CV.RelationStatuses.Current,
                        character = CV.Kinds.Character,
                    }, _batch.Transaction)
                .Select(Character).OfType<Reader>().ToList();

        private Reader? Character(string characterId)
        {
            if (!_characters.TryGetValue(characterId, out var reader))
            {
                reader = _batch.EntityById(characterId) is { Kind: CV.Kinds.Character, IsDeleted: false } row
                    ? new Reader(CV.PerspectiveKinds.CharacterPrefix + row.Slug,
                        new PerspectiveContext(Perspective.ForCharacter(new CampaignHandle.EntityBySlug(null, row.Slug)), _batch.Campaign.Role,
                            row.Id, row.Name, _loader.Membership(row.Id)))
                    : null;
                _characters[characterId] = reader;
            }

            return reader;
        }

        private static void Add(List<TextItem> items, Reader reader, string reference, string field, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add(new TextItem(new TextKey(reader.Key, field, text), reader, reference));
            }
        }
    }
}
