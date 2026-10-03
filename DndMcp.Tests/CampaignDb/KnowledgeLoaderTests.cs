using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: the verdict inputs loaded from campaigns.db are exactly what the rows say: a character perspective resolves
/// to that character with its party membership in session numbers; knowledge rows arrive with session numbers and "via"
/// names; attendance distinguishes present, absent, not listed and not recorded; and "in play" follows contract §3.4.
/// Domain's verdict rules are table-tested on these values, so a loader that counts a missing attendance row as present,
/// or loses a membership's since-session, gives an absent character the party's secrets.
/// </summary>
public sealed class KnowledgeLoaderTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _belmakor;
    private readonly SeededEntity _serif;
    private readonly SeededEntity _tristan;
    private readonly SeededEntity _aiden;
    private readonly SeededEntity _keras;
    private readonly SeededEntity _barkeep;
    private readonly SeededSession _s1;
    private readonly SeededSession _s2;
    private readonly SeededSession _s3;
    private readonly KnowledgeLoader _loader;

    public KnowledgeLoaderTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Belmakor", role: CampaignValues.Roles.Player, partyName: "Mythril Zeppelin");
        _s1 = _seed.Session(_campaign.Id, 1);
        _s2 = _seed.Session(_campaign.Id, 2);
        _s3 = _seed.Session(_campaign.Id, 3);
        _belmakor = Pc("Belmakor Silverwind");
        _serif = Pc("Serif");
        _tristan = Pc("Tristan");
        _aiden = Pc("Aiden Ironstar");
        _barkeep = _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Barkeep", subtype: "npc");
        _keras = _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Keras", visibility: CampaignValues.Visibilities.Author);
        _seed.MemberOf(_campaign.Id, _belmakor.Id, _campaign.Party!.Id);
        _seed.MemberOf(_campaign.Id, _serif.Id, _campaign.Party.Id, sinceSessionId: _s2.EntityId);
        _seed.MemberOf(_campaign.Id, _tristan.Id, _campaign.Party.Id, untilSessionId: _s1.EntityId);
        _seed.MemberOf(_campaign.Id, _aiden.Id, _campaign.Party.Id);
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Party, entityId: _aiden.Id, state: CampaignValues.KnowledgeStates.Met, knownAs: "the stranger");
        _seed.Attendance(_s1.EntityId, _belmakor.Id);
        _seed.Attendance(_s1.EntityId, _serif.Id, present: false);
        _seed.Attendance(_s1.EntityId, _tristan.Id);
        _seed.Attendance(_s3.EntityId, _belmakor.Id);
        _loader = new KnowledgeLoader(_connection, _seed.LoadCampaign(_campaign.Id));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("author", "player", true)]
    [InlineData("dm", "player", false)]
    [InlineData("dm", "dm", true)]
    [InlineData("party", "player", false)]
    [InlineData("table", "player", false)]
    [InlineData("public", "player", false)]
    public void Resolve_NonCharacterPerspective_CarriesTheCampaignRole(string perspective, string role, bool authorView)
    {
        var loader = new KnowledgeLoader(_connection, _seed.LoadCampaign(_campaign.Id) with { Role = role });

        var context = loader.Resolve(Perspective.Parse(perspective));

        Assert.Equal(role, context.CampaignRole);
        Assert.Null(context.CharacterId);
        Assert.Null(context.Membership);
        Assert.Equal(authorView, context.IsAuthorView);
    }

    /// <summary>
    /// Membership since/until come through as session numbers: Serif joined in S2 (party rows from S1 do not speak for
    /// him), Tristan left after S1 (his relation is former), Belmakor has no recorded start, the barkeep was never a member.
    /// The name is the one the character's own view gives itself: the barkeep is party-visible, not a member and has no row,
    /// so his own view does not see his entity and his banner names only the handle (review L04).
    /// </summary>
    [Theory]
    [InlineData("character:belmakor-silverwind", "Belmakor Silverwind", true, null, null, false)]
    [InlineData("character:serif", "Serif", true, 2, null, false)]
    [InlineData("character:tristan", "Tristan", true, null, 1, true)]
    [InlineData("character:barkeep", null, false, null, null, false)]
    public void Resolve_Character_GivesItsIdNameAndMembership(string perspective, string? name, bool member, int? since, int? until, bool former)
    {
        var context = _loader.Resolve(Perspective.Parse(perspective));

        Assert.Equal(name, context.CharacterName);
        Assert.NotNull(context.CharacterId);
        Assert.Equal(member ? new PartyMembership(since, until) { Former = former } : null, context.Membership);
        Assert.False(context.IsAuthorView);
    }

    /// <summary>
    /// A member_of relation marked former with no until is a membership that has ended (review of FD1): read as current, the
    /// character kept the party's relations, objectives and aliases, and everything the party learned in a session with no
    /// attendance recorded.
    /// </summary>
    [Fact]
    public void Membership_FormerRelationWithoutUntil_HasLeft()
    {
        var robin = Pc("Robin");
        _seed.MemberOf(_campaign.Id, robin.Id, _campaign.Party!.Id, sinceSessionId: _s1.EntityId, status: CampaignValues.RelationStatuses.Former);

        var membership = _loader.Membership(robin.Id);

        Assert.Equal(new PartyMembership(1, null) { Former = true }, membership);
        Assert.True(membership!.HasLeft);
        Assert.False(_loader.Membership(_belmakor.Id)!.HasLeft);
    }

    [Fact]
    public void Resolve_CharacterBySeq_FindsIt() =>
        Assert.Equal(_serif.Id, _loader.Resolve(Perspective.Parse("character:" + _serif.SeqHandle)).CharacterId);

    /// <summary>
    /// The banner's and the explanations' name is the one the character knows itself by (review L04): its own row's
    /// known_as ("the stranger"), never its stored true name; none for a perspective typed as <c>e:&lt;n&gt;</c> (the
    /// handle a disguise is shown under: its true name would undo the disguise) or for a character its own view cannot see
    /// (author visibility, or not in play), where printing the name told it to whoever typed the slug.
    /// </summary>
    [Fact]
    public void Resolve_CharacterName_IsTheNameItsOwnViewGivesItElseNone()
    {
        var keras = Pc("Keras Dawnbreaker");
        _seed.MemberOf(_campaign.Id, keras.Id, _campaign.Party!.Id);
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Character, keras.Id, CampaignValues.KnowledgeStates.Met, entityId: keras.Id,
            knownAs: "the stranger");
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Party, state: CampaignValues.KnowledgeStates.Met, entityId: keras.Id,
            knownAs: "the stranger");
        var vecna = _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Vecna Prime", visibility: CampaignValues.Visibilities.Author);
        var proposed = _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Captain Vexmoor", canonStatus: CampaignValues.CanonStatuses.Proposed);
        _seed.MemberOf(_campaign.Id, proposed.Id, _campaign.Party.Id);
        var fact = _seed.Fact(_campaign.Id, "The ship sank.", visibility: CampaignValues.Visibilities.Party);
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Party, factId: fact.Id, learnedSessionId: _s3.EntityId);

        var self = _loader.Resolve(Perspective.Parse("character:keras-dawnbreaker"));
        var verdict = KnowledgeVerdicts.Evaluate(self, _loader.EntriesForFacts([fact.Id])[fact.Id], CampaignValues.Visibilities.Party,
            _loader.Attendance);

        Assert.Equal("the stranger", self.CharacterName);
        Assert.DoesNotContain("Dawnbreaker", verdict.Explanation, StringComparison.Ordinal);
        Assert.Contains("the stranger", verdict.Explanation, StringComparison.Ordinal);
        Assert.Null(_loader.Resolve(Perspective.Parse("character:" + keras.SeqHandle)).CharacterName);
        Assert.Equal(keras.Id, _loader.Resolve(Perspective.Parse("character:" + keras.SeqHandle)).CharacterId);
        Assert.Null(_loader.Resolve(Perspective.Parse("character:vecna-prime")).CharacterName);
        Assert.Equal(vecna.Id, _loader.Resolve(Perspective.Parse("character:vecna-prime")).CharacterId);
        Assert.Null(_loader.Resolve(Perspective.Parse("character:captain-vexmoor")).CharacterName);
    }

    /// <summary>
    /// A slug that begins exactly one character's slug finds it (review U03): campaign create made
    /// <c>character:belmakor-silverwind</c> from the PC's full name, and every fresh session's first call said
    /// <c>character:belmakor</c>. The context carries the full handle, so the banner can say which character it took.
    /// </summary>
    [Fact]
    public void Resolve_SlugBeginningOneKnownCharactersSlug_IsThatCharacterUnderItsFullHandle()
    {
        var context = _loader.Resolve(Perspective.Parse("character:belmakor"));

        Assert.Equal(_belmakor.Id, context.CharacterId);
        Assert.Equal("character:belmakor-silverwind", context.Perspective.Text);
        Assert.Equal("Belmakor Silverwind", context.CharacterName);
        Assert.Equal(new PartyMembership(null, null), context.Membership);
    }

    /// <summary>
    /// The completion takes only a character the refusal could suggest, and counts only those: two of them is no completion
    /// (refused with both suggested); one the party does not know by name (author-only, disguised by the party's row, not in
    /// play) is neither taken nor counted, so a typed prefix never confirms that it exists.
    /// </summary>
    [Fact]
    public void Resolve_SlugBeginningSeveralOrHiddenCharacters_TakesOnlyAUniqueKnownOne()
    {
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Shadow", visibility: CampaignValues.Visibilities.Author);
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Echo", canonStatus: CampaignValues.CanonStatuses.Struck);

        Assert.Equal(_belmakor.Id, _loader.Resolve(Perspective.Parse("character:belmakor")).CharacterId);
        var aiden = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:aiden")));
        Assert.DoesNotContain("aiden-ironstar", aiden.Message, StringComparison.Ordinal);

        Pc("Belmakor Junior");
        var two = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:belmakor")));
        Assert.Contains("no character belmakor in this campaign", two.Message, StringComparison.Ordinal);
        Assert.Contains("character:belmakor-junior", two.Message, StringComparison.Ordinal);
        Assert.Contains("character:belmakor-silverwind", two.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("shadow", two.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("echo", two.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The completion takes a slug that ends where a word of the character's slug ends: <c>character:bel</c> and
    /// <c>character:belmakor-silver</c> name nobody, and are refused (suggesting the full handle), rather than reading as
    /// whoever's slug happens to begin with those letters.
    /// </summary>
    [Theory]
    [InlineData("character:bel")]
    [InlineData("character:belmakor-silver")]
    [InlineData("character:belmakorsilverwind")]
    public void Resolve_SlugThatEndsInsideAWordOfACharactersSlug_IsRefused(string perspective)
    {
        var error = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse(perspective)));

        Assert.Contains("no character", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A character who knows himself only by another name (his own row: "the stranger") is not a completion: the banner
    /// prints the handle completed to, and <c>character:corus-dawnbreaker</c> spells the true name his view hides from him,
    /// which the caller never typed (review of fix FH5). Typed in full, the handle is his and the name is the one he knows.
    /// </summary>
    [Fact]
    public void Resolve_SlugBeginningTheSlugOfACharacterWhoKnowsHimselfByAnotherName_IsNoCompletion()
    {
        var corus = Pc("Corus Dawnbreaker");
        _seed.MemberOf(_campaign.Id, corus.Id, _campaign.Party!.Id);
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Character, corus.Id, CampaignValues.KnowledgeStates.Met, entityId: corus.Id,
            knownAs: "the stranger");

        var error = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:corus")));
        var typed = _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"));

        Assert.Contains("no character corus in this campaign", error.Message, StringComparison.Ordinal);
        Assert.Equal((corus.Id, "character:corus-dawnbreaker", "the stranger"), (typed.CharacterId, typed.Perspective.Text, typed.CharacterName));
    }

    /// <summary>
    /// As of a session the character's own view is the one it had then (review LR01): Corus knew himself as "the stranger"
    /// from S1 and learned his name in S3 (the same row, edited). As of S2 he is "the stranger", and while he knew himself
    /// by another name a prefix is no completion; judged by today's rows, the banner of an as-of-2 read printed his true
    /// name above a listing of himself as "the stranger", and "character:corus" completed. Today both are his own name.
    /// </summary>
    [Fact]
    public void Resolve_AsOfASessionBeforeTheCharacterLearnedHisOwnName_NamesHimAsHeKnewHimselfThen()
    {
        var corus = Pc("Corus Dawnbreaker");
        _seed.MemberOf(_campaign.Id, corus.Id, _campaign.Party!.Id);
        var own = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = own, ["campaign_id"] = _campaign.Id, ["entity_id"] = corus.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Character,
            ["knower_id"] = corus.Id, ["state"] = CampaignValues.KnowledgeStates.Met, ["known_as"] = "the stranger", ["learned_session_id"] = _s1.EntityId,
        }, "record"), sessionId: _s1.EntityId);
        _db.Batch(_campaign.Id, r => r.Update("knowledge", own, new Dictionary<string, object?> { ["known_as"] = "Corus Dawnbreaker" }, "record"),
            sessionId: _s3.EntityId);

        var then = _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 2);
        var now = _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"));
        var prefixThen = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:corus"), asOfSession: 2));
        var prefixNow = _loader.Resolve(Perspective.Parse("character:corus"));

        Assert.Equal(("the stranger", "Corus Dawnbreaker"), (then.CharacterName, now.CharacterName));
        Assert.Contains("no character corus in this campaign", prefixThen.Message, StringComparison.Ordinal);
        Assert.Equal((corus.Id, "character:corus-dawnbreaker", "Corus Dawnbreaker"),
            (prefixNow.CharacterId, prefixNow.Perspective.Text, prefixNow.CharacterName));
        Assert.Equal(("the stranger", "Corus Dawnbreaker"),
            (_loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 1).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 3).CharacterName));
    }

    /// <summary>
    /// As of a session before a character existed, his own view has nothing to name (he is hidden from himself then), so
    /// the banner names only the handle; the name he has now is not projected back.
    /// </summary>
    [Fact]
    public void Resolve_AsOfASessionBeforeTheCharacterWasMade_HasNoName()
    {
        var corus = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("entity", new Dictionary<string, object?>
        {
            ["id"] = corus, ["campaign_id"] = _campaign.Id, ["kind"] = CampaignValues.Kinds.Character, ["slug"] = "corus-dawnbreaker",
            ["name"] = "Corus Dawnbreaker", ["visibility"] = CampaignValues.Visibilities.Public,
        }, "upsert"), sessionId: _s3.EntityId);

        Assert.Null(_loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 2).CharacterName);
        Assert.Equal("Corus Dawnbreaker", _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 3).CharacterName);
    }

    /// <summary>
    /// As of a session the own view replays the character's own row too, as every as_of read does (LR01's recheck): Corus,
    /// renamed in S3, was "Corus Dawnbreaker" in S2. Judged by today's row, the banner of an as-of-2 read named him by the
    /// name he had only from S3, above a listing that named him by the old one.
    /// </summary>
    [Fact]
    public void Resolve_AsOfASessionBeforeTheCharacterWasRenamed_NamesHimByHisNameThen()
    {
        var corus = Pc("Corus Dawnbreaker");
        _seed.MemberOf(_campaign.Id, corus.Id, _campaign.Party!.Id);
        _db.Batch(_campaign.Id, r => r.Update("entity", corus.Id, new Dictionary<string, object?> { ["name"] = "Corus Brightblade" }, "upsert"),
            sessionId: _s3.EntityId);

        Assert.Equal(("Corus Dawnbreaker", "Corus Brightblade", "Corus Brightblade"),
            (_loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 2).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 3).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker")).CharacterName));
    }

    /// <summary>
    /// As of a session the own view's rows are dated as every as_of verdict is (LR01's recheck): Corus's own record "the
    /// stranger", written between sessions but learned in S3, was not yet his in S2, which stops every default (the "not
    /// yet" wall): he is hidden from himself then, and the banner names only the handle. Undated, the record counted in S2,
    /// and the banner named him "the stranger" before he ever was.
    /// </summary>
    [Fact]
    public void Resolve_AsOfASessionBeforeHisOwnRecordWasLearned_HasNoName()
    {
        var corus = Pc("Corus Dawnbreaker");
        _seed.MemberOf(_campaign.Id, corus.Id, _campaign.Party!.Id);
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = CampaignDatabase.NewId(), ["campaign_id"] = _campaign.Id, ["entity_id"] = corus.Id,
            ["knower_kind"] = CampaignValues.KnowerKinds.Character, ["knower_id"] = corus.Id, ["state"] = CampaignValues.KnowledgeStates.Met,
            ["known_as"] = "the stranger", ["learned_session_id"] = _s3.EntityId,
        }, "record"));

        Assert.Equal((null, "the stranger", "the stranger"),
            (_loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 2).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 3).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker")).CharacterName));
    }

    /// <summary>
    /// As of a session a character deleted by then is hidden from himself, as every as_of read hides a deleted row (LR01's
    /// recheck): Corus, deleted in S2 and restored in S3, did not exist at the end of S2.
    /// </summary>
    [Fact]
    public void Resolve_AsOfASessionTheCharacterWasDeletedIn_HasNoName()
    {
        var corus = Pc("Corus Dawnbreaker");
        _seed.MemberOf(_campaign.Id, corus.Id, _campaign.Party!.Id);
        _db.Batch(_campaign.Id, r => r.SoftDelete("entity", corus.Id), sessionId: _s2.EntityId);
        _db.Batch(_campaign.Id, r => r.Restore("entity", corus.Id), sessionId: _s3.EntityId);

        Assert.Equal(("Corus Dawnbreaker", null, "Corus Dawnbreaker"),
            (_loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 1).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 2).CharacterName,
                _loader.Resolve(Perspective.Parse("character:corus-dawnbreaker"), asOfSession: 3).CharacterName));
    }

    /// <summary>
    /// The "no character" refusal suggests only characters in play (review L03): a proposed, planned, lean, struck or
    /// superseded character is hidden from every non-author read, and suggesting it named an invention to whoever typed a
    /// near miss. An in-play one with the same visibility is still suggested.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.CanonStatuses.Proposed, false)]
    [InlineData(CampaignValues.CanonStatuses.Planned, false)]
    [InlineData(CampaignValues.CanonStatuses.Lean, false)]
    [InlineData(CampaignValues.CanonStatuses.Struck, false)]
    [InlineData(CampaignValues.CanonStatuses.Superseded, false)]
    [InlineData(CampaignValues.CanonStatuses.Canon, true)]
    public void Resolve_NearMissOfACharacterNotInPlay_NeverSuggestsIt(string canonStatus, bool suggested)
    {
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Captain Vexmoor", subtype: "npc", canonStatus: canonStatus);

        var error = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:vex")));

        Assert.Equal(suggested, error.Message.Contains("vexmoor", StringComparison.Ordinal));
    }

    /// <summary>A planned or rumored member_of is not a membership: the party's knowledge does not flow to them.</summary>
    [Fact]
    public void Membership_PlannedRelation_IsNotAMembership()
    {
        _seed.MemberOf(_campaign.Id, _barkeep.Id, _campaign.Party!.Id, status: CampaignValues.RelationStatuses.Planned);

        Assert.Null(_loader.Membership(_barkeep.Id));
    }

    /// <summary>
    /// Membership is member_of THE PARTY (the campaign's party_id), not of any faction: counting the Thieves' Guild would
    /// hand a guild member everything the party learned.
    /// </summary>
    [Fact]
    public void Membership_MemberOfAnotherFactionOnly_IsNotPartyMembership()
    {
        var guild = _seed.Entity(_campaign.Id, CampaignValues.Kinds.Faction, "Thieves Guild");
        _seed.MemberOf(_campaign.Id, _barkeep.Id, guild.Id);

        Assert.Null(_loader.Membership(_barkeep.Id));
        Assert.Null(_loader.Resolve(Perspective.Parse("character:barkeep")).Membership);
    }

    /// <summary>
    /// No such character: one message whatever the handle names (a location, an author-only secret, the party faction, a
    /// deleted character), so the error confirms nothing hidden; suggestions never offer the author-only Keras.
    /// </summary>
    [Theory]
    [InlineData("character:serret")]
    [InlineData("character:the-axiom-cage")]
    [InlineData("character:mythril-zeppelin")]
    [InlineData("character:nobody")]
    public void Resolve_NoSuchCharacter_SaysOnlyThatAndLeaksNothing(string perspective)
    {
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Location, "Serret");
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Secret, "The Axiom Cage", visibility: CampaignValues.Visibilities.Author);
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Nobody", deletedAt: "2026-09-01T12:00:00.000Z");

        var error = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse(perspective)));

        Assert.Contains("no character", error.Message);
        Assert.DoesNotContain("location", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("faction", error.Message);
        Assert.DoesNotContain("deleted", error.Message);
        Assert.DoesNotContain("character:keras", error.Message.Replace(perspective, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Misspelled_SuggestsKnownCharactersOnly()
    {
        var belmakor = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:belmakr")));
        var aiden = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:aiden")));
        var keras = Assert.Throws<DndInputException>(() => _loader.Resolve(Perspective.Parse("character:kera")));

        Assert.Contains("Did you mean character:belmakor-silverwind?", belmakor.Message);
        Assert.DoesNotContain("aiden-ironstar", aiden.Message);
        Assert.DoesNotContain("character:keras", keras.Message);
    }

    [Fact]
    public void EntriesForFacts_Rows_ComeWithSessionNumbersViaNamesAndEveryRequestedId()
    {
        var fact = _seed.Fact(_campaign.Id, "The old king wants to go home.");
        var silent = _seed.Fact(_campaign.Id, "Nobody knows this.");
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Party, factId: fact.Id, learnedSessionId: _s1.EntityId,
            viaEntityId: _keras.Id, how: "told", knownAs: "the thing he wants", note: "sung");
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Character, _serif.Id, CampaignValues.KnowledgeStates.Forgot,
            factId: fact.Id, learnedSessionId: _s2.EntityId, validUntilSessionId: _s3.EntityId);
        _seed.Knowledge(_campaign.Id, CampaignValues.KnowerKinds.Public, state: CampaignValues.KnowledgeStates.Heard, factId: fact.Id);

        var entries = _loader.EntriesForFacts([fact.Id, silent.Id, fact.Id]);

        Assert.Equal(2, entries.Count);
        Assert.Empty(entries[silent.Id]);
        var rows = entries[fact.Id];
        Assert.Equal(new[] { "character", "party", "public" }, rows.Select(r => r.KnowerKind));
        Assert.Equal(new KnowledgeEntry("party", null, "knows", "the thing he wants", 1, null, "told", "Keras", "sung"), rows[1]);
        Assert.Equal(new KnowledgeEntry("character", _serif.Id, "forgot", null, 2, 3), rows[0]);
        Assert.Equal(new KnowledgeEntry("public", null, "heard"), rows[2]);
    }

    [Fact]
    public void EntriesForEntities_AwarenessRows_CarryTheKnownAs()
    {
        var entries = _loader.EntriesForEntities([_aiden.Id, _keras.Id]);

        Assert.Equal(new KnowledgeEntry("party", null, "met", "the stranger"), Assert.Single(entries[_aiden.Id]));
        Assert.Empty(entries[_keras.Id]);
    }

    /// <summary>
    /// S1 recorded attendance (Belmakor present, Serif absent, Aiden not listed); S2 recorded none. NotListed is never
    /// Present: that would give an absent character everything the party learned that night.
    /// </summary>
    [Theory]
    [InlineData("belmakor", 1, AttendanceAnswer.Present)]
    [InlineData("serif", 1, AttendanceAnswer.Absent)]
    [InlineData("aiden", 1, AttendanceAnswer.NotListed)]
    [InlineData("belmakor", 2, AttendanceAnswer.NotRecorded)]
    [InlineData("aiden", 3, AttendanceAnswer.NotListed)]
    [InlineData("belmakor", 9, AttendanceAnswer.NotRecorded)]
    public void Attendance_BySessionNumber_DistinguishesTheFourAnswers(string who, int session, AttendanceAnswer expected)
    {
        var id = who switch
        {
            "belmakor" => _belmakor.Id,
            "serif" => _serif.Id,
            _ => _aiden.Id,
        };

        Assert.Equal(expected, _loader.Attendance.Of(id, session));
    }

    public static TheoryData<string, int?, bool, int?, bool> InPlayCases() => new()
    {
        { CampaignValues.CanonStatuses.Canon, 2, false, null, true },
        { CampaignValues.CanonStatuses.Played, 2, false, null, true },
        { CampaignValues.CanonStatuses.Ruled, 2, false, null, true },
        { CampaignValues.CanonStatuses.Accepted, 2, false, null, true },
        { CampaignValues.CanonStatuses.Proposed, 2, false, null, false },
        { CampaignValues.CanonStatuses.Planned, 2, false, null, false },
        { CampaignValues.CanonStatuses.Struck, 2, false, null, false },
        { CampaignValues.CanonStatuses.Superseded, 2, false, null, false },
        { CampaignValues.CanonStatuses.Lean, 2, false, null, false },
        { CampaignValues.CanonStatuses.Canon, null, false, null, false },
        { CampaignValues.CanonStatuses.Canon, 2, true, null, false },
        { CampaignValues.CanonStatuses.Canon, 2, false, 1, false },
        { CampaignValues.CanonStatuses.Canon, 2, false, 2, true },
        { CampaignValues.CanonStatuses.Canon, 2, false, 3, true },
    };

    /// <summary>In play = established in a session ≤ n, a live canon status, not deleted (contract §3.4).</summary>
    [Theory]
    [MemberData(nameof(InPlayCases))]
    public void InPlay_CanonStatusSessionAndDeletion_DecideTogether(string canonStatus, int? establishedIn, bool deleted, int? asOf, bool expected)
    {
        var session = establishedIn switch
        {
            1 => _s1.EntityId,
            2 => _s2.EntityId,
            _ => null,
        };
        var fact = _seed.Fact(_campaign.Id, "The seal holds.", canonStatus: canonStatus, establishedSessionId: session,
            deletedAt: deleted ? "2026-09-01T12:00:00.000Z" : null);
        var row = new HandleResolver(_connection, _campaign.Id).TryFact(CampaignHandle.Parse(fact.SeqHandle), includeDeleted: true)!;

        Assert.Equal(expected, _loader.InPlay(row, asOf));
    }

    [Fact]
    public void SessionNumber_SessionIdsOfThisCampaignOnly()
    {
        var other = _seed.Campaign(name: "Other");
        var foreign = _seed.Session(other.Id, 7);

        Assert.Equal(2, _loader.SessionNumber(_s2.EntityId));
        Assert.Null(_loader.SessionNumber(null));
        Assert.Null(_loader.SessionNumber(foreign.EntityId));
        Assert.Null(_loader.SessionNumber(_belmakor.Id));
    }

    private SeededEntity Pc(string name) =>
        _seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, name, subtype: CampaignValues.Subtypes.Pc);
}
