using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: upsert finds before it creates (by ref, else by kind and name, article-insensitive), never changes a kind
/// or a slug, derives defaults per §3.7 (visibility by kind first: a secret restricted, a rule, note or question author),
/// suffixes a taken slug with a warning, keeps parents acyclic and shallow, and reports exactly the fields that changed.
/// A second "Iron Guts" that created iron-guts-2 would split one NPC's history in two; a slug that followed a rename
/// would break every note that cites it; a secret visible by default would be listed to the party.
/// </summary>
public sealed class UpsertOpTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public UpsertOpTests()
    {
        _dm = _f.Campaign("One Piece");
    }

    public void Dispose() => _f.Dispose();

    [Theory]
    [InlineData("quest", "The War God's Axe", "quest:the-war-gods-axe", "open")]
    [InlineData("secret", "Fruits are the seal", "secret:fruits-are-the-seal", "hidden")]
    [InlineData("character", "Björn Mountainfell", "character:bjorn-mountainfell", null)]
    [InlineData("beat", "The dock fight", "beat:the-dock-fight", "pending")]
    [InlineData("note", "G.O.D.S. Co. idea", "note:g-o-d-s-co-idea", null)]
    public void Upsert_KindAndName_CreatesWithTheKindsDefaults(string kind, string name, string reference, string? status)
    {
        var result = _f.Apply(_dm, Op.Upsert(kind, name));

        var applied = Assert.Single(result.Applied);
        Assert.Equal((WriteOutcomes.Created, reference), (applied.Outcome, applied.Ref));
        var entity = _f.Entity(_dm, reference);
        Assert.Equal((name, status, "canon", "confirmed"), (entity.Name, entity.Status, entity.CanonStatus, entity.Confidence));
        Assert.NotNull(result.BatchId);
    }

    [Theory]
    [InlineData("dm", null, "restricted")]
    [InlineData("player", null, "party")]
    [InlineData("dm", "{\"default_visibility\": \"public\"}", "public")]
    [InlineData("player", "{\"default_visibility\": \"author\"}", "author")]
    public void Upsert_NoVisibility_UsesTheSettingElseTheRolesDefault(string role, string? settings, string expected)
    {
        var campaign = _f.Campaign("Visibility " + role + settings?.Length, role: role, settingsJson: settings);

        _f.Apply(campaign, Op.Upsert("location", "Serret"));

        Assert.Equal(expected, _f.Entity(campaign, "location:serret").Visibility);
    }

    /// <summary>
    /// Review L06: a secret is never player-visible by default (restricted: known by exactly the knowers its rows name),
    /// and a rule, note or question is the author's (author); the setting then the role decide every other kind. A setting
    /// stricter than the kind's default still wins. Before, a player campaign showed the party "The old king is Kerasorn ·
    /// secret · hidden".
    /// </summary>
    [Theory]
    [InlineData("player", null, "secret", "restricted")]
    [InlineData("player", null, "rule", "author")]
    [InlineData("player", null, "note", "author")]
    [InlineData("player", null, "question", "author")]
    [InlineData("player", null, "location", "party")]
    [InlineData("dm", null, "secret", "restricted")]
    [InlineData("dm", null, "rule", "author")]
    [InlineData("dm", null, "note", "author")]
    [InlineData("dm", null, "question", "author")]
    [InlineData("dm", null, "character", "restricted")]
    [InlineData("player", "{\"default_visibility\": \"public\"}", "secret", "restricted")]
    [InlineData("player", "{\"default_visibility\": \"public\"}", "note", "author")]
    [InlineData("player", "{\"default_visibility\": \"public\"}", "character", "public")]
    [InlineData("dm", "{\"default_visibility\": \"author\"}", "secret", "author")]
    [InlineData("dm", "{\"default_visibility\": \"party\"}", "question", "author")]
    public void Upsert_NoVisibility_DefaultsByKindBeforeTheSettingAndTheRole(string role, string? settings, string kind, string expected)
    {
        var campaign = _f.Campaign("Kinds " + role + settings?.Length, role: role, settingsJson: settings);

        var result = _f.Apply(campaign, Op.Upsert(kind, "The old king is Kerasorn"));

        Assert.Equal(expected, _f.Entity(campaign, Assert.Single(result.Applied).Ref).Visibility);
    }

    [Theory]
    [InlineData("player", "secret", "party")]
    [InlineData("dm", "note", "public")]
    public void Upsert_KindWithADefault_TakesAnExplicitVisibility(string role, string kind, string visibility)
    {
        var campaign = _f.Campaign("Explicit " + role, role: role);

        var result = _f.Apply(campaign, Op.Upsert(kind, "Theory: Kerasorn rules the void", visibility: visibility));

        Assert.Equal(visibility, _f.Entity(campaign, Assert.Single(result.Applied).Ref).Visibility);
    }

    [Theory]
    [InlineData("Iron Guts")]
    [InlineData("iron guts")]
    [InlineData("  IRON   GUTS ")]
    [InlineData("the Iron Guts")]
    public void Upsert_SameNameAgain_UpdatesTheEntityInsteadOfCreatingASecond(string name)
    {
        _f.Apply(_dm, Op.Upsert("character", "Iron Guts", "npc"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = name, Summary = "Dwarf smith." });

        var applied = Assert.Single(result.Applied);
        Assert.Equal(("character:iron-guts", WriteOutcomes.Updated), (applied.Ref, applied.Outcome));
        Assert.Equal(1, _f.Count("SELECT count(*) FROM entity WHERE kind = 'character' AND slug LIKE 'iron-guts%'"));
        Assert.Equal("Dwarf smith.", _f.Entity(_dm, "character:iron-guts").Summary);
    }

    [Fact]
    public void Upsert_ByRef_UpdatesAndReportsOnlyTheChangedFields()
    {
        _f.Apply(_dm, Op.Upsert("character", "Nadar", "deity", "party"));

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Ref = "character:nadar", Subtype = "deity", Summary = "The War God.", Visibility = "Party", SortKey = 2,
        });

        var applied = Assert.Single(result.Applied);
        Assert.Equal(WriteOutcomes.Updated, applied.Outcome);
        Assert.Equal(["summary", "sort_key"], applied.ChangedFields);
        Assert.Equal(["summary", "sort_key"], _f.Log(result.BatchId).Select(r => r.FieldPath));
    }

    [Fact]
    public void Upsert_NothingChanges_IsUnchangedAndLogsNothing()
    {
        _f.Apply(_dm, Op.Upsert("character", "Nadar", "deity"));

        var result = _f.Apply(_dm, Op.Upsert("character", "Nadar", "deity"));

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void Upsert_RefNotFound_IsRefusedWithHowToCreate()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:nobody", Summary = "x" }));

        Assert.Contains("ops item 1 (upsert character:nobody): ref: no entity character:nobody in this campaign.", ex.Message);
        Assert.Contains("give kind and name without ref", ex.Message);
    }

    [Fact]
    public void Upsert_RefDeleted_IsRefusedWithHowToRestore()
    {
        _f.Apply(_dm, Op.Upsert("character", "Lance"));
        var seq = _f.Entity(_dm, "character:lance").SeqHandle;
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "character:lance" });

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:lance", Summary = "x" }));

        Assert.Contains($"ref character:lance is deleted ({seq}); restore it first", ex.Message);
    }

    [Fact]
    public void Upsert_KindDiffersFromRef_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("character", "Serret"));
        var seq = _f.Entity(_dm, "character:serret").SeqHandle;

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = seq, Kind = "location" }));

        Assert.Contains("kind location differs from the ref's kind character", ex.Message);
    }

    [Fact]
    public void Upsert_SessionByRef_IsRefused()
    {
        _f.Played(_dm, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "e:2", Summary = "x" }));

        Assert.Contains("campaign_session", ex.Message);
    }

    [Theory]
    [InlineData(false, "item", "slug keras is taken by character:keras (e:2), so the new item is item:keras-2.")]
    [InlineData(true, "character", "It is deleted: to bring it back instead, restore it with {\"op\": \"restore\", \"ref\": \"e:2\"}.")]
    public void Upsert_DerivedSlugTaken_GetsASuffixAndAWarningNamingTheHolder(bool deleteFirst, string kind, string expected)
    {
        _f.Apply(_dm, Op.Upsert("character", "Keras"));
        if (deleteFirst)
        {
            _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "character:keras" });
        }

        // A live entity of the same kind and name would be found and updated; a deleted one or another kind is not.
        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = kind, Name = "Keras", Summary = "twin" });

        Assert.Equal(kind + ":keras-2", Assert.Single(result.Applied).Ref);
        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.SlugCollision);
        Assert.Contains(expected, warning.Message);
    }

    [Fact]
    public void Upsert_ExplicitSlugTaken_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("character", "Keras"));

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "Cage", Slug = "keras" }));

        Assert.Contains("slug keras is taken by e:2", ex.Message);
    }

    [Fact]
    public void Upsert_ExplicitSlug_IsUsed()
    {
        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Old King", Slug = "old-king" });

        Assert.Equal("character:old-king", Assert.Single(result.Applied).Ref);
    }

    [Fact]
    public void Upsert_Rename_KeepsTheSlugWithAWarning()
    {
        _f.Apply(_dm, Op.Upsert("character", "Iron Guts"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Name = "Brass Belly" });

        Assert.Equal("character:iron-guts", Assert.Single(result.Applied).Ref);
        Assert.Equal("Brass Belly", _f.Entity(_dm, "character:iron-guts").Name);
        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.SlugKept && w.Message.Contains("slug stays iron-guts"));
    }

    [Fact]
    public void Upsert_Aliases_AreAddedUpdatedAndRemoved()
    {
        _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "The Old King",
            Aliases = [new AliasSpec { Alias = "the sorcerer king" }, new AliasSpec { Alias = "Keras", Visibility = "author" }],
        });

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Ref = "character:the-old-king",
            Aliases = [new AliasSpec { Alias = "THE SORCERER KING", Visibility = "public" }, new AliasSpec { Alias = "Keras" }],
            RemoveAliases = ["nobody"],
        });
        var removed = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:the-old-king", RemoveAliases = ["keras"] });

        Assert.Equal(["aliases"], Assert.Single(result.Applied).ChangedFields);
        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.Nothing && w.Message.Contains("remove_aliases item 1"));
        var alias = Assert.Single(_f.Query<AliasRow>($"SELECT {AliasRow.Columns} FROM entity_alias"));
        Assert.Equal(("the sorcerer king", "public"), (alias.Alias, alias.Visibility));
        Assert.Equal(WriteOutcomes.Updated, Assert.Single(removed.Applied).Outcome);
    }

    [Fact]
    public void Upsert_Tags_AreCreatedPerCampaignOnFirstUseAndRemoved()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Serret", Tags = ["arc-3", "Void"] });
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Ohara", Tags = ["arc 3"] });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "location:serret", RemoveTags = ["VOID", "missing"] });

        Assert.Equal(2, _f.Count("SELECT count(*) FROM tag"));
        Assert.Equal(2, _f.Count("SELECT count(*) FROM entity_tag"));
        Assert.Equal(["tags"], Assert.Single(result.Applied).ChangedFields);
        Assert.Contains(result.Warnings, w => w.Message.Contains("remove_tags item 2"));
    }

    [Fact]
    public void Upsert_Data_IsAMergePatchLoggedPerKey()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Data = Op.Data("{\"attitude\": 20, \"home\": \"Serret\"}") });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Data = Op.Data("{\"attitude\": -40, \"home\": null, \"likes\": [\"ale\"]}") });

        Assert.Equal("{\"attitude\":-40,\"likes\":[\"ale\"]}", _f.Entity(_dm, "character:iron-guts").Data);
        Assert.Equal(["data.attitude", "data.home", "data.likes"], Assert.Single(result.Applied).ChangedFields);
        Assert.Equal(["data.attitude", "data.home", "data.likes"], _f.Log(result.BatchId).Select(r => r.FieldPath));
    }

    [Fact]
    public void Upsert_DataOverTheLimitAfterTheMerge_IsRefused()
    {
        var half = new string('x', 12_000);
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "note", Name = "Big", Data = Op.Data($"{{\"a\": \"{half}\"}}") });
        var before = _f.Dump();

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "note:big", Data = Op.Data($"{{\"b\": \"{half}\"}}") }));

        Assert.Contains("data would be 24015 characters as JSON after the merge; at most 20000", ex.Message);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void Upsert_Parent_ACycleIsRefused()
    {
        _f.Apply(_dm, Op.Upsert("location", "Sky"), new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Flotsam", Parent = "location:sky" });

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "location:sky", Parent = "location:flotsam" }));

        Assert.Contains($"parent {_f.Entity(_dm, "location:flotsam").SeqHandle} is inside {_f.Entity(_dm, "location:sky").SeqHandle}; that would make a cycle.", ex.Message);
    }

    [Fact]
    public void Upsert_Parent_DeeperThanEightIsRefused()
    {
        var ops = new List<CampaignOpSpec> { Op.Upsert("location", "L0") };
        for (var i = 1; i <= 8; i++)
        {
            ops.Add(new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "L" + i, Parent = "location:l" + (i - 1) });
        }

        _f.Apply(_dm, ops.ToArray());

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "L9", Parent = "location:l8" }));
        Assert.Contains("would sit 9 levels deep; at most 8", ex.Message);
    }

    [Fact]
    public void Upsert_ParentMoveThatPushesChildrenTooDeep_IsRefused()
    {
        var ops = new List<CampaignOpSpec> { Op.Upsert("location", "A0") };
        for (var i = 1; i <= 5; i++)
        {
            ops.Add(new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "A" + i, Parent = "location:a" + (i - 1) });
        }

        ops.Add(Op.Upsert("location", "B0"));
        for (var i = 1; i <= 3; i++)
        {
            ops.Add(new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "B" + i, Parent = "location:b" + (i - 1) });
        }

        _f.Apply(_dm, ops.ToArray());

        // a5 is at depth 5; b0 has children three deep, so under a5 its leaf would be at 5 + 1 + 3 = 9.
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "location:b0", Parent = "location:a5" }));
        Assert.Contains("9 levels deep", ex.Message);
    }

    [Fact]
    public void Upsert_Clock_NeedsSegmentsOnCreate()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, Op.Upsert("clock", "Three days")));

        Assert.Contains("clock.segments is required to create a clock", ex.Message);
    }

    [Fact]
    public void Upsert_Clock_CreatesItsRowAndUpdatesIt()
    {
        _f.Apply(_dm, Op.Upsert("front", "The Void"),
            new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "Three days", Clock = new ClockSpec { Segments = 6, Unit = "Day", Front = "front:the-void", OnFillMd = "The blood moon rises." } });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "clock:three-days", Clock = new ClockSpec { Filled = 2, ShownToPlayers = true } });

        var clock = Assert.Single(_f.Query<ClockRow>($"SELECT {ClockRow.Columns} FROM clock"));
        Assert.Equal((6L, 2L, "day", 1L), (clock.Segments, clock.Filled, clock.Unit, clock.ShownToPlayers));
        Assert.Equal(_f.Entity(_dm, "front:the-void").Id, clock.FrontId);
        Assert.Equal(["clock.filled", "clock.shown_to_players"], Assert.Single(result.Applied).ChangedFields);
        Assert.Equal("running", _f.Entity(_dm, "clock:three-days").Status);
    }

    [Fact]
    public void Upsert_ClockFilledPastSegments_IsRefused()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "Chase", Clock = new ClockSpec { Segments = 4 } });

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "clock:chase", Clock = new ClockSpec { Filled = 5 } }));

        Assert.Contains("clock.filled would be 5, more than its 4 segments", ex.Message);
    }

    [Theory]
    [InlineData("{\"forbidden_terms\": [\"Keras\"], \"applies_to\": [\"party\"]}", "unknown key \"applies_to\"")]
    [InlineData("{\"note\": \"no words\"}", "a reveal rule forbids words")]
    [InlineData("{\"forbidden_terms\": [\"Keras\"], \"until\": [\"f:99\"]}", "no fact f:99")]
    public void Upsert_RevealRule_DataIsValidated(string data, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name", Data = Op.Data(data),
        }));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Upsert_RevealRule_ValidDataIsStored()
    {
        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name, no timespan", Visibility = "author",
            Data = Op.Data("{\"forbidden_terms\": [\"Keras\"], \"forbidden_patterns\": [\"<n>-year-old\", \"<n> years\"]}"),
        });

        Assert.Equal(WriteOutcomes.Created, Assert.Single(result.Applied).Outcome);
    }

    [Fact]
    public void Upsert_StatusThatDoesNotFitTheResolvedKind_IsRefusedForASeqHandle()
    {
        _f.Apply(_dm, Op.Upsert("character", "Tristan"));

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "e:2", Status = "resolved" }));

        Assert.Contains("ops item 1 (upsert e:2): status \"resolved\" is not a character status", ex.Message);
    }

    [Fact]
    public void Upsert_CharacterDies_WarnsToReviewMembershipAndKnowledge()
    {
        _f.Apply(_dm, Op.Upsert("character", "Tristan", "pc", status: "alive"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:tristan", Status = "dead" });

        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.CharacterDead && w.Message.Contains("member_of"));
    }

    [Fact]
    public void Upsert_IntroducedSessionAndKnownBy_UseTheSessions()
    {
        _f.Played(_dm, 3);
        _f.Sessions.Start(_dm, 4);

        _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "The Protector", IntroducedSession = 3,
            KnownBy = [Op.Knower("party", "unrecognized", "the advisor in Serret")],
        });

        var protector = _f.Entity(_dm, "character:the-protector");
        Assert.Equal(_f.Entity(_dm, "session:3").Id, protector.IntroducedSessionId);
        Assert.Equal((_f.Entity(_dm, "session:4").Id, "the advisor in Serret"),
            Assert.Single(_f.Query<(string, string)>("SELECT learned_session_id, known_as FROM knowledge")));
    }

    [Fact]
    public void Upsert_ProposedEntityWithoutCode_GetsTheNextFCode()
    {
        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Harbourmaster", CanonStatus = "proposed" });

        Assert.Equal("F1", Assert.Single(result.Applied).Code);
    }

    [Fact]
    public void Upsert_KnownByOnAnAuthorVisibilityEntity_WarnsThatAuthorVisibilityHidesIt()
    {
        _f.Apply(_dm, Op.Upsert("character", "The Mistaken One", visibility: "author"));

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Ref = "character:the-mistaken-one", KnownBy = [Op.Knower("party", "met")],
        });

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(WarningKinds.AuthorVisibility, warning.Kind);
        Assert.Contains("character:the-mistaken-one has author visibility", warning.Message);
        Assert.Equal(["known_by party"], Assert.Single(result.Applied).ChangedFields);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("dm")]
    public void Upsert_KnownByTheAuthorViewOnAnAuthorEntity_DoesNotWarn(string who)
    {
        _f.Apply(_dm, Op.Upsert("character", "The Dutiful One", visibility: "author"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:the-dutiful-one", KnownBy = [Op.Knower(who)] });

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Upsert_KnownByAnUnknownCharacter_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "Nadar", KnownBy = [Op.Knower("character:nobody")],
        }));

        Assert.Contains("known_by item 1: who: no character nobody in this campaign", ex.Message);
    }

    [Fact]
    public void Upsert_SlugWithAnExistingName_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("character", "Nadar"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Nadar", Slug = "war-god" }));

        Assert.Contains("slug is only for creating and never changes", ex.Message);
    }

    [Fact]
    public void Upsert_ExplicitSlugOfADeletedEntity_IsRefusedSuggestingRestore()
    {
        _f.Apply(_dm, Op.Upsert("character", "Iron Guts"));
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "character:iron-guts" });
        var before = _f.Dump();

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Brass Belly", Slug = "iron-guts" }));

        Assert.Contains("slug iron-guts is taken by e:2 (deleted: restore it with {\"op\": \"restore\", \"ref\": \"e:2\"} instead?)", ex.Message);
        Assert.Equal(before, _f.Dump());
    }

    [Theory]
    [InlineData("C4", null, "ops item 1 (upsert e:2): e:2 is C3; a register code never changes once assigned")]
    [InlineData(null, "C", "ops item 1 (upsert e:2): e:2 already has code C3; a register code never changes once assigned")]
    public void Upsert_CodeRefusals_NameTheEntityByHandleOnly(string? code, string? autoCode, string expected)
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Visibility = "author", Code = "C3" });

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "e:2", Code = code, AutoCode = autoCode }));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("keras", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("character", true)]
    [InlineData("item", false)]
    public void Upsert_RenameToTheNameOfAnotherEntityOfItsKind_WarnsThatByNameUpsertsBecomeAmbiguous(string otherKind, bool twin)
    {
        _f.Apply(_dm, Op.Upsert("character", "Iron Guts"), Op.Upsert(otherKind, "Brass Belly"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Name = "Brass Belly" });

        Assert.Equal(WriteOutcomes.Updated, Assert.Single(result.Applied).Outcome);
        var warning = result.Warnings.SingleOrDefault(w => w.Kind == WarningKinds.NameTwin);
        Assert.Equal(twin, warning is not null);
        if (twin)
        {
            Assert.Contains("e:2 now shares its name with e:3 (another character)", warning!.Message);
            Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Brass Belly", Summary = "x" }));
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NotFound_ATypoNearAnEntity_SuggestsItOnlyWhenThePartyKnowsItByItsOwnName(bool knownAsAnotherName, bool suggested)
    {
        _f.Apply(_dm, Op.Upsert("character", "Keras", visibility: "party"));
        if (knownAsAnotherName)
        {
            _f.Knowledge.Record(_dm, ["character:keras"], [Op.Knower("party", "met", knownAs: "the old king")], WriteContext.Default);
        }

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = "character:kera", Status = "dead" }));

        Assert.Contains("no entity character:kera in this campaign", ex.Message);
        Assert.Equal(suggested, ex.Message.Contains("character:keras", StringComparison.Ordinal));
    }
}
