using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: when a batch makes text readable to a player-side view (a fact told to a knower, a visible entity's name,
/// summary, body and aliases, a live or played session's title and recap, a known_as), that text is scanned for words an
/// active gate or reveal rule forbids and for names the view does not use (an author or restricted alias, the true name of
/// an entity it knows only under a disguise); each hit is a warning naming the field, the word and what to do instead,
/// and the write is applied (warn and apply), in a dry run alike; an undo and a redo are checked the same way, and a true
/// name the reader does not use also gets the record call that ends the disguise. Text no player-side view reads (author
/// visibility, prep nobody knows, a planned session) and text a view already read before the batch are not reported, and
/// one text gets one warning per word or name however often it says it. Without it, "Keras
/// told us the white lines are a seal." reached the party's session page while a gate forbade both words, and "Morwen
/// Vashkar poisoned the well." sat on the page of the woman the party knows as "the veiled woman" (reviews U09, L12).
/// </summary>
public sealed class PlayerTextChecksTests : IDisposable
{
    private const string KerasRemedy = "rename it to what the players call it and keep this name as an author alias";

    private readonly WriteFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static IReadOnlyList<PlayerTextWarning> Texts(IReadOnlyList<WriteWarning> warnings) => warnings.OfType<PlayerTextWarning>().ToList();

    private static IReadOnlyList<(string Kind, string Target, string Field, string Word, string Source)> Hits(IReadOnlyList<WriteWarning> warnings) =>
        Texts(warnings).Select(w => (w.Kind, w.Target, w.Field, w.Word, w.Source)).ToList();

    // A hidden-name warning's second remedy (review UR1): the record call that makes the reader's known_as the true name.
    private static string Learned(string campaign, string reader, string target, string? state, string trueName) =>
        $"; or, if {reader} now knows the name, record it: campaign_knowledge {{\"action\": \"record\", \"campaign\": \"{campaign}\", " +
        $"\"targets\": [\"{target}\"], \"knowers\": [{{\"who\": \"{reader}\"{(state is null ? string.Empty : $", \"state\": \"{state}\"")}, " +
        $"\"known_as\": \"{trueName}\"}}]}}";

    // A player campaign where the party knows Keras only as "the old king", and f:1's gate forbids "Keras" and "seal"
    // while the party does not know f:1.
    private CampaignRow Sky()
    {
        var c = _f.Campaign("Sky", role: "player", myCharacter: "Belmakor Silverwind");
        _f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Visibility = "party", KnownBy = [Op.Knower("party", "met", "the old king")] },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The old king's name is Keras.", About = ["character:keras"],
                Gate = new GateSpec { ForbiddenTerms = ["Keras", "seal"] }, KnownBy = [Op.Knower("party", "unaware")],
            });
        return c;
    }

    // A DM campaign where the party met Morwen Vashkar as "the veiled woman".
    private CampaignRow Veil()
    {
        var c = _f.Campaign("Veil");
        _f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Morwen Vashkar", Subtype = "npc", Visibility = "restricted" });
        _f.Knowledge.Record(c, ["character:morwen-vashkar"], [Op.Knower("party", "met", "the veiled woman")], WriteContext.Default);
        return c;
    }

    [Fact]
    public void RecordPast_RecapWithForbiddenWordsAndADisguisedTrueName_WarnsForEachAndKeepsTheRecap()
    {
        var c = Sky();

        var result = _f.Sessions.RecordPast(c, 1, recapMd: "Keras told us the white lines are a seal.");

        Assert.Equal(
        [
            (WarningKinds.ForbiddenWord, "session:1", PlayerTextFields.Recap, "Keras", "f:1"),
            (WarningKinds.ForbiddenWord, "session:1", PlayerTextFields.Recap, "seal", "f:1"),
            (WarningKinds.HiddenName, "session:1", PlayerTextFields.Recap, "Keras", "character:keras"),
        ], Hits(result.Warnings));
        Assert.Equal(
        [
            "session:1's recap uses \"Keras\", forbidden by f:1's gate while it holds: reword the recap (record_past changes it).",
            "session:1's recap uses \"seal\", forbidden by f:1's gate while it holds: reword the recap (record_past changes it).",
            "session:1's recap uses \"Keras\", the true name of character:keras, which party knows as \"the old king\": say \"the old king\" instead" +
            Learned("sky", "party", "character:keras", "met", "Keras") + ".",
        ], Texts(result.Warnings).Select(w => w.Message));
        Assert.All(Texts(result.Warnings), w => Assert.Equal((WarningSeverities.Warning, "party", (int?)null), (w.Severity, w.Reader, w.OpIndex)));
        Assert.Equal("Keras told us the white lines are a seal.", _f.Entity(c, "session:1").BodyMd);
    }

    [Fact]
    public void Upsert_PartyVisiblePlaceNamedWithAForbiddenWordAndAHiddenName_WarnsPerFieldAndCreatesIt()
    {
        var c = Sky();

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "location", Name = "Keras's tomb", Visibility = "party", Summary = "Where the seal is weakest.",
        });

        Assert.Equal(
        [
            (WarningKinds.ForbiddenWord, "location:kerass-tomb", PlayerTextFields.Name, "Keras's", "f:1"),
            (WarningKinds.HiddenName, "location:kerass-tomb", PlayerTextFields.Name, "Keras's", "character:keras"),
            (WarningKinds.ForbiddenWord, "location:kerass-tomb", PlayerTextFields.Summary, "seal", "f:1"),
        ], Hits(result.Warnings));
        Assert.Equal($"location:kerass-tomb's name uses \"Keras's\", forbidden by f:1's gate while it holds: {KerasRemedy}.", Texts(result.Warnings)[0].Message);
        Assert.Equal("location:kerass-tomb's name uses \"Keras's\", the true name of character:keras, which party knows as \"the old king\": " +
                     $"{KerasRemedy}{Learned("sky", "party", "character:keras", "met", "Keras")}.", Texts(result.Warnings)[1].Message);
        Assert.Equal("location:kerass-tomb's summary uses \"seal\", forbidden by f:1's gate while it holds: reword it (secret_md is the place for what " +
                     "only the author may read).", Texts(result.Warnings)[2].Message);
        Assert.All(Texts(result.Warnings), w => Assert.Equal((int?)0, w.OpIndex));
        Assert.Equal(WriteOutcomes.Created, Assert.Single(result.Applied).Outcome);
    }

    [Fact]
    public void Upsert_PartyAliasThatNamesTheTrueName_SaysToMarkTheAliasAuthor()
    {
        var c = Sky();

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "item", Name = "The old crown", Visibility = "party", Aliases = [new AliasSpec { Alias = "the crown of Keras", Visibility = "party" }],
        });

        Assert.Equal(
        [
            "item:the-old-crown's alias \"the crown of Keras\" uses \"Keras\", forbidden by f:1's gate while it holds: mark the alias author.",
            "item:the-old-crown's alias \"the crown of Keras\" uses \"Keras\", the true name of character:keras, which party knows as \"the old king\": " +
            "mark the alias author" + Learned("sky", "party", "character:keras", "met", "Keras") + ".",
        ], Texts(result.Warnings).Select(w => w.Message));
    }

    /// <summary>
    /// Review L12: a fact told to the party without a known_as shows the party its statement, which here names the woman
    /// the party knows as "the veiled woman"; with the party's phrasing there is nothing to say.
    /// </summary>
    [Fact]
    public void Fact_ToldToThePartyNamingADisguisedNpc_SaysToGiveTheirPhrasing()
    {
        var c = Veil();

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "Morwen Vashkar poisoned the well.", About = ["character:morwen-vashkar"], KnownBy = [Op.Knower("party")],
        });
        var phrased = _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "Morwen Vashkar cursed the mill.", About = ["character:morwen-vashkar"],
            KnownBy = [Op.Knower("party", knownAs: "The veiled woman cursed the mill.")],
        });

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.HiddenName, "f:1", PlayerTextFields.Statement, "Morwen Vashkar", "party", "character:morwen-vashkar"),
            (warning.Kind, warning.Target, warning.Field, warning.Word, warning.Reader, warning.Source));
        Assert.Equal("f:1's statement uses \"Morwen Vashkar\", the true name of character:morwen-vashkar, which party knows as \"the veiled woman\": " +
                     "give known_as with the party's phrasing; say \"the veiled woman\" instead" +
                     Learned("veil", "party", "character:morwen-vashkar", "met", "Morwen Vashkar") + ".", warning.Message);
        Assert.Empty(Texts(phrased.Warnings));
    }

    [Fact]
    public void RecordPast_RecapNamingADisguisedNpc_SaysWhatThePartyCallsHer()
    {
        var c = Veil();

        var result = _f.Sessions.RecordPast(c, 1, "The well", recapMd: "Morwen Vashkar watched us from the well.");

        Assert.Equal("session:1's recap uses \"Morwen Vashkar\", the true name of character:morwen-vashkar, which party knows as \"the veiled woman\": " +
                     "say \"the veiled woman\" instead" + Learned("veil", "party", "character:morwen-vashkar", "met", "Morwen Vashkar") + ".",
            Assert.Single(Texts(result.Warnings)).Message);
    }

    /// <summary>
    /// A name counts only when it is written as one: the One Piece campaign's people and powers are ordinary words ("the
    /// Protector", "Raven"), and "a raven" or "the protector of the harbour" is a bird and a duty, not the true name of
    /// someone the party knows as a stranger. A name stored in lower case (the author alias "the grey one") is a name in
    /// any case.
    /// </summary>
    [Theory]
    [InlineData("A raven watched the docks.", null, null, null)]
    [InlineData("The ravens of the harbour carry messages.", null, null, null)]
    [InlineData("The guards argued about who would protect the protector of the harbour.", null, null, null)]
    [InlineData("Raven watched the docks.", "Raven", "character:raven", "the true name of character:raven, which party knows as \"the cloaked stranger\": say \"the cloaked stranger\" instead; or, if party now knows the name, record it: campaign_knowledge {\"action\": \"record\", \"campaign\": \"one-piece\", \"targets\": [\"character:raven\"], \"knowers\": [{\"who\": \"party\", \"state\": \"met\", \"known_as\": \"Raven\"}]}")]
    [InlineData("We met the Protector at dawn.", "Protector", "character:the-protector", "the true name of character:the-protector, which party knows as \"the advisor in Serret\": say \"the advisor in Serret\" instead; or, if party now knows the name, record it: campaign_knowledge {\"action\": \"record\", \"campaign\": \"one-piece\", \"targets\": [\"character:the-protector\"], \"knowers\": [{\"who\": \"party\", \"state\": \"met\", \"known_as\": \"The Protector\"}]}")]
    [InlineData("Then The Grey One left.", "Grey One", "character:raven", "an author alias of character:raven, which party does not use: say \"the cloaked stranger\" instead")]
    [InlineData("Then the grey one left.", "grey one", "character:raven", "an author alias of character:raven, which party does not use: say \"the cloaked stranger\" instead")]
    public void RecordPast_OrdinaryWordsSpelledLikeADisguisedName_AreNotReportedButTheNameIs(string recap, string? word, string? source, string? what)
    {
        var c = _f.Campaign("One Piece");
        _f.Apply(c,
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Raven", Visibility = "restricted", Aliases = [new AliasSpec { Alias = "the grey one", Visibility = "author" }],
                KnownBy = [Op.Knower("party", "met", "the cloaked stranger")],
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "The Protector", Visibility = "restricted", KnownBy = [Op.Knower("party", "met", "the advisor in Serret")],
            });

        var result = _f.Sessions.RecordPast(c, 1, recapMd: recap);

        if (word is null)
        {
            Assert.Empty(Texts(result.Warnings));
        }
        else
        {
            var warning = Assert.Single(Texts(result.Warnings));
            Assert.Equal((WarningKinds.HiddenName, word, source), (warning.Kind, warning.Word, warning.Source));
            Assert.Equal($"session:1's recap uses \"{word}\", {what}.", warning.Message);
        }
    }

    /// <summary>
    /// A known_as is the knower's own name for the entity, so one that contains the true name ("Captain Valdris",
    /// "Valdris's ghost" for Valdris) leaks nothing to that knower, and neither does the bare "Valdris" in a recap it
    /// reads: it says the name itself. Before, the known_as was reported as using the true name, with advice to say the
    /// known_as instead.
    /// </summary>
    [Theory]
    [InlineData("party", "Captain Valdris")]
    [InlineData("party", "Valdris's ghost")]
    [InlineData("character:serif", "Old Valdris")]
    public void Record_AKnownAsContainingTheTrueName_IsTheKnowersOwnNameAndLeaksNothing(string knower, string knownAs)
    {
        var c = _f.Campaign("Veil");
        _f.Apply(c,
            Op.Upsert("character", "Serif", "pc"),
            Op.Link("character:serif", "member_of", "faction:the-party"),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Valdris", Visibility = "party" });

        var known = _f.Knowledge.Record(c, ["character:valdris"], [Op.Knower(knower, "met", knownAs)], WriteContext.Default);
        var recap = _f.Sessions.RecordPast(c, 1, recapMd: $"{knownAs} laughed. Valdris then left.");

        Assert.Empty(Texts(known.Warnings));
        Assert.Empty(Texts(recap.Warnings));
    }

    [Fact]
    public void Record_AKnownAsNamingAnotherDisguisedEntity_SaysWhatTheKnowerCallsIt()
    {
        var c = Veil();

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "Aldric", Visibility = "party", KnownBy = [Op.Knower("party", "met", "Morwen Vashkar's brother")],
        });

        Assert.Equal("party's known_as for character:aldric uses \"Morwen Vashkar's\", the true name of character:morwen-vashkar, which party knows as " +
                     "\"the veiled woman\": say \"the veiled woman\" instead" + Learned("veil", "party", "character:morwen-vashkar", "met", "Morwen Vashkar") + ".",
            Assert.Single(Texts(result.Warnings)).Message);
    }

    /// <summary>
    /// A name the reader uses is fine even when a disguised entity shares it: "Keras" is the party's city as well as the
    /// king the party knows as "the old king", and the recap may name the city.
    /// </summary>
    [Fact]
    public void RecordPast_ANameTheReaderUsesForAnotherEntity_IsNotReported()
    {
        var c = _f.Campaign("Sky", role: "player");
        _f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Visibility = "party", KnownBy = [Op.Knower("party", "met", "the old king")] },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Keras", Visibility = "party" });

        var result = _f.Sessions.RecordPast(c, 1, recapMd: "We sailed to Keras.");

        Assert.Empty(Texts(result.Warnings));
    }

    /// <summary>
    /// Every alias the reader does not use is reported, not only the author's: a restricted alias, the author's own
    /// known_as, and the dm's known_as in a DM campaign (where the dm is the author). The advice is the name the party
    /// sees.
    /// </summary>
    [Theory]
    [InlineData("restricted alias", "Charon Vex rowed us across.", "Charon Vex", "a restricted alias")]
    [InlineData("author", "Death's Boatman waited.", "Death's Boatman", "the author's own name")]
    [InlineData("dm", "Death's Boatman waited.", "Death's Boatman", "the author's own name")]
    public void RecordPast_RecapUsingANameOnlyTheAuthorUses_SaysTheNameThePartyUses(string source, string recap, string word, string what)
    {
        var c = _f.Campaign("Veil");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "The Ferryman", Visibility = "party",
            Aliases = [new AliasSpec { Alias = "Charon Vex", Visibility = "restricted" }],
        });
        if (source != "restricted alias")
        {
            _f.Knowledge.Record(c, ["character:the-ferryman"], [Op.Knower(source, "knows", "Death's Boatman")], WriteContext.Default);
        }

        var result = _f.Sessions.RecordPast(c, 1, recapMd: recap);

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.HiddenName, "party", "character:the-ferryman"), (warning.Kind, warning.Reader, warning.Source));
        Assert.Equal($"session:1's recap uses \"{word}\", {what} of character:the-ferryman, which party does not use: say \"The Ferryman\" instead.",
            warning.Message);
    }

    /// <summary>
    /// The party's members read what the party reads, each with its own names: Serif knows Morwen Vashkar only as "the
    /// widow", so the party's recap naming her is a leak to him, though not to the party.
    /// </summary>
    [Fact]
    public void RecordPast_ANameAMemberKnowsUnderADisguise_WarnsForThatMember()
    {
        var c = _f.Campaign("Sky", role: "player");
        _f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Morwen Vashkar", Visibility = "party" },
            Op.Upsert("character", "Serif", "pc"),
            Op.Link("character:serif", "member_of", "faction:the-party"));
        _f.Knowledge.Record(c, ["character:morwen-vashkar"], [Op.Knower("character:serif", "met", "the widow")], WriteContext.Default);

        var result = _f.Sessions.RecordPast(c, 1, recapMd: "Morwen Vashkar smiled.");

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal("character:serif", warning.Reader);
        Assert.Equal("session:1's recap uses \"Morwen Vashkar\", the true name of character:morwen-vashkar, which character:serif knows as \"the widow\": " +
                     "say \"the widow\" instead" + Learned("sky", "character:serif", "character:morwen-vashkar", "met", "Morwen Vashkar") + ".", warning.Message);
    }

    /// <summary>
    /// A party-visible fact reaches a member as of the session it was established in (FD1): Serif, absent from session 1,
    /// does not read the fact established there, so its naming of the woman he knows as "the widow" is no leak to him;
    /// present, he reads it and is warned about.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fact_PartyVisibleFromASessionAMemberMissed_IsNotReadByThatMember(bool serifPresent)
    {
        var c = _f.Campaign("Veil");
        _f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Morwen Vashkar", Visibility = "party" },
            Op.Upsert("character", "Belmakor", "pc"),
            Op.Upsert("character", "Serif", "pc"),
            Op.Link("character:belmakor", "member_of", "faction:the-party"),
            Op.Link("character:serif", "member_of", "faction:the-party"));
        _f.Knowledge.Record(c, ["character:morwen-vashkar"], [Op.Knower("character:serif", "met", "the widow")], WriteContext.Default);
        _f.Played(c, 1, ("character:belmakor", true), ("character:serif", serifPresent));

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "Morwen Vashkar poisoned the well.", Visibility = "party", CanonStatus = "played", EstablishedSession = 1,
        });

        if (serifPresent)
        {
            var warning = Assert.Single(Texts(result.Warnings));
            Assert.Equal((WarningKinds.HiddenName, "character:serif", "Morwen Vashkar", "character:morwen-vashkar"),
                (warning.Kind, warning.Reader, warning.Word, warning.Source));
        }
        else
        {
            Assert.Empty(Texts(result.Warnings));
        }
    }

    [Fact]
    public void RecordPast_RecapUsingAnAuthorAlias_SaysTheNameThePartyUses()
    {
        var c = _f.Campaign("Sky", role: "player");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "The Old King", Visibility = "party", Aliases = [new AliasSpec { Alias = "Keras", Visibility = "author" }],
        });

        var result = _f.Sessions.RecordPast(c, 1, recapMd: "Keras sent us for the thing he wants.");

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.HiddenName, "Keras", "character:the-old-king"), (warning.Kind, warning.Word, warning.Source));
        Assert.Equal("session:1's recap uses \"Keras\", an author alias of character:the-old-king, which party does not use: say \"The Old King\" instead.",
            warning.Message);
    }

    /// <summary>
    /// Review U09: retracting the row that gave the party its name for Keras shows the party his true name, which f:1's
    /// gate still forbids.
    /// </summary>
    [Fact]
    public void Retract_TheRowThatDisguisedAnEntity_WarnsThatItsTrueNameIsNowShownWithAForbiddenWord()
    {
        var c = Sky();

        var result = _f.Knowledge.Retract(c, ["character:keras"], ["party"], WriteContext.Default);

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.ForbiddenWord, "character:keras", PlayerTextFields.Name, "Keras", "f:1"),
            (warning.Kind, warning.Target, warning.Field, warning.Word, warning.Source));
        Assert.Equal($"character:keras's name uses \"Keras\", forbidden by f:1's gate while it holds: {KerasRemedy}.", warning.Message);
    }

    /// <summary>Review U09: no player-side view reads author-only text, prep nobody knows yet, or a planned session.</summary>
    [Fact]
    public void Write_TextNoPlayerSideViewReads_IsNotReported()
    {
        var c = Sky();
        _f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Visibility = "restricted" });

        var author = _f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Keras's tomb", Visibility = "author", Summary = "Where the seal is weakest." },
            new CampaignOpSpec { Op = "fact", Statement = "Keras sealed Baal.", Visibility = "author", KnownBy = [Op.Knower("party")] },
            new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Summary = "Keras's smith, who forged the seal." });
        var plan = _f.Sessions.Plan(c, 2, "Keras unmasked at the seal");

        Assert.Empty(Texts(author.Warnings));
        Assert.Contains(author.Warnings, w => w.Kind == WarningKinds.AuthorVisibility);
        Assert.Empty(Texts(plan.Warnings));
    }

    /// <summary>
    /// In a DM campaign the dm is the author, not a reader: prep at the role's default visibility, restricted or a secret,
    /// with no knower, is read by no player-side view, so a forbidden word in it is nothing to report.
    /// </summary>
    [Theory]
    [InlineData("location", null)]
    [InlineData("secret", null)]
    [InlineData("fact", null)]
    [InlineData("fact", "author")]
    public void Write_PrepInADmCampaignThatNoKnowerReads_IsNotReported(string kind, string? visibility)
    {
        var c = _f.Campaign("One Piece");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "Baal is bound.", Gate = new GateSpec { ForbiddenTerms = ["seal"] }, KnownBy = [Op.Knower("party", "unaware")],
        });

        var result = _f.Apply(c, kind == "fact"
            ? Op.Fact("The seal weakens with every fruit eaten.", visibility: visibility)
            : new CampaignOpSpec { Op = "upsert", Kind = kind, Name = "The seal chamber", Visibility = visibility, Summary = "Where the seal is weakest." });

        Assert.Empty(Texts(result.Warnings));
    }

    [Fact]
    public void Start_APlannedSessionWhoseTitleNamesADisguisedEntity_WarnsOnceItIsShown()
    {
        var c = Sky();
        _f.Sessions.Plan(c, 2, "Keras unmasked");

        var result = _f.Sessions.Start(c, 2);

        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.PlannedTitle);
        Assert.Equal(
        [
            (WarningKinds.ForbiddenWord, "session:2", PlayerTextFields.Title, "Keras", "f:1"),
            (WarningKinds.HiddenName, "session:2", PlayerTextFields.Title, "Keras", "character:keras"),
        ], Hits(result.Warnings));
        Assert.Equal("session:2's title uses \"Keras\", forbidden by f:1's gate while it holds: pass title to end or record_past to change it.",
            Texts(result.Warnings)[0].Message);
    }

    /// <summary>Only text that is new to a view is reported: re-touching an entity repeats nothing, a changed field is checked.</summary>
    [Fact]
    public void Upsert_TextAViewAlreadyReads_IsNotReportedAgain()
    {
        var c = Sky();
        _f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Keras's tomb", Visibility = "party" });

        var tagged = _f.Apply(c, new CampaignOpSpec { Op = "upsert", Ref = "location:kerass-tomb", Tags = ["crypt"] });
        var known = _f.Knowledge.Record(c, ["location:kerass-tomb"], [Op.Knower("party", "aware")], WriteContext.Default);
        var changed = _f.Apply(c, new CampaignOpSpec { Op = "upsert", Ref = "location:kerass-tomb", Summary = "The seal sleeps here." });

        Assert.Empty(Texts(tagged.Warnings));
        Assert.Empty(Texts(known.Warnings));
        Assert.Equal([(WarningKinds.ForbiddenWord, "location:kerass-tomb", PlayerTextFields.Summary, "seal", "f:1")], Hits(changed.Warnings));
    }

    [Fact]
    public void RecordPast_DryRun_GivesTheSameWarningsAsTheRealRun()
    {
        var c = Sky();

        var dry = _f.Sessions.RecordPast(c, 1, recapMd: "Keras told us the white lines are a seal.", context: new WriteContext { DryRun = true });
        var real = _f.Sessions.RecordPast(c, 1, recapMd: "Keras told us the white lines are a seal.");

        Assert.Equal(3, Texts(dry.Warnings).Count);
        Assert.Equal(Texts(real.Warnings).Select(w => w.Message), Texts(dry.Warnings).Select(w => w.Message));
    }

    /// <summary>
    /// A gate's words are forbidden until its forbidden_until facts are in play; told before, the remedy is the party's
    /// phrasing in the preferred words; told after the until fact is played, there is nothing to say.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fact_ToldWithAWordAGateForbidsUntilAFactIsInPlay_WarnsOnlyBeforeIt(bool axePlayed)
    {
        var c = _f.Campaign("One Piece");
        _f.Played(c, 1);
        _f.Apply(c, Op.Fact("The War God's axe is fully assembled.", "planned", visibility: "party"),
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Eating a devil fruit breaks part of Baal's seal.",
                Gate = new GateSpec { ForbiddenTerms = ["seal"], ForbiddenUntil = ["f:1"], PreferredTerms = ["shell", "wrapping"] },
            });
        if (axePlayed)
        {
            _f.Apply(c, new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = "played", EstablishedSession = 1 });
        }

        var result = _f.Apply(c, new CampaignOpSpec { Op = "fact", Statement = "The white lines are a seal.", KnownBy = [Op.Knower("party")] });

        if (axePlayed)
        {
            Assert.Empty(Texts(result.Warnings));
        }
        else
        {
            Assert.Equal("f:3's statement uses \"seal\", forbidden by f:2's gate while it holds: give known_as with the party's phrasing; " +
                         "say \"shell\" instead (or \"wrapping\").", Assert.Single(Texts(result.Warnings)).Message);
        }
    }

    /// <summary>
    /// A reveal rule holds only while one of its until facts is not in play (contract §3.4), as a gate's words lift once
    /// its forbidden_until facts are: before f:1 is played the party's thread naming Keras is warned about, after it there
    /// is nothing to say. Without the until test, every reveal rule stayed active forever and every party-visible write
    /// warned about words the rule no longer forbids (review MR01: the mutant that made the rule ignore its until facts
    /// survived both suites).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upsert_PartyVisibleTextWithAWordARevealRuleForbidsUntilAFactIsInPlay_WarnsOnlyBeforeIt(bool untilPlayed)
    {
        var c = NoNameRule();
        if (untilPlayed)
        {
            _f.Apply(c, new CampaignOpSpec { Op = "fact", Ref = "f:1", CanonStatus = "played", EstablishedSession = 1 });
        }

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "thread", Name = "The errand", Visibility = "party", BodyMd = "Keras waits at the gate.",
        });

        if (untilPlayed)
        {
            Assert.Empty(Texts(result.Warnings));
        }
        else
        {
            var warning = Assert.Single(Texts(result.Warnings));
            Assert.Equal((WarningKinds.ForbiddenWord, "thread:the-errand", PlayerTextFields.Body, "Keras", "rule:no-name"),
                (warning.Kind, warning.Target, warning.Field, warning.Word, warning.Source));
        }
    }

    /// <summary>
    /// One text gets one warning per rule and word, however often it says the word, and one per disguised entity however
    /// often it names it: printed per occurrence, a long recap repeating one name filled the 40 warnings a result shows
    /// and pushed every other warning off the page (review MR02: the mutants that defeat either dedupe survived).
    /// </summary>
    [Fact]
    public void Upsert_TextUsingAForbiddenTermTwice_WarnsOnce()
    {
        var c = NoNameRule();

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "thread", Name = "The errand", Visibility = "party", BodyMd = "Keras waits at the gate. Keras will not leave.",
        });

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.ForbiddenWord, PlayerTextFields.Body, "Keras", "rule:no-name"), (warning.Kind, warning.Field, warning.Word, warning.Source));
    }

    /// <inheritdoc cref="Upsert_TextUsingAForbiddenTermTwice_WarnsOnce"/>
    [Fact]
    public void RecordPast_RecapNamingADisguisedNpcTwice_WarnsOnce()
    {
        var c = Veil();

        var result = _f.Sessions.RecordPast(c, 1, "The well", recapMd: "Morwen Vashkar watched us from the well. Later Morwen Vashkar left.");

        var warning = Assert.Single(Texts(result.Warnings));
        Assert.Equal((WarningKinds.HiddenName, "Morwen Vashkar", "character:morwen-vashkar"), (warning.Kind, warning.Word, warning.Source));
    }

    // A player campaign with a played session 1, f:1 planned and party-visible, and the reveal rule rule:no-name that
    // forbids "Keras" until f:1 is in play.
    private CampaignRow NoNameRule()
    {
        var c = _f.Campaign("Rules", role: "player");
        _f.Played(c, 1);
        _f.Apply(c, Op.Fact("The old king is unmasked at the gate.", "planned", visibility: "party"));
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name for the old king", Slug = "no-name",
            Data = Op.Data("{\"forbidden_terms\": [\"Keras\"], \"until\": [\"f:1\"]}"),
        });
        return c;
    }

    [Fact]
    public void Upsert_PartyVisibleTextWithAWordARevealRuleForbids_NamesTheRule()
    {
        var c = _f.Campaign("Sky", role: "player");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name for the old king", Slug = "no-name",
            Data = Op.Data("{\"forbidden_terms\": [\"Keras\"], \"forbidden_patterns\": [\"<n> years\"]}"),
        });

        var result = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "thread", Name = "The old king's errand", BodyMd = "He has waited nine hundred years for Keras's return.",
        });

        Assert.Equal(
        [
            (WarningKinds.ForbiddenWord, "thread:the-old-kings-errand", PlayerTextFields.Body, "nine hundred years", "rule:no-name"),
            (WarningKinds.ForbiddenWord, "thread:the-old-kings-errand", PlayerTextFields.Body, "Keras's", "rule:no-name"),
        ], Hits(result.Warnings));
        Assert.StartsWith("thread:the-old-kings-errand's body uses \"nine hundred years\", forbidden by rule:no-name while it holds: ",
            Texts(result.Warnings)[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every player-side knower reads a fact it is told: the party, the table, the public, a character, and the dm of a
    /// player campaign; the author view (and the dm of a DM campaign, who is the author) reads nothing new to warn about.
    /// The phrasing to give is the knower's whose row was written (the party reads the public's row too).
    /// </summary>
    [Theory]
    [InlineData("player", "party", true)]
    [InlineData("player", "table", true)]
    [InlineData("player", "public", true)]
    [InlineData("player", "dm", true)]
    [InlineData("player", "character:serif", true)]
    [InlineData("player", "author", false)]
    [InlineData("dm", "dm", false)]
    [InlineData("dm", "party", true)]
    public void Record_AFactWithAForbiddenWord_WarnsForEveryPlayerSideKnower(string role, string who, bool warns)
    {
        var c = _f.Campaign("Rules", role: role);
        _f.Apply(c,
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name", Slug = "no-name", Data = Op.Data("{\"forbidden_terms\": [\"Keras\"]}"),
            },
            Op.Upsert("character", "Serif", "pc"),
            Op.Fact("Keras watches the harbour."));

        var result = _f.Knowledge.Record(c, ["f:1"], [Op.Knower(who)], WriteContext.Default);

        if (warns)
        {
            var warning = Assert.Single(Texts(result.Warnings));
            Assert.Equal((who == "public" ? "party" : who, "rule:no-name"), (warning.Reader, warning.Source));
            Assert.Equal($"f:1's statement uses \"Keras\", forbidden by rule:no-name while it holds: give known_as with {(who.StartsWith("character:", StringComparison.Ordinal) ? who : "the " + who)}'s phrasing.", warning.Message);
        }
        else
        {
            Assert.Empty(Texts(result.Warnings));
        }
    }

    /// <summary>
    /// Review U09 (undo): undoing the batch that recorded the party's known_as for Keras shows the party his true name,
    /// which f:1's gate forbids, exactly as retracting that row does. The undo is applied and warns, and its dry run warns
    /// the same without changing anything. Before, an undo ran no check at all, and the party's search found "Keras".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Undo_OfTheBatchThatRecordedTheMaskingKnownAs_WarnsThatTheTrueNameIsNowShownWithAForbiddenWord(bool dryRun)
    {
        var c = _f.Campaign("Sky", role: "player", myCharacter: "Belmakor Silverwind");
        _f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Visibility = "party" });
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "The ancient sorcerer king's name is Keras.", About = ["character:keras"],
            Gate = new GateSpec { ForbiddenTerms = ["Keras"] }, KnownBy = [Op.Knower("party", "unaware")],
        });
        var mask = _f.Knowledge.Record(c, ["character:keras"], [Op.Knower("party", "met", "the ancient sorcerer king")], WriteContext.Default);

        var undo = _f.History.Undo(c, mask.BatchId!, new WriteContext { DryRun = dryRun });

        Assert.Empty(Texts(mask.Warnings));
        var warning = Assert.Single(Texts(undo.Warnings));
        Assert.Equal((WarningKinds.ForbiddenWord, "character:keras", PlayerTextFields.Name, "Keras", "party", "f:1", (int?)null),
            (warning.Kind, warning.Target, warning.Field, warning.Word, warning.Reader, warning.Source, warning.OpIndex));
        Assert.Equal($"character:keras's name uses \"Keras\", forbidden by f:1's gate while it holds: {KerasRemedy}.", warning.Message);
        Assert.Equal(dryRun ? 1 : 0, _f.Count("SELECT count(*) FROM knowledge WHERE known_as = 'the ancient sorcerer king'"));
    }

    /// <summary>
    /// An undo that hides text has nothing to say: undoing the retract of the party's known_as puts the disguise back. Its
    /// redo (an undo of the undo) shows the party the true name again, and warns exactly as the retract did.
    /// </summary>
    [Fact]
    public void Undo_ThatPutsADisguiseBack_WarnsNothingAndItsRedoWarnsAsTheRetractDid()
    {
        var c = Sky();
        var retract = _f.Knowledge.Retract(c, ["character:keras"], ["party"], WriteContext.Default);

        var undo = _f.History.Undo(c, retract.BatchId!, WriteContext.Default);
        var redo = _f.History.Undo(c, undo.UndoBatchId!, WriteContext.Default);

        Assert.Empty(undo.Warnings);
        Assert.True(redo.WasRedo);
        Assert.Single(Texts(retract.Warnings));
        Assert.Equal(Texts(retract.Warnings).Select(w => w.Message), Texts(redo.Warnings).Select(w => w.Message));
    }

    /// <summary>A redo of a batch whose text is harmless (no forbidden word, no hidden name) warns nothing, nor does its undo.</summary>
    [Fact]
    public void Redo_OfAHarmlessBatch_WarnsNothing()
    {
        var c = Sky();
        var place = _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "location", Name = "The harbour", Visibility = "party", Summary = "Where the old king's ships wait.",
            Aliases = [new AliasSpec { Alias = "the docks", Visibility = "party" }],
        });

        var undo = _f.History.Undo(c, place.BatchId!, WriteContext.Default);
        var redo = _f.History.Undo(c, undo.UndoBatchId!, WriteContext.Default);

        Assert.Empty(Texts(place.Warnings));
        Assert.Empty(undo.Warnings);
        Assert.True(redo.WasRedo);
        Assert.Empty(redo.Warnings);
        Assert.Equal("Where the old king's ships wait.", _f.Entity(c, "location:the-harbour").Summary);
    }

    /// <summary>
    /// Review UR1: a batch that tells the party the true name of an entity it knows under a disguise may mean exactly that
    /// ("The ancient sorcerer king's name is Keras."), so the warning also prints the campaign_knowledge record call that
    /// sets the party's known_as to the true name, naming the campaign and keeping the party's state. Sent as printed it
    /// ends the disguise: the name is the party's own from then, and a recap that says it is no leak. The call leaves the
    /// state out for knows (its default) and for unrecognized, which disguises whatever the known_as says. Before, the only
    /// advice was to reword the fact to the old name, the opposite of what the author meant.
    /// </summary>
    [Theory]
    [InlineData("met", "which party knows as \"the ancient sorcerer king\": give known_as with the party's phrasing; say \"the ancient sorcerer king\" instead", ", \"state\": \"met\"")]
    [InlineData("knows", "which party knows as \"the ancient sorcerer king\": give known_as with the party's phrasing; say \"the ancient sorcerer king\" instead", "")]
    [InlineData("unrecognized", "which party does not recognise: give known_as with the party's phrasing", "")]
    public void Fact_TellingThePartyADisguisedEntitysTrueName_PrintsTheRecordCallThatEndsTheDisguise(string state, string why, string keep)
    {
        var c = _f.Campaign("Sky", role: "player", myCharacter: "Belmakor Silverwind");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "Keras", Visibility = "party",
            KnownBy = [Op.Knower("party", state, state == "unrecognized" ? null : "the ancient sorcerer king")],
        });

        var told = _f.Apply(c, new CampaignOpSpec
        {
            Op = "fact", Statement = "The ancient sorcerer king's name is Keras.", About = ["character:keras"], KnownBy = [Op.Knower("party")],
        });

        const string Call = "campaign_knowledge {\"action\": \"record\", \"campaign\": \"sky\", \"targets\": [\"character:keras\"], \"knowers\": [{\"who\": \"party\"";
        var call = Call + keep + ", \"known_as\": \"Keras\"}]}";
        var warning = Assert.Single(Texts(told.Warnings));
        Assert.Equal($"f:1's statement uses \"Keras\", the true name of character:keras, {why}; or, if party now knows the name, record it: {call}.",
            warning.Message);

        SendRecordCall(c, call);
        var recap = _f.Sessions.RecordPast(c, 1, recapMd: "Keras, come down.");

        Assert.Empty(Texts(recap.Warnings));
        Assert.Equal(state == "met" ? "met" : "knows",
            _f.Scalar<string>("SELECT state FROM knowledge WHERE entity_id = @id AND knower_kind = 'party'", new { id = _f.Entity(c, "character:keras").Id }));
    }

    /// <summary>
    /// Review UR1: the record call carries the true name as a JSON string, so a name with a quote in it (Björn "Stone"
    /// Fell) still prints a call that parses and, sent as printed, ends the disguise. Pasted in raw, the quote would close
    /// the string early, and the call the warning promises to work as printed could not be sent at all.
    /// </summary>
    [Fact]
    public void RecordPast_RecapNamingADisguisedNpcWhoseNameHasQuotes_PrintsARecordCallThatParsesAndEndsTheDisguise()
    {
        var c = _f.Campaign("Sky", role: "player", myCharacter: "Belmakor Silverwind");
        _f.Apply(c, new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = "Björn \"Stone\" Fell", Visibility = "party", KnownBy = [Op.Knower("party", "heard", "the mountain")],
        });

        var recap = _f.Sessions.RecordPast(c, 1, recapMd: "Björn \"Stone\" Fell came down.");
        var message = Assert.Single(Texts(recap.Warnings)).Message;
        var call = message[message.IndexOf("campaign_knowledge {", StringComparison.Ordinal)..^1];
        SendRecordCall(c, call);
        var again = _f.Sessions.RecordPast(c, 1, recapMd: "Björn \"Stone\" Fell came down the mountain.");

        Assert.EndsWith("\"knowers\": [{\"who\": \"party\", \"state\": \"heard\", \"known_as\": \"Björn \\\"Stone\\\" Fell\"}]}", call);
        Assert.Empty(Texts(again.Warnings));
    }

    /// <summary>
    /// Review U09 (undo): an undo shows the players again whatever the batch it reverses hid, and warns exactly as the
    /// write that first showed the text did, whichever row did the hiding: an entity's row (a delete, a session's new
    /// title), a fact's row (a reworded statement, author visibility), an alias's visibility, a knowledge row (a retract,
    /// which the undo re-inserts and only the logged row can name the target of; a known_as reworded in place, which only
    /// the stored row can), or a session's status (the redo of a start that an undo took back). Each is its own path to
    /// the "before" snapshot: with one missing, that kind of undo showed the party the word or the name with nothing said.
    /// </summary>
    [Theory]
    [InlineData("entity deleted")]
    [InlineData("session retitled")]
    [InlineData("fact reworded")]
    [InlineData("fact made author")]
    [InlineData("alias made author")]
    [InlineData("fact knowledge retracted")]
    [InlineData("entity knowledge retracted")]
    [InlineData("fact known_as reworded")]
    [InlineData("session start undone")]
    public void Undo_OfABatchThatHidTextFromThePlayers_WarnsAsTheWriteThatShowedItDid(string hiding)
    {
        var c = Sky();
        var (shown, hidingBatch) = ShowThenHide(c, hiding);

        var undo = _f.History.Undo(c, hidingBatch, WriteContext.Default);

        Assert.NotEmpty(Texts(shown));
        Assert.Equal(Texts(shown).Select(w => w.Message), Texts(undo.Warnings).Select(w => w.Message));
    }

    // In Sky(): a write that shows the party a forbidden word or a hidden name, then the batch that hides it again.
    private (IReadOnlyList<WriteWarning> Shown, string HidingBatch) ShowThenHide(CampaignRow c, string hiding)
    {
        switch (hiding)
        {
            case "entity deleted":
            {
                var shown = _f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Keras's tomb", Visibility = "party" });
                return (shown.Warnings, _f.Apply(c, new CampaignOpSpec { Op = "delete", Ref = "location:kerass-tomb" }).BatchId!);
            }

            case "session retitled":
            {
                var shown = _f.Sessions.RecordPast(c, 1, "The seal", recapMd: "Quiet.");
                return (shown.Warnings, _f.Sessions.RecordPast(c, 1, "The harbour", recapMd: "Quiet.").BatchId!);
            }

            case "fact reworded" or "fact made author":
            {
                var shown = _f.Apply(c, new CampaignOpSpec { Op = "fact", Statement = "The seal is under the harbour.", Visibility = "party" });
                var hide = hiding == "fact reworded"
                    ? new CampaignOpSpec { Op = "fact", Ref = "f:2", Statement = "The ward is under the harbour." }
                    : new CampaignOpSpec { Op = "fact", Ref = "f:2", Visibility = "author" };
                return (shown.Warnings, _f.Apply(c, hide).BatchId!);
            }

            case "alias made author":
            {
                var shown = _f.Apply(c, new CampaignOpSpec
                {
                    Op = "upsert", Kind = "item", Name = "The old crown", Visibility = "party",
                    Aliases = [new AliasSpec { Alias = "the crown of Keras", Visibility = "party" }],
                });
                return (shown.Warnings, _f.Apply(c, new CampaignOpSpec
                {
                    Op = "upsert", Ref = "item:the-old-crown", Aliases = [new AliasSpec { Alias = "the crown of Keras", Visibility = "author" }],
                }).BatchId!);
            }

            case "fact knowledge retracted":
            {
                var shown = _f.Apply(c, new CampaignOpSpec
                {
                    Op = "fact", Statement = "The seal is under the harbour.", Visibility = "restricted", KnownBy = [Op.Knower("party")],
                });
                return (shown.Warnings, _f.Knowledge.Retract(c, ["f:2"], ["party"], WriteContext.Default).BatchId!);
            }

            case "entity knowledge retracted":
            {
                var shown = _f.Apply(c, new CampaignOpSpec
                {
                    Op = "upsert", Kind = "location", Name = "The seal tower", Visibility = "restricted", KnownBy = [Op.Knower("party")],
                });
                return (shown.Warnings, _f.Knowledge.Retract(c, ["location:the-seal-tower"], ["party"], WriteContext.Default).BatchId!);
            }

            case "fact known_as reworded":
            {
                _f.Apply(c, new CampaignOpSpec
                {
                    Op = "fact", Statement = "Keras built the tower.", About = ["character:keras"],
                    KnownBy = [Op.Knower("party", "knows", "the old king built the tower")],
                });
                var shown = _f.Knowledge.Record(c, ["f:2"], [Op.Knower("party", "knows", "Keras built the tower.")], WriteContext.Default);
                return (shown.Warnings,
                    _f.Knowledge.Record(c, ["f:2"], [Op.Knower("party", "knows", "someone built the tower")], WriteContext.Default).BatchId!);
            }

            case "session start undone":
            {
                _f.Sessions.Plan(c, 1, "The seal");
                var shown = _f.Sessions.Start(c, 1);
                return (shown.Warnings, _f.History.Undo(c, shown.BatchId!, WriteContext.Default).UndoBatchId!);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(hiding), hiding, null);
        }
    }

    // Sends a printed campaign_knowledge record call as the tool does: the campaign it names, its targets and knowers.
    private void SendRecordCall(CampaignRow c, string call)
    {
        using var sent = JsonDocument.Parse(call["campaign_knowledge ".Length..]);
        Assert.Equal((c.Slug, "record"), (sent.RootElement.GetProperty("campaign").GetString(), sent.RootElement.GetProperty("action").GetString()));
        var knowers = sent.RootElement.GetProperty("knowers").EnumerateArray().Select(k => (KnowerSpec?)new KnowerSpec
        {
            Who = k.GetProperty("who").GetString(),
            State = k.TryGetProperty("state", out var state) ? state.GetString() : null,
            KnownAs = k.GetProperty("known_as").GetString(),
        }).ToList();
        _f.Knowledge.Record(c, sent.RootElement.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToList(), knowers, WriteContext.Default);
    }
}
