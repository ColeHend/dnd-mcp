using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using DndMcp.Tests.CampaignWrite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Everything one perspective can reach through the readers, collected the way a curious client would: every entity
/// handle <c>e:1</c>..<c>e:max</c> and fact handle <c>f:1</c>..<c>f:max</c> of the campaign tried with campaign_get (every
/// include), the whole listing, the knowledge resource, every session and the summary.
///
/// <para>
/// Two uses. <see cref="Results"/> is what the leak rule is asserted on (serialize it all, look for forbidden strings:
/// contract §0). <see cref="OwnText"/> maps each ref the perspective can get to the text the perspective is shown OF THAT
/// ENTITY OR FACT ITSELF (an entity's display name, summary, body, aliases and tags; a fact's text), which is what a
/// search hit must be explained by: a hit whose own shown text does not contain the query word was found through text
/// the perspective cannot see. The facts a disguised entity carries are deliberately not part of its own text, or a
/// disguised item found through its hidden summary would be "explained" by a visible fact that shares the word.
/// </para>
/// </summary>
public sealed class PerspectiveReach
{
    private PerspectiveReach(string perspective, IReadOnlyList<object> results, IReadOnlyDictionary<string, string> ownText)
    {
        Perspective = perspective;
        Results = results;
        OwnText = ownText;
        Strings = results.SelectMany(LeakProbe.StringValues).ToList();
    }

    public string Perspective { get; }

    /// <summary>Every reader result this perspective can obtain.</summary>
    public IReadOnlyList<object> Results { get; }

    /// <summary>Per ref: the text shown of that entity or fact itself.</summary>
    public IReadOnlyDictionary<string, string> OwnText { get; }

    /// <summary>Every string value in <see cref="Results"/> (no JSON property names).</summary>
    public IReadOnlyList<string> Strings { get; }

    public static PerspectiveReach Collect(ScenarioReads reads, WriteFixture f, CampaignRow campaign, string perspective)
    {
        var results = new List<object>();
        var ownText = new Dictionary<string, string>(StringComparer.Ordinal);

        var entityHandles = new List<string>();
        foreach (var seq in f.Query<long>("SELECT seq FROM entity WHERE campaign_id = @id ORDER BY seq", new { id = campaign.Id }))
        {
            if (reads.TryGet(campaign, perspective, "e:" + seq) is { } found)
            {
                var entity = found.Entities.Single();
                entityHandles.Add("e:" + seq);
                ownText[entity.Ref] = string.Join(" \n ", new[] { entity.DisplayName, entity.Summary, entity.BodyMd }
                    .Concat(entity.Aliases.Select(a => a.Alias)).Concat(entity.Tags).Where(s => !string.IsNullOrEmpty(s)));
            }
        }

        var factHandles = new List<string>();
        foreach (var seq in f.Query<long>("SELECT seq FROM fact WHERE campaign_id = @id ORDER BY seq", new { id = campaign.Id }))
        {
            if (reads.TryGet(campaign, perspective, "f:" + seq) is { } found)
            {
                var fact = found.Facts.Single();
                factHandles.Add("f:" + seq);
                ownText[fact.Ref] = fact.Text;
            }
        }

        foreach (var chunk in entityHandles.Concat(factHandles).Chunk(10))
        {
            results.Add(reads.Get(campaign, perspective, EntityIncludes.All, null, chunk));
        }

        results.AddRange(reads.SearchAll(campaign, null, perspective).Pages);
        results.AddRange(reads.KnownTo(campaign, perspective));
        string? cursor = null;
        do
        {
            var page = reads.SessionReader.List(campaign, null, 50, cursor, Domain.Campaign.Perspective.Parse(perspective));
            results.Add(page);
            foreach (var session in page.Sessions)
            {
                results.Add(reads.SessionReader.Get(campaign, session.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Domain.Campaign.Perspective.Parse(perspective)));
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        results.Add(reads.Summaries.Build(campaign, Domain.Campaign.Perspective.Parse(perspective)));
        return new PerspectiveReach(perspective, results, ownText);
    }
}

/// <summary>Helpers for the leak property: the campaign's words, JSON string values, and an FTS matcher with the campaign tokenizer.</summary>
public static partial class LeakProbe
{
    /// <summary>
    /// Every word (3+ letters or digits) of every text the campaign stores, whoever may see it: entity names, summaries,
    /// bodies, secrets, data, sources; aliases of every visibility; tags; fact statements and sources; knowledge
    /// known_as, how and notes; relation labels; objective text; session prep and attendance notes; cross-link notes;
    /// and the names, bodies and aliases of the other campaign's entities cross-linked to this one (the firewall).
    /// Distinct, case-insensitive, in first-seen order.
    /// </summary>
    public static IReadOnlyList<string> CampaignWords(WriteFixture f, CampaignRow campaign)
    {
        const string Sql = """
            SELECT name || ' ' || summary || ' ' || body_md || ' ' || secret_md || ' ' || data || ' ' || coalesce(source, '') FROM entity WHERE campaign_id = @id
            UNION ALL SELECT a.alias FROM entity_alias a JOIN entity e ON e.id = a.entity_id WHERE e.campaign_id = @id
            UNION ALL SELECT name FROM tag WHERE campaign_id = @id
            UNION ALL SELECT statement || ' ' || coalesce(source, '') FROM fact WHERE campaign_id = @id
            UNION ALL SELECT coalesce(known_as, '') || ' ' || coalesce(how, '') || ' ' || coalesce(note, '') FROM knowledge WHERE campaign_id = @id
            UNION ALL SELECT coalesce(label, '') FROM relation WHERE campaign_id = @id
            UNION ALL SELECT o.text FROM objective o JOIN entity e ON e.id = o.quest_id WHERE e.campaign_id = @id
            UNION ALL SELECT prep_md FROM session WHERE campaign_id = @id
            UNION ALL SELECT coalesce(sa.note, '') FROM session_attendance sa JOIN session s ON s.entity_id = sa.session_id WHERE s.campaign_id = @id
            UNION ALL SELECT coalesce(x.note, '') || ' ' || o.name || ' ' || o.body_md || ' ' || o.summary FROM cross_link x
                JOIN entity a ON a.id = x.a_id JOIN entity b ON b.id = x.b_id
                JOIN entity o ON o.id = CASE WHEN a.campaign_id = @id THEN b.id ELSE a.id END
                WHERE a.campaign_id = @id OR b.campaign_id = @id
            UNION ALL SELECT oa.alias FROM cross_link x
                JOIN entity a ON a.id = x.a_id JOIN entity b ON b.id = x.b_id
                JOIN entity_alias oa ON oa.entity_id = CASE WHEN a.campaign_id = @id THEN b.id ELSE a.id END
                WHERE a.campaign_id = @id OR b.campaign_id = @id
            """;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var text in f.Query<string>(Sql, new { id = campaign.Id }))
        {
            foreach (Match match in Word().Matches(text ?? string.Empty))
            {
                if (match.Value.Length >= 3 && seen.Add(match.Value))
                {
                    words.Add(match.Value);
                }
            }
        }

        return words;
    }

    /// <summary>
    /// <see cref="LeakAssert.Clean"/> with one exception: the text of the facts in <paramref name="toldFacts"/> (the
    /// <c>Text</c> and <c>Snippet</c> of every object whose <c>Ref</c> is one of them: a fact hit, a fact page, a linked
    /// fact, a knowledge-resource line) is taken out before the forbidden strings are looked for. A perspective that was
    /// told a fact may read that fact's words and nowhere else: the dm, told f:2 ("The old king's name is Keras."), may
    /// meet "Keras" inside f:2 and must still never meet it as an author alias, a ref, a snippet of another row or a name.
    /// Dropping the word from the dm's forbidden list instead would let all of those through.
    /// </summary>
    public static void CleanExceptToldFacts(object? result, IReadOnlyList<string> forbidden, IReadOnlyCollection<string> toldFacts, string because)
    {
        var json = WithoutToldFactText(result, toldFacts);
        foreach (var word in forbidden)
        {
            Assert.True(json.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0,
                $"{because}: the result contains \"{word}\" outside the text of {string.Join(", ", toldFacts)}:\n{json}");
        }
    }

    /// <summary>
    /// The result serialized as the leak tests serialize it, with the told facts' own text removed (see
    /// <see cref="CleanExceptToldFacts"/>).
    /// </summary>
    public static string WithoutToldFactText(object? result, IReadOnlyCollection<string> toldFacts)
    {
        var json = LeakAssert.Serialize(result);
        if (toldFacts.Count == 0)
        {
            return json;
        }

        var node = JsonNode.Parse(json);
        Strip(node, toldFacts);
        return node?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }

    private static void Strip(JsonNode? node, IReadOnlyCollection<string> toldFacts)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["Ref"] is JsonValue reference && reference.TryGetValue<string>(out var value) && toldFacts.Contains(value))
                {
                    obj.Remove("Text");
                    obj.Remove("Snippet");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Strip(child, toldFacts);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Strip(child, toldFacts);
                }

                break;
        }
    }

    /// <summary>Every string value of an object serialized as the leak tests serialize it (property names excluded).</summary>
    public static IEnumerable<string> StringValues(object? result)
    {
        using var document = JsonDocument.Parse(LeakAssert.Serialize(result));
        var values = new List<string>();
        Walk(document.RootElement, values);
        return values;
    }

    private static void Walk(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, values);
                }

                break;
            case JsonValueKind.String:
                values.Add(element.GetString()!);
                break;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}

/// <summary>
/// An in-memory FTS5 table with the campaign tables' tokenizer (<c>porter unicode61 remove_diacritics 2</c>, contract
/// §3.9), so "does this text contain this word" is answered exactly as campaign_search answers it: the same stemming
/// ("kings" finds "king"), the same folding, the same word boundaries. A hand-rolled stemmer would call a legitimate hit a
/// leak (or a leak legitimate) wherever it and porter disagree.
/// </summary>
public sealed class FtsMatcher : IDisposable
{
    private readonly SqliteConnection _connection;

    public FtsMatcher()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _connection.Open();
        _connection.Execute("CREATE VIRTUAL TABLE corpus USING fts5(text, tokenize = 'porter unicode61 remove_diacritics 2')");
    }

    /// <summary>Adds a text; returns its row id.</summary>
    public long Add(string text) =>
        _connection.ExecuteScalar<long>("INSERT INTO corpus(text) VALUES (@text); SELECT last_insert_rowid();", new { text });

    /// <summary>The rows whose text contains <paramref name="word"/> as an FTS token.</summary>
    public IReadOnlySet<long> RowsMatching(string word) =>
        _connection.Query<long>("SELECT rowid FROM corpus WHERE corpus MATCH @query", new { query = "\"" + word.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" })
            .ToHashSet();

    public void Dispose() => _connection.Dispose();
}
