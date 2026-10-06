using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Repository.Campaign.Combat;

public sealed partial class CombatService
{
    /// <summary>The most entries one call adds (copies counted by <c>count</c>, within the simulator's 40-creature cap).</summary>
    public const int MaxEntries = 40;

    /// <summary>
    /// <c>prepare {name, lair?, edition?, combatants?}</c> (contract §6.1): a PLANNED fight (status <c>planned</c>, round 0),
    /// its combatants added now and the party not added. Its sheet-seeded combatants are re-seeded from their sheets when
    /// it starts (the sheets may change before then). A planned fight is never shown to a non-author view (§6.12).
    /// </summary>
    /// <exception cref="DndInputException">A bad or taken name, an edition missing in a mixed campaign, or a refused entry.</exception>
    public CombatOutcome Prepare(CampaignRow campaign, PrepareRequest request)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(request);
        CheckEntries(request.Combatants);
        var (subjects, warnings) = PreRead(campaign, null, (c, _) => EntrySubjects(c, campaign, request.Combatants), (_, _) => EntryTexts(request.Combatants));
        return Write(campaign, (connection, transaction, current) =>
        {
            var name = EncounterResolver.CheckNewName(connection, current, request.Name, transaction);
            var edition = Edition(current, request.Edition);
            var at = _database.Now();
            var id = CampaignDatabase.NewId();
            connection.Execute(
                "INSERT INTO encounter (id, campaign_id, name, ruleset, status, round, lair, created_at, updated_at) " +
                "VALUES (@id, @campaignId, @name, @edition, @planned, 0, @lair, @at, @at)",
                new { id, campaignId = current.Id, name, edition, planned = ES.Planned, lair = request.Lair ? 1 : 0, at },
                transaction);
            var row = CombatStore.Encounter(connection, id, transaction)!;
            var run = new CombatRun(connection, transaction, current, row, CombatStore.Load(connection, current, row, transaction), at, subjects, _roller);
            run.Lines.Add($"Prepared \"{name}\" ({edition}{(request.Lair ? ", in a lair" : string.Empty)}): start it with combat {{\"action\": \"start\", \"encounter\": {EncounterResolver.Json(name)}, \"campaign\": \"{current.Slug}\"}}.");
            if (request.Combatants.Count > 0)
            {
                AddEntries(run, request.Combatants);
            }

            return run.Outcome(CombatActions.Prepare, warnings, created: true);
        });
    }

    /// <summary>
    /// <c>start</c> (contract §6.1): a new ACTIVE fight (round 0, filed under the live session if any), or the planned (or
    /// paused) fight named by <see cref="StartRequest.Encounter"/> or <see cref="StartRequest.Name"/> made active, its
    /// sheet-seeded combatants re-seeded from their sheets as they are NOW (state and snapshot, D19), all in one
    /// transaction; then <c>add_party</c> (every current party member, D8, not already in the fight, in name order:
    /// sheet-seeded, or with no hit points when it has no sheet), the call's combatants, and the surprised. Refused while
    /// another fight is active (D13: checked inside the transaction; the unique index's refusal mapped to the same words).
    /// </summary>
    /// <exception cref="DndInputException">Another fight is active, a bad name or edition, the named fight is running or ended, or a refused entry.</exception>
    public CombatOutcome Start(CampaignRow campaign, StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(request);
        CheckEntries(request.Combatants);
        var (subjects, warnings) = PreRead(campaign, null, (c, _) => EntrySubjects(c, campaign, request.Combatants), (_, _) => EntryTexts(request.Combatants));
        return Write(campaign, (connection, transaction, current) =>
        {
            var at = _database.Now();
            var existing = Activatable(connection, transaction, current, request);
            EncounterResolver.RequireNoActive(connection, current, transaction, existing?.Id);
            var live = connection.QueryFirstOrDefault<string?>(
                "SELECT entity_id FROM session WHERE campaign_id = @campaignId AND status = @live LIMIT 1",
                new { campaignId = current.Id, live = CampaignValues.SessionStatuses.Live }, transaction);
            BeforeActivate?.Invoke(connection, transaction);
            EncounterRow row;
            var created = existing is null;
            try
            {
                row = existing is null
                    ? Create(connection, transaction, current, request, live, at)
                    : Activate(connection, transaction, current, existing, request, live, at);
            }
            catch (SqliteException ex) when (EncounterResolver.IsActiveConflict(ex))
            {
                var active = EncounterResolver.Active(connection, current.Id, transaction);
                throw EncounterResolver.ActiveRefusal(current, active?.Name ?? "another fight");
            }

            var run = new CombatRun(connection, transaction, current, row, CombatStore.Load(connection, current, row, transaction), at, subjects, _roller);
            var startDetail = new JsonObject { ["name"] = row.Name, ["ruleset"] = row.Ruleset, ["lair"] = row.Lair != 0 };
            if (!created)
            {
                startDetail["from"] = existing!.Status;
            }

            CombatStore.AppendLog(connection, transaction, row.Id,
                new CombatChange(L.Start, 0, null, null, null, null, CombatJson.Serialize(startDetail)), null, at);
            run.Lines.Add(created
                ? $"Started \"{row.Name}\" ({row.Ruleset}{(row.Lair != 0 ? ", in a lair" : string.Empty)}): round 0; roll initiative to begin round 1."
                : $"Started the {existing!.Status} fight \"{row.Name}\" ({row.Ruleset}{(row.Lair != 0 ? ", in a lair" : string.Empty)}).");
            if (existing is { Status: ES.Planned })
            {
                Reseed(run);
            }

            if (request.AddParty)
            {
                AddParty(run);
            }

            if (request.Combatants.Count > 0)
            {
                AddEntries(run, request.Combatants);
            }

            if (request.Surprised.Count > 0)
            {
                run.Apply(new SurpriseOp(request.Surprised));
            }

            return run.Outcome(CombatActions.Start, warnings, created);
        });
    }

    /// <summary>
    /// <c>add {combatants}</c> (contract §6.2) to the fight <paramref name="encounter"/> names (a planned one too): each
    /// entry's character resolved and its sheet read in this transaction (sheet-seeded iff a character with a sheet and no
    /// stat block, D19), <c>hp: "roll"</c> rolled and logged as "hit points", the D20b warning for a typed name the party
    /// view would not show.
    /// </summary>
    /// <exception cref="DndInputException">The encounter is not found or has ended, a cross-campaign or unknown character, or a refused entry.</exception>
    public CombatOutcome Add(CampaignRow campaign, string? encounter, IReadOnlyList<CombatantRequest> combatants)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(combatants);
        CheckEntries(combatants);
        if (combatants.Count == 0)
        {
            throw new DndInputException("combatants is empty; give a list of combatants to add, e.g. [{\"srd\": \"2014/monster/mummy\", \"count\": 2}].");
        }

        var (subjects, warnings) = PreRead(campaign, null, (c, _) => EntrySubjects(c, campaign, combatants), (_, _) => EntryTexts(combatants));
        return Write(campaign, (connection, transaction, current) =>
        {
            var row = EncounterResolver.Resolve(connection, current, encounter, transaction);
            var run = new CombatRun(connection, transaction, current, row, CombatStore.Load(connection, current, row, transaction), _database.Now(), subjects, _roller);
            AddEntries(run, combatants);
            return run.Outcome(CombatActions.Add, warnings);
        });
    }

    // -------------------------------------------------------------------------------------------------------------------
    // start's parts

    // The planned or paused fight a start names, or null for a new one; refused for one that runs or has ended.
    private static EncounterRow? Activatable(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, StartRequest request)
    {
        EncounterRow? found = null;
        if (!string.IsNullOrWhiteSpace(request.Encounter))
        {
            found = EncounterResolver.Resolve(connection, campaign, request.Encounter, transaction);
        }
        else if (!string.IsNullOrWhiteSpace(request.Name))
        {
            found = EncounterResolver.ByName(connection, campaign.Id, request.Name, transaction) is { Status: not ES.Ended } match ? match : null;
        }

        if (found is null)
        {
            return null;
        }

        return found.Status switch
        {
            ES.Planned or ES.Paused => found,
            ES.Active => throw new DndInputException(
                $"\"{WriteBatch.Echo(found.Name)}\" is already running (round {CombatStore.N(found.Round)}); combat {{\"action\": \"state\", \"campaign\": \"{campaign.Slug}\"}} shows it."),
            _ => throw new DndInputException(
                $"\"{WriteBatch.Echo(found.Name)}\" has ended and cannot run again; start a new fight with name (an ended fight's name may be used again)."),
        };
    }

    private static EncounterRow Create(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, StartRequest request, string? live, string at)
    {
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? DefaultName(connection, transaction, campaign)
            : EncounterResolver.CheckNewName(connection, campaign, request.Name, transaction);
        var edition = Edition(campaign, request.Edition);
        var id = CampaignDatabase.NewId();
        connection.Execute(
            "INSERT INTO encounter (id, campaign_id, session_id, name, ruleset, status, round, lair, started_at, created_at, updated_at) " +
            "VALUES (@id, @campaignId, @live, @name, @edition, @active, 0, @lair, @at, @at, @at)",
            new { id, campaignId = campaign.Id, live, name, edition, active = ES.Active, lair = request.Lair == true ? 1 : 0, at },
            transaction);
        return CombatStore.Encounter(connection, id, transaction)!;
    }

    // A planned or paused fight made active. A changed lair re-counts the stat blocks' legendary uses (none spent yet:
    // only a planned fight's, which has not run); a paused fight keeps its counts and state.
    private static EncounterRow Activate(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, EncounterRow existing,
        StartRequest request, string? live, string at)
    {
        if (!string.IsNullOrWhiteSpace(request.Edition) && Edition(campaign, request.Edition) != existing.Ruleset)
        {
            throw new DndInputException(
                $"edition: \"{WriteBatch.Echo(existing.Name)}\" was prepared for {existing.Ruleset} (its stat blocks are {existing.Ruleset}'s); leave edition out, or prepare another fight.");
        }

        var lair = request.Lair is { } given ? (given ? 1L : 0L) : existing.Lair;
        connection.Execute(
            "UPDATE encounter SET status = @active, session_id = coalesce(@live, session_id), lair = @lair, " +
            "started_at = coalesce(started_at, @at), updated_at = @at WHERE id = @id",
            new { active = ES.Active, live, lair, at, id = existing.Id },
            transaction);
        var row = CombatStore.Encounter(connection, existing.Id, transaction)!;
        if (existing.Status == ES.Planned && lair != existing.Lair)
        {
            var state = CombatStore.Load(connection, campaign, row, transaction);
            var recounted = state with
            {
                Combatants = state.Combatants.Select(c => c.StatBlock is { } block && c.Legendary is not null
                    ? c with
                    {
                        Legendary = new LegendaryState(
                            StatBlockFacts.LegendaryActionUses(block, lair != 0), 0, StatBlockFacts.LegendaryResistanceUses(block, lair != 0), 0),
                    }
                    : c).ToList(),
            };
            CombatStore.WriteCombatants(connection, transaction, state, recounted, EncounterState.ChangedCombatants(state, recounted), at);
        }

        return row;
    }

    // "Fight N": the first number no encounter of the campaign is named with.
    private static string DefaultName(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign)
    {
        var taken = EncounterResolver.All(connection, campaign.Id, transaction).Select(e => CampaignText.Key(e.Name)).ToHashSet(StringComparer.Ordinal);
        for (var n = taken.Count + 1; ; n++)
        {
            var name = "Fight " + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(CampaignText.Key(name)))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// D19 at <c>start</c> of a planned fight: every character combatant played without a stat block takes its state and
    /// snapshot from its sheet as it is NOW (<see cref="CombatantFactory.Reseed"/>), keeping its place in the fight; one
    /// whose sheet no longer exists keeps what it was prepared with (and <c>end</c> reports the missing sheet). Each
    /// re-seed is a state change, so it gets its combat_log row (kind <c>add</c>, <c>detail.reseeded</c>).
    /// </summary>
    private static void Reseed(CombatRun run)
    {
        var sheets = new Dictionary<string, CharacterSheet>(StringComparer.Ordinal);
        foreach (var c in run.State.Combatants)
        {
            if (c.EntityId is not null && c.StatBlock is null && CharacterSheetStore.Read(run.Connection, c.EntityId, run.Transaction) is { } sheet)
            {
                sheets[c.Id] = sheet;
            }
            else if (c.IsSheetSeeded)
            {
                run.Lines.Add($"{c.Name}: its sheet no longer exists; it keeps the state it was prepared with (end will report the missing sheet).");
            }
        }

        ReseedFromSheets(run, sheets, inFight: false);
    }

    /// <summary>
    /// Re-seeds the given combatants (combatant id → its character's sheet as read now) from their sheets
    /// (<see cref="CombatantFactory.Reseed"/>: state and snapshot from the sheet, keeping its place, name, side, initiative
    /// and whether it left), each a state change with its combat_log row (kind <c>add</c>, <c>detail.reseeded</c>) and a
    /// line. Shared by a planned fight's start and by <c>add {character}</c> for a combatant that joined without a sheet;
    /// the latter (<paramref name="inFight"/>) keeps what the fight gave the combatant (<see cref="ReseedInFight"/>).
    /// </summary>
    private static void ReseedFromSheets(CombatRun run, IReadOnlyDictionary<string, CharacterSheet> sheets, bool inFight)
    {
        if (sheets.Count == 0)
        {
            return;
        }

        var before = run.State;
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var after = before with
        {
            Combatants = before.Combatants.Select(c =>
            {
                if (!sheets.TryGetValue(c.Id, out var sheet))
                {
                    return c;
                }

                var seeded = CombatantFactory.Reseed(c, sheet, before.Ruleset, before.Round);
                if (!inFight)
                {
                    return seeded with { Removed = c.Removed };
                }

                var (merged, note) = ReseedInFight(c, seeded, before.Ruleset);
                if (note is not null)
                {
                    kept[c.Id] = note;
                }

                return merged;
            }).ToList(),
        };
        CombatStore.WriteCombatants(run.Connection, run.Transaction, before, after, EncounterState.ChangedCombatants(before, after), run.At);
        foreach (var c in after.Combatants.Where(c => sheets.ContainsKey(c.Id)))
        {
            CombatStore.AppendLog(run.Connection, run.Transaction, after.Id, new CombatChange(L.Add, after.Round, after.TurnCombatantId, c.Id, c.Id, c.Hp,
                CombatJson.Serialize(new JsonObject { ["name"] = c.Name, ["reseeded"] = true, ["sheet_seeded"] = true })), null, run.At);
            run.Lines.Add(kept.TryGetValue(c.Id, out var note)
                ? $"{c.Name}: re-seeded from its sheet as it is now; {note}."
                : $"{c.Name}: re-seeded from its sheet as it is now.");
        }

        run.Use(after);
    }

    /// <summary>
    /// The combatant a RUNNING fight re-seeds (F2, review CR01): from its sheet (<paramref name="seeded"/>, the factory's
    /// re-seed) only what the unseeded combatant lacked (hit points, maximum and reduction, temporary hit points, AC, death
    /// saves, exhaustion, resources and slots, and the snapshot the write-back compares with), and everything the fight
    /// gave it kept from <paramref name="fought"/>: its place (initiative, the init bonus once it has an initiative it
    /// keeps, order, side, hidden, surprised, left), its reaction, every condition on it from any source, its concentration
    /// (which wins over the sheet's) and so everything that concentration holds on others, and the effects it sources (on
    /// other combatants, untouched). Taking the whole combatant from the sheet dropped its concentration while the paralysis
    /// it held stayed on the goblin, orphaned: dropping the concentration was refused, damage prompted no save, leaving did
    /// not end the hold, and the from_state simulation refused the fight.
    /// <para>
    /// <b>Hit points the fight tracks are the fight's</b> (F3, review F2R03): a combatant that <c>set</c> gave hit points
    /// (and the damage since, down to 0 and dying) keeps them, at most the sheet's effective maximum, and the line says what
    /// the sheet said; one that is down keeps its down state and its death-save tallies (the sheet's full hit points no
    /// longer leave it unconscious and prone at 44/44, a creature that never acts and that no heal wakes). The sheet's own
    /// down state (Unconscious <c>zero_hp</c>, Prone <c>until_stands</c>) comes in only with the sheet's hit points.
    /// </para>
    /// <para>
    /// <b>The sheet's persisting conditions</b> join only where the combatant lacks one of that name. One the fight's
    /// condition shadows ("poisoned (Goblin 2), 1 minute" over the sheet's "poisoned, until removed") is named in the
    /// snapshot (<see cref="SheetSnapshot.Shadowed"/>), so the write-back keeps it on the sheet as stored (F3, review F2R03:
    /// it was dropped from the sheet at <c>end</c>).
    /// </para>
    /// </summary>
    /// <returns>The merged combatant, and the line's addition when the fight's hit points were kept (else null).</returns>
    private static (CombatantState Combatant, string? Note) ReseedInFight(CombatantState fought, CombatantState seeded, string edition)
    {
        var keepHp = fought.Hp is not null;
        var conditions = fought.Conditions.ToList();
        var shadowed = new List<string>();
        foreach (var condition in seeded.Conditions)
        {
            if (keepHp && IsDownState(condition))
            {
                continue;
            }

            if (fought.Conditions.Any(c => string.Equals(c.Name, condition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (condition.Duration is CombatValues.Durations.UntilRemoved or CombatValues.Durations.Rounds &&
                    !shadowed.Contains(condition.Name, StringComparer.OrdinalIgnoreCase))
                {
                    shadowed.Add(condition.Name);
                }

                continue;
            }

            conditions.Add(condition with { Id = NextConditionId(conditions) });
        }

        var merged = seeded with
        {
            Conditions = conditions,
            Concentration = fought.Concentration ?? seeded.Concentration,
            InitBonus = fought.Initiative is null || fought.Removed ? seeded.InitBonus : fought.InitBonus,
            ReactionUsed = fought.ReactionUsed,
            Removed = fought.Removed,
            Defeated = fought.Defeated,
            Dead = fought.Dead || seeded.Dead,
            SheetSnapshot = shadowed.Count == 0 ? seeded.SheetSnapshot : seeded.SheetSnapshot! with { Shadowed = shadowed },
        };
        if (!keepHp)
        {
            return (merged, null);
        }

        // The fight's hit points, within the sheet's maximum (the sheet with none: the fight's maximum too, D17).
        var sheetSays = seeded.MaxHp is null
            ? "the sheet has no max_hp"
            : $"the sheet says {CombatStore.N(seeded.Hp ?? 0)}/{CombatStore.N(seeded.EffectiveMaxHp(edition) ?? 0)}";
        var hp = fought.Hp!.Value;
        if (seeded.MaxHp is null)
        {
            merged = merged with { Hp = hp, MaxHp = fought.MaxHp, MaxHpReduction = fought.MaxHpReduction, DeathSaves = fought.DeathSaves };
        }
        else
        {
            var cap = merged.EffectiveMaxHp(edition) ?? hp;
            merged = merged with { Hp = Math.Min(hp, cap), DeathSaves = fought.DeathSaves };
        }

        var cut = merged.Hp != hp ? $", cut from {CombatStore.N(hp)} to the sheet's maximum" : string.Empty;
        var down = merged.Hp == 0 && !merged.Dead ? $": it stays down ({CombatContext.Tallies(merged.DeathSaves)})" : string.Empty;
        return (merged, $"kept the fight's HP {CombatStore.N(merged.Hp!.Value)}{cut} ({sheetSays}){down}");
    }

    // The tracker's own down state, which a sheet at 0 HP seeds (CombatantFactory.FromSheet): Unconscious until hit points
    // rise above 0 (a knock-out's too) and Prone until it stands. It goes with the sheet's hit points, never the fight's.
    private static bool IsDownState(CombatCondition condition) =>
        condition.Duration == CombatValues.Durations.ZeroHp ||
        (condition.Duration == CombatValues.Durations.UntilStands && string.Equals(condition.Name, Domain.Simulation.StatBlockValues.Conditions.Prone, StringComparison.Ordinal));

    // The next unused condition id among a combatant's conditions ("c4"), as the tracker numbers them.
    private static string NextConditionId(IReadOnlyList<CombatCondition> conditions)
    {
        var max = 0;
        foreach (var condition in conditions)
        {
            if (condition.Id.Length > 1 && condition.Id[0] == 'c' &&
                int.TryParse(condition.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return "c" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    // add_party (D8): the current members not already in the fight, in name order; the dead and departed named in a note.
    private static void AddParty(CombatRun run)
    {
        var roster = PartyRoster.Read(run.Connection, run.Campaign, null, run.Transaction);
        var inFight = run.State.Combatants.Select(c => c.EntityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var entries = roster.Members.Where(m => !inFight.Contains(m.EntityId)).Select(m => new AddEntry
        {
            EntityId = m.EntityId,
            EntityName = m.Name,
            EntityHandle = m.Handle,
            EntitySubtype = m.Subtype,
            Sheet = m.Sheet,
        }).ToList();
        if (roster.Excluded.Count > 0)
        {
            run.Lines.Add("Not added: " + string.Join(", ", roster.Excluded.Select(x => $"{x.Name} ({x.Reason})")) + ".");
        }

        if (entries.Count == 0)
        {
            if (roster.Members.Count == 0)
            {
                run.Lines.Add(roster.PartyRef is { } party
                    ? $"The party has no current members to add (link characters member_of {party})."
                    : "The campaign has no party to add.");
            }

            return;
        }

        var without = entries.Where(e => e.Sheet is null).ToList();
        if (without.Count > 0)
        {
            // The way to seed them later (review C04): a sheet, then add {character} again, which re-seeds the combatant
            // already in the fight from it; or hit points of its own with set.
            var add = CombatCalls.Combat(CombatActions.Add, ("combatants", without.Select(e => CombatCalls.Object(("character", e.EntityHandle))).ToArray()));
            run.Lines.Add($"No sheet, so no hit points: {string.Join(", ", without.Select(e => e.EntityName!))}. Give each a sheet with " +
                          $"campaign_character update, then seed it into this fight with {WithCampaign(add, run.Campaign.Slug)}; or give hp with combat set.");
        }

        run.Apply(new AddOp(entries, entries.Select(_ => CampaignDatabase.NewId()).ToList()));
    }

    /// <summary>
    /// The call's combatants as tracker add entries (characters resolved, sheets read), applied as one add. A character
    /// already in the fight WITHOUT a sheet (added by <c>start</c> before it had one, D8) that has one now is re-seeded from
    /// it instead (review C04: otherwise nothing could seed it, its fight would never reach its sheet, and the encounter
    /// simulation would keep leaving it out); one that left is re-seeded and re-joins.
    /// </summary>
    private static void AddEntries(CombatRun run, IReadOnlyList<CombatantRequest> inputs)
    {
        var entries = new List<AddEntry>();
        var ids = new List<string>();
        var reseeds = new Dictionary<string, CharacterSheet>(StringComparer.Ordinal);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var entry = Entry(run.Connection, run.Transaction, run.Campaign, input, i);
            if (Unseeded(run.State, entry) is { } joined)
            {
                RequireOnlyTheCharacter(input, joined, i);
                reseeds[joined.Id] = entry.Sheet!;
                if (!joined.Removed)
                {
                    continue; // re-seeded below; nothing to add
                }
            }

            entries.Add(entry);
            if (entry.EntityId is { } entityId && run.State.Combatants.Any(c => c.EntityId == entityId))
            {
                continue; // a re-join takes no new id
            }

            var side = entry.Side is { } typed && S.Set.TryMatch(typed, out var canonical) ? canonical : S.Enemy;
            for (var n = 0; n < Math.Clamp(entry.Count, 1, CombatTracker.MaxCount); n++)
            {
                var id = CampaignDatabase.NewId();
                ids.Add(id);
                run.ExpectNew(id, new RollSubject(CombatSubjects.EntryKey(i), side, entry.Hidden, entry.EntityId));
            }
        }

        ReseedFromSheets(run, reseeds, inFight: true);
        if (entries.Count > 0)
        {
            run.Apply(new AddOp(entries, ids));
        }
    }

    // The combatant an entry re-seeds (AddEntries): its character, played without a stat block and now with a sheet, is
    // already in the fight as a combatant with neither (it joined before it had a sheet); null for any other entry.
    private static CombatantState? Unseeded(EncounterState state, AddEntry entry) =>
        entry is { EntityId: { } entityId, Monster: null, Sheet: not null }
            ? state.Combatants.FirstOrDefault(c => c.EntityId == entityId && c.StatBlock is null && !c.IsSheetSeeded)
            : null;

    // A re-seed takes everything from the sheet; anything else the entry gives is refused, never dropped in silence.
    private static void RequireOnlyTheCharacter(CombatantRequest input, CombatantState joined, int index)
    {
        if (input.Name is not null || input.Count != 1 || input.Hp is not null || input.Ac is not null || input.InitBonus is not null ||
            input.Side is not null || input.Hidden || input.DeathSaves is not null)
        {
            throw new DndInputException(
                $"combatants item {CombatStore.N(index + 1)}: {joined.EntityHandle ?? joined.Name} is in the fight as {joined.Name} with no sheet; " +
                "adding it again re-seeds it from the sheet it has now, which gives all of it: give only character (change the rest with set afterwards).");
        }
    }

    // A call with the campaign named last (the tracker's calls name none: a printed call goes to whichever campaign is current).
    private static string WithCampaign(string call, string slug) => call[..^1] + $", \"campaign\": \"{slug}\"}}";

    // One input as a tracker entry: the character resolved in this transaction (refusals numbered by item), its sheet read
    // when it is played without a stat block (sheet seeding, D19).
    private static AddEntry Entry(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, CombatantRequest input, int index)
    {
        var entry = new AddEntry
        {
            Monster = input.Monster,
            Name = input.Name,
            Count = input.Count,
            Hp = input.Hp,
            Ac = input.Ac,
            InitBonus = input.InitBonus,
            Side = input.Side,
            Hidden = input.Hidden,
            DeathSaves = input.DeathSaves,
        };
        if (input.Character is null)
        {
            return entry;
        }

        var where = $"combatants item {CombatStore.N(index + 1)}";
        if (string.IsNullOrWhiteSpace(input.Character))
        {
            throw new DndInputException($"{where}: character is blank; give a character's handle, e.g. \"character:torch\".");
        }

        EntityRow entity;
        try
        {
            entity = CharacterLookup.Resolve(connection, campaign, input.Character, transaction);
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{where}: {ex.Message}");
        }

        return entry with
        {
            EntityId = entity.Id,
            EntityName = entity.Name,
            EntityHandle = entity.Handle,
            EntitySubtype = entity.Subtype,
            Sheet = input.Monster is null ? CharacterSheetStore.Read(connection, entity.Id, transaction) : null,
        };
    }

    // The safe-name subjects of the call's entries (their hit-point rolls' labels), resolved on the read connection; a
    // character that does not resolve is left to the transaction's refusal.
    private static IReadOnlyList<SafeNameSubject>? EntrySubjects(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<CombatantRequest> inputs)
    {
        if (inputs.All(i => i.Monster is null || i.Hp?.Kind != CombatValues.HpChoices.Roll))
        {
            return null;
        }

        var subjects = new List<SafeNameSubject>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            string? entityId = null;
            if (!string.IsNullOrWhiteSpace(input.Character))
            {
                try
                {
                    entityId = CharacterLookup.Resolve(connection, campaign, input.Character).Id;
                }
                catch (DndInputException)
                {
                    // Refused inside the transaction, with the item's number.
                }
            }

            subjects.Add(new SafeNameSubject(
                CombatSubjects.EntryKey(i),
                EntityId: entityId,
                MonsterName: input.Monster?.Name,
                Name: input.Monster is null && entityId is null ? input.Name : null,
                Side: input.Side is { } typed && S.Set.TryMatch(typed, out var canonical) ? canonical : S.Enemy));
        }

        return subjects;
    }

    // The D20b typed names of the call's entries: a typed name over a stat block or a custom name, never a character's
    // (a linked combatant is named by the view's own name for its entity).
    private static IReadOnlyList<TypedText> EntryTexts(IReadOnlyList<CombatantRequest> inputs) =>
        inputs.Where(i => i.Character is null && !string.IsNullOrWhiteSpace(i.Name) &&
                          (i.Monster is null || CampaignText.Key(i.Name) != CampaignText.Key(i.Monster.Name)))
            .Select(i => new TypedText(i.Name!.Trim(), "this combatant", i.Monster?.Name ?? BoardNames.UnknownCreature))
            .ToList();

    private static void CheckEntries(IReadOnlyList<CombatantRequest> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count > MaxEntries)
        {
            throw new DndInputException($"combatants has {CombatStore.N(inputs.Count)} entries; at most {CombatStore.N(MaxEntries)} per call (count makes copies).");
        }
    }
}
