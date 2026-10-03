using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-onepiece.md</c> §3 rows 31-40, perspective-safe search and get, on the world the write path
/// built. The party finds Silk Isle and the question about it but nothing of the withheld answer (G.O.D.S. Co.'s founders,
/// the rope, the fleet: secret_md and a lean author fact), never the Protector by his true name, never the seal before it
/// is told, and after T5 exactly the two facts it was told. Each of those words sits in a column or row the table must
/// not read; a search that reached one would hand the players the next arc.
/// </summary>
public sealed class OnePieceSearchScenarioTests : IDisposable
{
    private static readonly string[] SilkIsleSecrets = ["rope en masse", "founders", "fleet", "withheld", "Direction"];

    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private SearchResult Search(string query, string perspective = "author") => _world.Reads.Search(_world.Campaign, query, perspective);

    [Theory]
    [InlineData(31, "party")]
    [InlineData(36, "table")]
    public void Search_Rows31And36SilkIsle_FindsTheIsleAndTheQuestionWithoutTheAnswer(int row, string perspective)
    {
        var result = Search("silk isle", perspective);

        Assert.True(new[] { "location:silk-isle", "question:q7" }.SequenceEqual(result.Entities.Select(e => e.Ref).Order(StringComparer.Ordinal)),
            $"row {row}: {string.Join(", ", result.Entities.Select(e => e.Ref))}");
        Assert.Equal("open", result.Entities.Single(e => e.Ref == "question:q7").Status);
        Assert.DoesNotContain(result.Facts, f => f.Ref == _world.Facts.Q7Answer);
        Assert.Empty(result.Facts);
        Assert.All(result.Entities, e => Assert.DoesNotContain("G.O.D.S.", e.Snippet ?? string.Empty, StringComparison.Ordinal));
        LeakAssert.Clean(result, SilkIsleSecrets, $"row {row} as {perspective}");
    }

    /// <summary>
    /// Row 32, pinned as the contract answers it (§3.9: "when nothing matches every word, retry with AnyTerms and say
    /// so"). The golden's "empty" predates that rule: nothing the party sees matches both words, so the any-word retry
    /// finds the question through the one word of its visible body ("rope- and weave-work"), flagged partial, and nothing
    /// of the secret answer comes with it. Neither word is ever matched through secret text.
    /// </summary>
    [Fact]
    public void Search_Row32RopeFleetAsParty_IsOnlyAPartialMatchThroughVisibleText()
    {
        var result = Search("rope fleet", "party");

        Assert.True(result.PartialMatch);
        var hit = Assert.Single(result.Entities);
        Assert.Equal(("question:q7", SnippetFields.Body), (hit.Ref, hit.SnippetFrom));
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, [.. SilkIsleSecrets, "G.O.D.S."], "row 32");
    }

    [Fact]
    public void Search_Row32RopeFleetAsAuthor_MatchesBothWordsThroughTheSecrets()
    {
        var result = Search("rope fleet");

        Assert.False(result.PartialMatch);
        Assert.Contains(result.Entities, e => e.Ref == "faction:gods-co" && e.SnippetFrom == SnippetFields.Secret);
        Assert.Contains(result.Facts, f => f.Ref == _world.Facts.Q7Answer);
    }

    [Fact]
    public void Search_Row33GodsFoundersAsAuthor_FindsTheFactionTheQuestionAndTheLeanAnswer()
    {
        var result = Search("G.O.D.S. founders");

        Assert.False(result.PartialMatch);
        Assert.Contains(result.Entities, e => e.Ref == "faction:gods-co");
        Assert.Contains(result.Entities, e => e.Ref == "question:q7");
        Assert.Contains(result.Facts, f => f.Ref == _world.Facts.Q7Answer && f.CanonStatus == "lean");
    }

    /// <summary>
    /// Row 33 for the party, pinned as the contract answers it: "founders" is secret-only, so nothing matches both words and
    /// the partial retry finds the faction by its visible name alone; the founders and the answer never come with it. The
    /// party sees the faction under <c>e:&lt;n&gt;</c>: its slug <c>gods-co</c> is not what its shown name "G.O.D.S. Co."
    /// slugifies to (contract §3.2's ref rule), so the stored slug is never printed to a non-author view.
    /// </summary>
    [Fact]
    public void Search_Row33GodsFoundersAsParty_NeverMatchesTheSecretWord()
    {
        var seq = _world.F.Entity(_world.Campaign, "faction:gods-co").Seq;

        var result = Search("G.O.D.S. founders", "party");

        Assert.True(result.PartialMatch);
        var faction = Assert.Single(result.Entities);
        Assert.Equal(("e:" + seq, "G.O.D.S. Co.", (string?)null), (faction.Ref, faction.DisplayName, faction.Snippet));
        Assert.DoesNotContain(result.Facts, f => f.Ref == _world.Facts.Q7Answer);
        Assert.Empty(Search("founders", "party").Entities);
        LeakAssert.Clean(result, SilkIsleSecrets, "row 33");
    }

    [Theory]
    [InlineData(34, "seal")]
    [InlineData(38, "lineage")]
    [InlineData(39, "protector")]
    [InlineData(39, "Protector")]
    public void Search_Rows34And38And39SecretOrDisguisedAsParty_FindsNothing(int row, string query)
    {
        var result = Search(query, "party");

        Assert.True(result.Entities.Count == 0 && result.Facts.Count == 0, $"row {row}: {LeakAssert.Serialize(result)}");
    }

    /// <summary>Row 34: the author finds the secret entity, the gated fact and Nadar's plan for the same word.</summary>
    [Fact]
    public void Search_Row34SealAsAuthor_FindsTheSecretAndBothFacts()
    {
        var result = Search("seal");

        Assert.Contains(result.Entities, e => e.Ref == OnePieceScenario.Secret);
        Assert.Contains(result.Facts, f => f.Ref == _world.Facts.Seal);
        Assert.Contains(result.Facts, f => f.Ref == _world.Facts.NadarPlan);
    }

    [Fact]
    public void Search_Row35MoonGoddessProtectingAsParty_FindsTheFactThePartyKnows()
    {
        var result = Search("moon goddess protecting", "party");

        var fact = Assert.Single(result.Facts);
        Assert.Equal(_world.Facts.ShieldedChild, fact.Ref);
        Assert.Null(fact.KnownBy);
        Assert.False(result.PartialMatch);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void Get_Row37WithheldQuestionAsAPlayerView_IsOpenWithNoSecretAndNoAnswer(string perspective)
    {
        var result = _world.Reads.Get(_world.Campaign, perspective, EntityIncludes.All, null, "question:q7");

        var q7 = Assert.Single(result.Entities);
        Assert.Equal(("open", "Q7"), (q7.Status, q7.Code));
        Assert.Null(q7.Author);
        Assert.Empty(q7.Facts!);
        LeakAssert.Clean(result, [.. SilkIsleSecrets, "lean", "🔒"], $"row 37 as {perspective}");
    }

    [Fact]
    public void Get_Row37WithheldQuestionAsAuthor_ShowsWithheldTheSecretAndTheLeanAnswer()
    {
        var q7 = _world.Reads.Get(_world.Campaign, "author", EntityIncludes.All, null, "question:q7").Entities.Single();

        Assert.Equal("withheld", q7.Status);
        Assert.StartsWith("Direction (not yet locked)", q7.Author!.SecretMd, StringComparison.Ordinal);
        Assert.Contains(q7.Facts!, f => f.Ref == _world.Facts.Q7Answer && f.Author!.CanonStatus == "lean");
    }

    /// <summary>
    /// Row 37's "showing withheld would tell the players Cole has an answer", for counts (contract §3.2: no trace of what
    /// is hidden): every count a player view gets counts only what it is shown. Its summary counts one open question (Q7,
    /// withheld shown as open), never the author's design questions Q21 and Q22, and has no author part; its session list
    /// totals the four played sessions, never the planned 8-12. The author's summary counts all three questions.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void Summary_Row37APlayerViewsCounts_CountOnlyWhatItIsShown(string perspective)
    {
        var view = Domain.Campaign.Perspective.Parse(perspective);

        var summary = _world.Reads.Summaries.Build(_world.Campaign, view);
        var sessions = _world.Reads.SessionReader.List(_world.Campaign, null, 50, null, view);
        var author = _world.Reads.Summaries.Build(_world.Campaign);

        Assert.Equal(1, summary.OpenQuestions);
        Assert.Null(summary.Author);
        Assert.Equal(summary.OpenThreads.Count, summary.OpenThreadsTotal);
        Assert.Equal((4, 4), (sessions.Sessions.Count, sessions.Total));
        Assert.Equal(3, author.OpenQuestions + author.Author!.WithheldQuestions + author.Author.LeanQuestions);
        LeakAssert.Clean(summary, ["withheld", "lean", "Q21", "Q22", "lineage", "Keras–Baal"], $"the summary as {perspective}");
    }

    /// <summary>
    /// Row 39: the disguised Protector is rendered as "the advisor in Serret" under e:&lt;n&gt;, his slug finds nothing
    /// (worded like a handle that names nothing), and the name the party knows finds him. The research world gives him no
    /// summary, body, status or party alias, so the null summary here holds even for a reader that printed a stored
    /// summary; <see cref="Get_Row39TheAdvisorWithEveryFieldFilledIn_ShowsOnlyTheDisguise"/> is the test of the disguise's shape.
    /// </summary>
    [Fact]
    public void Get_Row39ProtectorAsParty_IsTheAdvisorInSerretAndHisSlugNamesNothing()
    {
        var seq = _world.F.Entity(_world.Campaign, "character:protector").Seq;

        var advisor = _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, "e:" + seq);
        var bySlug = Assert.Throws<DndInputException>(() => _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, "character:protector"));
        var byName = Search("advisor", "party");

        var entity = Assert.Single(advisor.Entities);
        Assert.Equal(("e:" + seq, "the advisor in Serret"), (entity.Ref, entity.DisplayName));
        Assert.Null(entity.Summary);
        LeakAssert.Clean(advisor, ["Protector", "Keras", "fragment", "simulacrum", "Battle Advisor", "unrecognized"], "row 39");
        Assert.Contains("nothing by that handle for this perspective", bySlug.Message, StringComparison.Ordinal);
        LeakAssert.CleanMessage(bySlug.Message, "character:protector", ["advisor", "Serret", "e:" + seq], "row 39 by slug");
        Assert.Equal("e:" + seq, Assert.Single(byName.Entities).Ref);
    }

    /// <summary>
    /// Row 39 with the disguise under load (contract §3.2: a disguised entity shows only its display name, kind, e:&lt;n&gt;
    /// and the facts the view knows): the author gives the Protector a status, a summary, a body, a party-visible alias and
    /// a tag, every one naming what he is. The party's get and search still show "the advisor in Serret" and nothing else
    /// (no status: a disguised entity has none to show), and none of those fields finds him: the alias and the tag are
    /// party-visible text, but they are his own text, and a disguised entity is never found through its own text.
    /// </summary>
    [Fact]
    public void Get_Row39TheAdvisorWithEveryFieldFilledIn_ShowsOnlyTheDisguise()
    {
        var seq = _world.F.Entity(_world.Campaign, "character:protector").Seq;
        _world.F.Apply(_world.Campaign, new Domain.Campaign.Ops.CampaignOpSpec
        {
            Op = "upsert", Ref = "character:protector", Status = "alive", Summary = "Serret's Battle Advisor, a Keras fragment.",
            BodyMd = "The Protector kept the Cage engaging.", Tags = ["fragment"],
            Aliases = [new Domain.Campaign.Ops.AliasSpec { Alias = "the Protector of Serret", Visibility = "party" }],
        });

        var entity = _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, "e:" + seq).Entities.Single();
        var hit = Assert.Single(Search("advisor", "party").Entities);

        Assert.Equal(("e:" + seq, "character", "the advisor in Serret"), (entity.Ref, entity.Kind, entity.DisplayName));
        Assert.Null(entity.Status);
        Assert.Null(entity.Summary);
        Assert.Null(entity.BodyMd);
        Assert.Null(entity.Subtype);
        Assert.Empty(entity.Aliases);
        Assert.Empty(entity.Tags);
        Assert.Equal(("e:" + seq, (string?)null), (hit.Ref, hit.Status));
        Assert.Empty(Search("protector", "party").Entities);
        Assert.Empty(Search("fragment", "party").Entities);
        LeakAssert.Clean(entity, ["Protector", "Keras", "fragment", "Battle", "alive", "Cage"], "row 39, every field filled in");
        LeakAssert.Clean(hit, ["Protector", "Keras", "fragment", "Battle", "alive"], "row 39's search hit");
        Assert.Equal("alive", _world.Reads.Get(_world.Campaign, "author", new EntityIncludes(), null, "character:protector").Entities.Single().Status);
    }

    /// <summary>
    /// Contract §3.2 and §3.9 on the fact side (rows 34-35's filter with a knower's own phrasing): a fact the party knows in
    /// its own words (the deciding row's known_as) is shown and found only as those words, by search, by get and by the
    /// knowledge resource (campaign://one-piece/knowledge/party). The statement's words ("moonlight", "shards", the Cage)
    /// never find it and never appear on its page or its resource line (other facts the party knows may still share a
    /// word: "shards" finds the fruit-and-shard fact), while the author still sees both.
    /// </summary>
    [Fact]
    public void SearchAndGet_AFactThePartyKnowsInItsOwnWords_IsFoundAndShownOnlyInThoseWords()
    {
        _world.F.Knowledge.Record(_world.Campaign, [_world.Facts.IllusionRewatched],
            [new Domain.Campaign.Ops.KnowerSpec { Who = "party", State = "knows", KnownAs = "the re-watched vision" }],
            Repository.Campaign.Write.WriteContext.For(9));

        var byPhrasing = Search("re-watched vision", "party");
        var fact = _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, _world.Facts.IllusionRewatched).Facts.Single();
        var author = _world.Reads.Get(_world.Campaign, "author", EntityIncludes.All, null, _world.Facts.IllusionRewatched).Facts.Single();

        Assert.Equal(_world.Facts.IllusionRewatched, Assert.Single(byPhrasing.Facts).Ref);
        Assert.DoesNotContain(Search("shards", "party").Facts, f => f.Ref == _world.Facts.IllusionRewatched);
        Assert.Empty(Search("moonlight wrapping", "party").Facts);
        Assert.Equal("the re-watched vision", fact.Text);
        LeakAssert.Clean(fact, ["moonlight", "shards", "Cage", "specific question"], "a fact known by its own phrasing");
        Assert.StartsWith("Re-watched with a specific question", author.Text, StringComparison.Ordinal);
        Assert.Contains(Search("shards").Facts, f => f.Ref == _world.Facts.IllusionRewatched);
        var resource = _world.Reads.KnownTo(_world.Campaign, "party");
        Assert.Equal("the re-watched vision", resource.SelectMany(p => p.Facts).Single(f => f.Ref == _world.Facts.IllusionRewatched).Text);
        LeakAssert.Clean(resource, ["moonlight", "shards", "specific question"], "the knowledge resource of a fact known by its own phrasing");
    }

    /// <summary>
    /// T1 as the scenario writes it (<see cref="OnePieceScenario.T1"/>, the party's own words): the party is shown and finds
    /// the testimony as "the advisor in Serret said …", on its page, in its knowledge resource and in the table's view
    /// derived from the party's row, and "protector" still finds nothing. The contrast is W's step, the bare statement
    /// with no phrasing: then the fact itself tells the party the advisor's true name, and its search for "protector"
    /// finds it. That is why the scenario phrases T1, as it re-titles session 1.
    /// </summary>
    [Fact]
    public void SearchAndGet_T1InThePartysOwnWords_ShowsTheTestimonyWithoutTheProtectorsName()
    {
        _world.T1();

        var found = Search("advisor serret", "party");
        var fact = _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, _world.Facts.TProtector).Facts.Single();
        var table = _world.Reads.Get(_world.Campaign, "table", EntityIncludes.All, null, _world.Facts.TProtector).Facts.Single();
        var resource = _world.Reads.KnownTo(_world.Campaign, "party");

        Assert.Contains(found.Facts, f => f.Ref == _world.Facts.TProtector);
        Assert.Equal((OnePieceScenario.TestimonyAsThePartyHeardIt, OnePieceScenario.TestimonyAsThePartyHeardIt), (fact.Text, table.Text));
        Assert.Empty(Search("protector", "party").Facts);
        Assert.Empty(Search("protector", "party").Entities);
        LeakAssert.Clean(new object[] { found, fact, table, resource }, ["Protector", "kept the Cage engaging"], "T1 in the party's words");
    }

    /// <summary>
    /// The contrast the scenario's T1 exists for: W's step (a party row with no known_as) is written as asked, and from then
    /// on the fact's own statement tells the party the advisor's true name. The write path cannot know the party's words;
    /// the caller has to give them.
    /// </summary>
    [Fact]
    public void Search_T1AsWsFixtureWritesIt_TellsThePartyTheProtectorsNameThroughTheStatement()
    {
        _world.F.Knowledge.Record(_world.Campaign, [_world.Facts.TProtector], [Op.Knower("party")], Repository.Campaign.Write.WriteContext.For(8));

        var result = Search("protector", "party");

        Assert.Equal(_world.Facts.TProtector, Assert.Single(result.Facts).Ref);
        Assert.StartsWith("The Protector kept the Cage engaging", result.Facts[0].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contract §3.2's strictest-endpoint rule on the scenario's one edge to a hidden entity: Nadar, whom the party sees,
    /// watches the author-only Mistaken One through a party-visible relation. The party's page of Nadar has no relations at
    /// all (no ref, no label, no count), while the author sees the edge.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void Get_NadarsRelationToAnAuthorOnlyEntity_IsNotShownToAPlayerView(string perspective)
    {
        var nadar = _world.Reads.Get(_world.Campaign, perspective, EntityIncludes.All, null, "character:nadar");
        var author = _world.Reads.Get(_world.Campaign, "author", EntityIncludes.All, null, "character:nadar").Entities.Single();

        Assert.Empty(nadar.Entities.Single().Relations!);
        LeakAssert.Clean(nadar, ["mistaken", "watches", "fragment"], $"Nadar's page as {perspective}");
        var edge = Assert.Single(author.Relations!);
        Assert.Equal(("watches", "character:mistaken-one"), (edge.Rel, edge.Other.Ref));
    }

    /// <summary>
    /// Row 39's precondition, and why the scenario re-titles session 1: the disguise hides the ENTITY's true name, not
    /// every text the author writes. A played session is party-visible, so W's fixture title "Small town — the dragon and
    /// the Protector" is found by the party's search for "protector" (as the session, never as the character).
    /// </summary>
    [Fact]
    public void Search_Row39APartyVisibleTitleNamingTheProtector_FindsTheSessionNeverTheCharacter()
    {
        _world.F.Sessions.RecordPast(_world.Campaign, 1, "Small town — the dragon and the Protector");

        var result = Search("protector", "party");

        var hit = Assert.Single(result.Entities);
        Assert.Equal(("session", "Small town — the dragon and the Protector"), (hit.Kind, hit.DisplayName));
        Assert.Empty(result.Facts);
    }

    /// <summary>
    /// Row 40, with the gated facts restricted (the contract §8 decision this scenario's fixture makes): after T5 a party
    /// search for "seal" returns exactly @seal and @nadar-plan; the author-visibility secret entity is still not returned.
    /// </summary>
    [Fact]
    public void Search_Row40AfterT5_ReturnsBothRevealedFactsButNotTheSecret()
    {
        _world.ThroughT4();
        _world.T5();

        var result = Search("seal", "party");

        Assert.Equal(new[] { _world.Facts.Seal, _world.Facts.NadarPlan }.Order(StringComparer.Ordinal), result.Facts.Select(f => f.Ref).Order(StringComparer.Ordinal));
        Assert.Empty(result.Entities);
        Assert.All(result.Facts, f => Assert.Null(f.KnownBy));
    }

    /// <summary>
    /// Row 40, the reason the fixture differs from the research: with author visibility (W's fixture and the research
    /// table) the same reveal is written and the party row exists, yet the party can never be shown the fact, because
    /// author visibility is absolute (contract §3.2). Pinned so a change to that rule is a decision, not an accident.
    /// </summary>
    [Fact]
    public void Search_Row40WithAuthorVisibility_TheRevealIsWrittenButNeverShown()
    {
        _world.ThroughT4();
        _world.F.Apply(_world.Campaign,
            new Domain.Campaign.Ops.CampaignOpSpec { Op = "fact", Ref = _world.Facts.Seal, Visibility = "author" },
            new Domain.Campaign.Ops.CampaignOpSpec { Op = "fact", Ref = _world.Facts.NadarPlan, Visibility = "author" });

        var t5 = _world.T5();

        Assert.Contains(t5.Warnings, w => w.Kind == Repository.Campaign.Write.WarningKinds.AuthorVisibility);
        // The party row is written (its state is "knows"), yet the ledger's party cell says what every party read does.
        var cell = _world.Reads.Cell(_world.Campaign, _world.Facts.Seal, "party");
        Assert.Equal((Standings.AuthorOnly, "author only", "knows"), (cell.Standing, cell.Text, cell.State));
        Assert.Empty(Search("seal", "party").Facts);
        Assert.Null(_world.Reads.TryGet(_world.Campaign, "party", _world.Facts.Seal));
    }
}
