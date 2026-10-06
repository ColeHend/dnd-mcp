using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// <c>balance_simulate {encounter, from_state?}</c>'s feed (contract §6.11, §14.5): the encounter read on a read connection
/// with the CURRENT sheets of its sheet-seeded combatants (and, for a planned fight with no party combatants, the
/// campaign's current party, D8), turned into the simulator's entries by T's <see cref="TrackerSimulation.Build"/>.
///
/// <para>
/// <b>What the host does with it.</b> It appends the call's explicit <c>party</c>/<c>enemies</c> (never with
/// <c>from_state</c>), then refuses a side left empty with <see cref="TrackerSimulation.RequireBothSides"/> (a side may come
/// back empty here: "what if they meet X" is a prepared fight plus <c>enemies</c> in the call), sets
/// <see cref="SimulationSpec.Lair"/> from <see cref="EncounterSimulation.Lair"/> and <see cref="SimulationSpec.Resume"/> from
/// <see cref="EncounterSimulation.Resume"/>, and prints <see cref="EncounterSimulation.Notes"/> as its own section BEFORE the
/// report: the report itself must equal the equivalent explicit call's byte for byte (§6.11), so nothing only the
/// encounter form knows (who was excluded and why, the neutral and left combatants, where the party came from) may reach
/// it. <see cref="EncounterSimulation.Assumptions"/> are the lines the explicit <c>character</c> call gets too.
/// </para>
/// <para>
/// <b>Store failures</b> become the store message (<see cref="CampaignDatabase.TryMapUnavailable"/>), never the generic
/// error; a sheet or combatant this version cannot read is a <see cref="CampaignStoreUnavailableException"/> naming the
/// file and the column. Refusals (no such fight, <c>from_state</c> before round 1, an enemy with unknown hit points under
/// <c>from_state</c>) are the author's: <c>balance_simulate</c> is an author tool, so they may list encounter names.
/// </para>
/// </summary>
public sealed class EncounterSimulationLoader
{
    private readonly CampaignDatabase _database;

    /// <param name="database">campaigns.db (read connections only).</param>
    public EncounterSimulationLoader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>The encounter <paramref name="encounter"/> names ("current" by default, "last", or a name) as a simulation.</summary>
    /// <param name="fromState">Resume the live state (an active fight in round 1 or later).</param>
    /// <param name="rulings">The rulings the run will use (they decide which names a build knows, §6.11).</param>
    /// <exception cref="DndInputException">No such fight, <c>from_state</c> on a fight that is not running, or an enemy with unknown hit points under <c>from_state</c>.</exception>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db, a combatant row or a sheet cannot be read.</exception>
    public EncounterSimulation Load(CampaignRow campaign, string? encounter = null, bool fromState = false, RulingsSpec? rulings = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        try
        {
            using var connection = ReadConnection.Open(_database);
            var row = EncounterResolver.Resolve(connection, campaign, encounter);
            var state = CombatStore.Load(connection, campaign, row);
            var seeded = state.Combatants.Where(c => c.IsSheetSeeded && c.EntityId is not null && !c.Removed).Select(c => c.EntityId!).ToList();
            var sheets = CharacterSheetStore.ReadMany(connection, seeded);
            IReadOnlyList<PartyMemberSheet> party = [];
            var notes = new List<string>();
            if (!fromState && row.Status == ES.Planned && !state.Combatants.Any(c => c.Side is S.Party or S.Ally && !c.Removed))
            {
                var roster = PartyRoster.Read(connection, campaign);
                party = roster.Members.Select(m => new PartyMemberSheet(m.EntityId, m.Name, m.Sheet)).ToList();
                if (roster.Excluded.Count > 0)
                {
                    notes.Add("Not in the party any more: " + string.Join(", ", roster.Excluded.Select(x => $"{x.Name} ({x.Reason})")) + ".");
                }
            }

            var built = TrackerSimulation.Build(state, new TrackerSimulationInputs
            {
                FromState = fromState,
                Sheets = sheets,
                CampaignParty = party,
                Rulings = rulings,
                CampaignSlug = campaign.Slug,
            });
            notes.InsertRange(0, built.Notes);
            return new EncounterSimulation(row.Name, state.Ruleset, row.Status, built.Party, built.Enemies, built.Resume, built.Lair, notes, built.Assumptions);
        }
        catch (SqliteException ex) when (CampaignDatabase.TryMapUnavailable(ex, _database.Path, out var unavailable))
        {
            throw unavailable;
        }
    }
}
