using Dapper;
using DndMcp.Domain.Campaign;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// Whose name a dice label carries (contract §6.10): one subject of a roll, as the roller knows it. Exactly one of the
/// three forms applies, checked in this order.
/// </summary>
/// <param name="Key">The caller's key for the subject (a combatant id, a character id): the result is keyed by it.</param>
/// <param name="EntityId">An entity-linked subject (a sheet's character, a combatant added with <c>character</c>).</param>
/// <param name="MonsterName">The stat block snapshot's monster name ("Lich", "Mummy"), when the subject has one.</param>
/// <param name="Name">A custom combatant's own name (no entity, no stat block).</param>
/// <param name="Side">A custom combatant's side (<see cref="CV.CombatSides"/>): only a party or ally name can be kept.</param>
public sealed record SafeNameSubject(string Key, string? EntityId = null, string? MonsterName = null, string? Name = null, string? Side = null);

/// <summary>
/// The names a session's dice labels may carry (contract §6.10, D11): a label is printed to every view that reads the
/// session's dice (non-secret rolls are listed to the party, the table, the public when it may see the session, the dm of
/// a player campaign and each party member's own view), so the name in it must be one ALL of them may read. Computed on a
/// read connection BEFORE the roll's transaction (a <see cref="ReadScope"/> cannot run inside one); the combat layer
/// computes every combatant's name in one pass and picks the subject's inside its transaction, and the character writer
/// names a rest's Hit Dice this way.
///
/// <para>
/// <b>The rules</b>, per subject:
/// </para>
/// <list type="bullet">
/// <item><b>Entity-linked:</b> the party's display name for it, kept only when the party sees it undisguised and no other
/// reader sees it disguised (a party member's own known_as for it, an unrecognised entry: that reader would learn the true
/// name from the label); otherwise the stat block's monster name, else the fallback ("a combatant", or "a character" for a
/// sheet's own roll). An entity the party cannot see at all falls back too: the label must not introduce it.</item>
/// <item><b>Stat block (srd) with no entity:</b> the snapshot's monster name, whatever name the author typed ("Keras" typed
/// over a lich is "Lich" in every label, A-L2).</item>
/// <item><b>Custom:</b> its own name when it is on the party's or an ally's side and passes the party's view-text check
/// (<see cref="ViewTextCheck"/>) with no possible inventions (a name the campaign has never recorded is one no reader can
/// be shown to know); otherwise the fallback. The name is checked as typed AND with every word capitalised ("third
/// silence" as "Third Silence"): the check lists only capitalised words as possible inventions or partial names, so a
/// name typed in lower case ("keras", "cage guard", "fleet captain") would otherwise skip the very test this rule exists
/// for. Both forms must pass with no possible invention.</item>
/// </list>
/// <para>
/// The readers are wider than the contract's list by one view, deliberately: <c>public</c>, which reads a session whose
/// entity is public. A reader that cannot see the entity at all never forces the fallback (it cannot be shown a disguise).
/// </para>
/// </summary>
public static class SessionSafeNames
{
    /// <summary>The stand-in for a combatant whose name no reader may be shown (contract §6.10).</summary>
    public const string CombatantFallback = "a combatant";

    /// <summary>The stand-in for a character whose sheet rolled (a rest's Hit Dice, D11).</summary>
    public const string CharacterFallback = "a character";

    /// <summary>The safe name of every subject, keyed by <see cref="SafeNameSubject.Key"/>.</summary>
    /// <exception cref="Domain.Core.DndInputException">Never for valid subjects (a reader's perspective always resolves).</exception>
    public static IReadOnlyDictionary<string, string> For(
        SqliteConnection connection,
        CampaignRow campaign,
        IReadOnlyList<SafeNameSubject> subjects,
        string fallback = CombatantFallback)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (subjects.Count == 0)
        {
            return names;
        }

        var scope = ReadScope.Open(connection, campaign, Perspective.Author, null);
        var party = scope.Loader.Resolve(Perspective.Parse(CV.PerspectiveKinds.Party));
        var readers = Readers(scope);
        scope.LoadEntities(subjects.Select(s => s.EntityId).OfType<string>());

        var customs = new List<SafeNameSubject>();
        foreach (var subject in subjects)
        {
            if (subject.EntityId is { } entityId)
            {
                names[subject.Key] = EntityName(scope, party, readers, entityId) ?? Clean(subject.MonsterName) ?? fallback;
            }
            else if (Clean(subject.MonsterName) is { } monster)
            {
                names[subject.Key] = monster;
            }
            else if (Clean(subject.Name) is not null && subject.Side is CV.CombatSides.Party or CV.CombatSides.Ally)
            {
                customs.Add(subject);
            }
            else
            {
                names[subject.Key] = fallback;
            }
        }

        if (customs.Count > 0)
        {
            // Each name as typed and capitalised, in one render (class summary): texts 2i and 2i + 1.
            var texts = customs.SelectMany(c => new[] { Clean(c.Name), Capitalised(Clean(c.Name)!) }).ToList();
            var check = ViewTextCheck.Check(connection, campaign, party.Perspective, texts);
            for (var i = 0; i < customs.Count; i++)
            {
                var safe = check.Texts.Skip(2 * i).Take(2).All(verdict => verdict.Passes && !verdict.HasPossibleInventions);
                names[customs[i].Key] = safe ? Clean(customs[i].Name)! : fallback;
            }
        }

        return names;
    }

    /// <summary>The safe name of one character for its own sheet's rolls ("a character" when none is safe).</summary>
    public static string ForCharacter(SqliteConnection connection, CampaignRow campaign, string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        return For(connection, campaign, [new SafeNameSubject("character", EntityId: entityId)], CharacterFallback)["character"];
    }

    /// <summary>
    /// Every view that reads a session's non-secret dice besides the author's: the party, the table, the public, the dm of a
    /// player campaign, and the own view of every character linked to the party (current or former: a member who left
    /// still reads the sessions it was in).
    /// </summary>
    internal static IReadOnlyList<PerspectiveContext> Readers(ReadScope scope)
    {
        var kinds = new List<string> { CV.PerspectiveKinds.Party, CV.PerspectiveKinds.Table, CV.PerspectiveKinds.Public };
        if (scope.Campaign.Role == CV.Roles.Player)
        {
            kinds.Add(CV.PerspectiveKinds.Dm);
        }

        var readers = kinds.Select(k => scope.Loader.Resolve(Perspective.Parse(k))).ToList();
        if (scope.Campaign.PartyId is { } partyId)
        {
            var members = scope.Connection.Query<long>(
                "SELECT DISTINCT e.seq FROM relation r JOIN entity e ON e.id = r.from_id WHERE r.campaign_id = @campaignId AND r.rel = @rel " +
                "AND r.to_id = @partyId AND r.status IN (@current, @former) AND e.kind = @character AND e.deleted_at IS NULL ORDER BY e.seq",
                new
                {
                    campaignId = scope.Campaign.Id,
                    rel = CV.Rels.MemberOf,
                    partyId,
                    current = CV.RelationStatuses.Current,
                    former = CV.RelationStatuses.Former,
                    character = CV.Kinds.Character,
                }).ToList();
            readers.AddRange(members.Select(seq => scope.Loader.Resolve(Perspective.ForCharacter(new CampaignHandle.EntityBySeq(seq)))));
        }

        return readers;
    }

    // The party's name for the entity, when every reader may be shown it (class summary); null otherwise.
    private static string? EntityName(ReadScope scope, PerspectiveContext party, IReadOnlyList<PerspectiveContext> readers, string entityId)
    {
        if (scope.Entity(entityId) is not { } entity)
        {
            return null;
        }

        var partyView = scope.ViewFor(party, entity);
        if (!partyView.Visible || partyView.Disguised)
        {
            return null;
        }

        return readers.Any(reader => scope.ViewFor(reader, entity) is { Visible: true, Disguised: true }) ? null : partyView.DisplayName;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// <paramref name="text"/> with the first letter of every word upper case ("bjorn's axe" → "Bjorn's Axe"): an apostrophe
    /// does not start a word, so a possessive stays one.
    /// </summary>
    internal static string Capitalised(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = text.ToCharArray();
        var atStart = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetterOrDigit(c))
            {
                if (atStart)
                {
                    chars[i] = char.ToUpperInvariant(c);
                }

                atStart = false;
            }
            else if (c is not ('\'' or '’' or '‘' or 'ʼ'))
            {
                atStart = true;
            }
        }

        return new string(chars);
    }
}
