using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Tests.CampaignRead;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The leak rule as properties over whole scenarios (contract §0, PLAN exit list: "campaign_search with a player
/// perspective never matches secret text"). For every non-author perspective:
/// <list type="number">
/// <item>
/// <b>Search is explained by what the perspective is shown.</b> Every word stored anywhere in the campaign (every name,
/// alias of any visibility, summary, body, secret, fact, known_as, note, and the cross-linked entities of the other
/// campaign) is searched; every entity found must contain that word in the text the perspective is shown OF THAT ENTITY
/// (its display name, summary, body, aliases, tags), and every fact found in the text shown of that fact. A hit that fails
/// this was found through secret_md, an author alias, a disguised entity's true name or a statement behind a known_as.
/// Words are matched with the campaign's own FTS tokenizer (<see cref="FtsMatcher"/>), so stemming never makes a
/// legitimate hit look like a leak.
/// </item>
/// <item>
/// <b>Secret-only words find nothing.</b> A word none of the perspective's reachable results contains finds nothing at
/// all, and the known secret words (Keras, Axiom, Cage, Baal, Third Silence, the ambition, the Silk Isle answer, the
/// seal before T5 …) are among those words: had any reader shown one to the perspective, it would not be.
/// </item>
/// <item>
/// <b>Nothing forbidden anywhere.</b> Every result the perspective can reach (every entity and fact it can open with every
/// include, the listing, the knowledge resource, every session, the summary) and every search result contains none of
/// the perspective's forbidden strings outside the text of a fact it was told (the dm's f:2), and no refusal of a handle
/// it cannot see names one either.
/// </item>
/// <item>
/// <b>Outsiders reach nothing by default.</b> The public, and the old king as a character (no party membership, no
/// knowledge rows of his own), can open nothing at all: party visibility reaches the party's members only (contract §3.3,
/// rule 4), so an NPC never "knows" what the party knows.
/// </item>
/// </list>
/// </summary>
public sealed class BelmakorLeakPropertyTests(BelmakorScenario world) : IClassFixture<BelmakorScenario>
{
    public static TheoryData<string> Perspectives() => new(BelmakorScenario.NonAuthorPerspectives);

    /// <summary>The words every non-author view must find nothing for (their text is secret_md, author aliases, the other campaign).</summary>
    private static IReadOnlyList<string> SecretWords(string perspective)
    {
        var words = new List<string> { "axiom", "cage", "baal", "silence", "cole", "imported" };
        if (perspective != "dm")
        {
            words.Add("keras");
        }

        if (perspective is not ("dm" or "character:belmakor"))
        {
            words.AddRange(["reclaim", "blighted", "outstrips"]);
        }

        return words;
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Search_EveryCampaignWordForANonAuthorPerspective_FindsOnlyWhatItsShownTextContains(string perspective)
    {
        LeakProperty.AssertSearchExplainedByShownText(world.Reads, world.F, world.Campaign, perspective,
            SecretWords(perspective), BelmakorScenario.ForbiddenFor(perspective), BelmakorScenario.ToldFacts(perspective));
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Reach_EverythingANonAuthorPerspectiveCanOpen_ContainsNothingForbidden(string perspective)
    {
        var reach = PerspectiveReach.Collect(world.Reads, world.F, world.Campaign, perspective);

        if (perspective is "public" or "character:old-king")
        {
            Assert.True(reach.OwnText.Count == 0, $"{perspective} reaches {string.Join(", ", reach.OwnText.Keys)}");
        }
        else
        {
            Assert.True(reach.OwnText.Count > 0, $"{perspective}, a party-side view, reaches something");
        }

        foreach (var result in reach.Results)
        {
            LeakProbe.CleanExceptToldFacts(result, BelmakorScenario.ForbiddenFor(perspective), BelmakorScenario.ToldFacts(perspective),
                $"{result.GetType().Name} as {perspective}");
        }
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Get_EveryHandleAPerspectiveCannotSee_IsRefusedWithoutNamingAnythingForbidden(string perspective)
    {
        LeakProperty.AssertRefusalsClean(world.Reads, world.F, world.Campaign, perspective, BelmakorScenario.ForbiddenFor(perspective));
    }

    /// <summary>
    /// The property has teeth: for the author, the same words find the secret entities (the author searches all seven
    /// columns), so "finds nothing" above is the perspective filter at work, not an empty index.
    /// </summary>
    [Theory]
    [InlineData("Keras", "character:old-king")]
    [InlineData("Axiom", "item:thing-he-wants")]
    [InlineData("imported", "character:old-king")]
    [InlineData("blighted", "secret:belmakors-ambition")]
    public void Search_TheSecretWordsAsTheAuthor_FindTheSecretEntities(string word, string expected)
    {
        Assert.Contains(world.Reads.Search(world.Campaign, word).Entities, e => e.Ref == expected);
    }
}

/// <summary>
/// The same leak properties over the One Piece scenario, at the baseline and after the whole scenario (T1-T5 with the
/// gated facts restricted), for the player views and for NPC characters with knowledge of their own. Two things of the
/// world give the properties something to catch that the Belmakor world lacks: the party knows the Protector's testimony
/// under its own phrasing (<see cref="OnePieceScenario.T1"/>), so every reader that prints a fact must print the phrasing
/// and never the statement naming "the Protector"; and Nadar, whom the party sees, has a party-visible relation to the
/// author-only Mistaken One, so every reader that prints relations must drop an edge whose other end the view cannot see.
/// "Protector" stays forbidden to the player views after T5: T5 tells the party the seal, never who the advisor is.
/// </summary>
public sealed class OnePieceLeakPropertyTests
{
    private static readonly string[] AnswerWords = ["founders", "fleet"];

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var perspective in OnePieceScenario.NonAuthorPerspectives)
        {
            data.Add(perspective, false);
            data.Add(perspective, true);
        }

        return data;
    }

    private static bool IsPlayerView(string perspective) => OnePieceScenario.PlayerViews.Contains(perspective);

    private static IReadOnlyList<string> SecretWords(string perspective, bool afterT5)
    {
        if (!IsPlayerView(perspective))
        {
            return AnswerWords;
        }

        var words = new List<string>(AnswerWords) { "keras", "fragment", "simulacrum", "lineage", "mistaken", "dutiful", "protector" };
        if (!afterT5)
        {
            words.AddRange(["baal", "seal"]);
        }

        return words;
    }

    private static IReadOnlyList<string> ForbiddenFor(string perspective, bool afterT5)
    {
        var list = new List<string> { "rope en masse", "founders", "fleet" };
        if (IsPlayerView(perspective))
        {
            list.AddRange(["Keras", "fragment", "simulacrum", "Mistaken", "Dutiful", "lineage", "Battle Advisor", "Protector"]);
            if (!afterT5)
            {
                list.AddRange(["Baal", "seal"]);
            }
        }

        return list;
    }

    private static OnePieceScenario World(bool afterT5)
    {
        var world = OnePieceScenario.Build();
        if (afterT5)
        {
            world.ThroughT4();
            world.T5();
        }

        return world;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Search_EveryCampaignWordForANonAuthorPerspective_FindsOnlyWhatItsShownTextContains(string perspective, bool afterT5)
    {
        using var world = World(afterT5);

        LeakProperty.AssertSearchExplainedByShownText(world.Reads, world.F, world.Campaign, perspective,
            SecretWords(perspective, afterT5), ForbiddenFor(perspective, afterT5));
    }

    /// <summary>
    /// What a perspective outside the party can open at all: exactly the facts its own aware rows name (the Mistaken One's
    /// "had help" row is unaware, so that fact is not among them), and nothing for the public or the arch mage, who have
    /// no rows. None of them is a party member, so party visibility gives them nothing (contract §3.3, rule 4); null for
    /// the party-side views, whose reach is the whole party-visible world.
    /// </summary>
    private static IReadOnlyList<string>? OutsiderReach(string perspective, OnePieceFacts facts) => perspective switch
    {
        "public" or "character:arch-mage" => [],
        "character:mistaken-one" => [facts.TMistaken, facts.TooSlow, facts.TotalFault],
        "character:nadar" => [facts.NadarPlan, facts.Seal],
        "character:protector" => [facts.TProtector],
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reach_EverythingANonAuthorPerspectiveCanOpen_ContainsNothingForbidden(string perspective, bool afterT5)
    {
        using var world = World(afterT5);

        var reach = PerspectiveReach.Collect(world.Reads, world.F, world.Campaign, perspective);

        if (OutsiderReach(perspective, world.Facts) is { } expected)
        {
            Assert.Equal(expected.Order(StringComparer.Ordinal), reach.OwnText.Keys.Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Contains("character:nadar", reach.OwnText.Keys);
        }

        foreach (var result in reach.Results)
        {
            LeakAssert.Clean(result, ForbiddenFor(perspective, afterT5), $"{result.GetType().Name} as {perspective}{(afterT5 ? " after T5" : string.Empty)}");
        }

        LeakProperty.AssertRefusalsClean(world.Reads, world.F, world.Campaign, perspective, ForbiddenFor(perspective, afterT5));
    }

    /// <summary>
    /// After T5 the party is shown the seal (restricted, told): the property's secret words shrink exactly by what it was
    /// told. The testimony it knows in its own words is in its reach (so the properties above do check a fact shown under
    /// a known_as, on every reader), and the advisor's true name is not.
    /// </summary>
    [Fact]
    public void Reach_PartyAfterT5_IsShownTheSealButStillNotTheSilkIsleAnswer()
    {
        using var world = World(afterT5: true);

        var reach = PerspectiveReach.Collect(world.Reads, world.F, world.Campaign, "party");

        var shown = string.Join("\n", reach.Strings);
        Assert.Contains("Baal's seal", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("founders", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OnePieceScenario.TestimonyAsThePartyHeardIt, reach.OwnText[world.Facts.TProtector]);
        Assert.Contains(reach.Results.OfType<Repository.Campaign.Read.PerspectiveKnowledge>().SelectMany(p => p.Facts),
            f => f.Ref == world.Facts.TProtector && f.Text == OnePieceScenario.TestimonyAsThePartyHeardIt);
        Assert.DoesNotContain("Protector", shown, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The assertions the two leak-property classes share.</summary>
internal static class LeakProperty
{
    /// <summary>
    /// Properties 1-3 of <see cref="BelmakorLeakPropertyTests"/> for search, over every word the campaign stores.
    /// <paramref name="toldFacts"/> are the facts whose own text may carry a forbidden string for this perspective
    /// (<see cref="LeakProbe.CleanExceptToldFacts"/>).
    /// </summary>
    public static void AssertSearchExplainedByShownText(ScenarioReads reads, WriteFixture f, CampaignRow campaign, string perspective,
        IReadOnlyList<string> secretWords, IReadOnlyList<string> forbidden, IReadOnlyCollection<string>? toldFacts = null)
    {
        var reach = PerspectiveReach.Collect(reads, f, campaign, perspective);
        using var fts = new FtsMatcher();
        var shownRow = fts.Add(string.Join(" \n ", reach.Strings));
        var ownRows = reach.OwnText.ToDictionary(kv => kv.Key, kv => fts.Add(kv.Value), StringComparer.Ordinal);
        var failures = new List<string>();
        var secretOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = LeakProbe.CampaignWords(f, campaign);
        Assert.True(words.Count > 100, $"the campaign's words were collected ({words.Count})");

        foreach (var word in words)
        {
            var rows = fts.RowsMatching(word);
            var (entities, facts, pages) = reads.SearchAll(campaign, word, perspective);
            var isSecretOnly = !rows.Contains(shownRow);
            if (isSecretOnly)
            {
                secretOnly.Add(word);
                if (entities.Count + facts.Count > 0)
                {
                    failures.Add($"\"{word}\" is in no text {perspective} is shown, yet search found {string.Join(", ", entities.Select(e => e.Ref).Concat(facts.Select(x => x.Ref)))}");
                }
            }

            foreach (var hit in entities)
            {
                if (!ownRows.TryGetValue(hit.Ref, out var row))
                {
                    failures.Add($"\"{word}\" found {hit.Ref}, which {perspective} cannot open");
                }
                else if (!rows.Contains(row))
                {
                    failures.Add($"\"{word}\" found {hit.Ref} (\"{hit.DisplayName}\", tier {hit.MatchTier}) through text {perspective} is not shown of it");
                }
            }

            foreach (var hit in facts)
            {
                if (!ownRows.TryGetValue(hit.Ref, out var row))
                {
                    failures.Add($"\"{word}\" found {hit.Ref}, which {perspective} cannot open");
                }
                else if (!rows.Contains(row))
                {
                    failures.Add($"\"{word}\" found {hit.Ref} through text {perspective} is not shown of it");
                }
            }

            foreach (var page in pages)
            {
                var json = LeakProbe.WithoutToldFactText(page, toldFacts ?? []);
                failures.AddRange(forbidden.Where(w => json.Contains(w, StringComparison.OrdinalIgnoreCase))
                    .Select(w => $"search \"{word}\" as {perspective} contains \"{w}\""));
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} leak(s) for {perspective}:\n" + string.Join("\n", failures.Take(40)));
        var shown = secretWords.Where(w => !secretOnly.Contains(w)).ToList();
        Assert.True(shown.Count == 0, $"{perspective} is shown text containing: {string.Join(", ", shown)}");
    }

    /// <summary>
    /// Every entity (by e:&lt;n&gt; and by its true kind:slug) and every fact of the campaign that the perspective cannot
    /// open is refused with a message that, once the caller's own typed handle is taken out, names nothing forbidden; and
    /// the refusal of a hidden <c>e:&lt;n&gt;</c> or <c>f:&lt;n&gt;</c> is word for word the refusal of a number nothing has,
    /// so probing handles one by one cannot tell "hidden" from "absent".
    /// </summary>
    public static void AssertRefusalsClean(ScenarioReads reads, WriteFixture f, CampaignRow campaign, string perspective, IReadOnlyList<string> forbidden)
    {
        string Refusal(string handle)
        {
            var ex = Assert.Throws<DndInputException>(() =>
                reads.Get(campaign, perspective, new Repository.Campaign.Read.EntityIncludes(), null, handle));
            return ex.Message.Replace(handle, "<handle>", StringComparison.Ordinal);
        }

        var absent = new Dictionary<string, string>(StringComparer.Ordinal) { ["e:"] = Refusal("e:999999"), ["f:"] = Refusal("f:999999") };
        var handles = f.Query<(long Seq, string Kind, string Slug)>("SELECT seq, kind, slug FROM entity WHERE campaign_id = @id", new { id = campaign.Id })
            .SelectMany(e => new[] { "e:" + e.Seq, e.Kind + ":" + e.Slug, e.Slug })
            .Concat(f.Query<long>("SELECT seq FROM fact WHERE campaign_id = @id", new { id = campaign.Id }).Select(s => "f:" + s))
            .ToList();
        var refused = 0;
        foreach (var handle in handles)
        {
            try
            {
                reads.Get(campaign, perspective, new Repository.Campaign.Read.EntityIncludes(), null, handle);
            }
            catch (DndInputException ex)
            {
                refused++;
                LeakAssert.CleanMessage(ex.Message, handle, forbidden, $"refusing {handle} to {perspective}");
                if (absent.TryGetValue(handle[..2], out var nothing))
                {
                    Assert.True(nothing == ex.Message.Replace(handle, "<handle>", StringComparison.Ordinal),
                        $"refusing {handle} to {perspective} differs from refusing a handle nothing has: {ex.Message}");
                }
            }
        }

        Assert.True(refused > 0, $"{perspective} is refused something");
    }
}
