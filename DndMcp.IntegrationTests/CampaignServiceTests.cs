using DndMcp.Domain.Core;
using DndMcp.Hosting;
using DndMcp.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: the host's campaign handle opens nothing until asked, resolves the campaign a call is about per contract
/// §3.7, keeps this process's choice apart from the persisted active campaign, and never lets a missing or unreadable
/// campaigns.db break the tools that only read defaults from it.
///
/// <para>
/// Why it fails silently: a default lookup that created campaigns.db would leave a database behind for every user who
/// only reads rules; one that threw would break rules_search for everyone with a damaged file; and a process choice
/// written only to app_state would let a second Claude session flip the first one's campaign mid-conversation.
/// </para>
/// </summary>
public sealed class CampaignServiceTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("dnd-mcp-campaign-service-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private CampaignService NewService() =>
        new(new DndMcpServerOptions { DataDirectory = _data }, NullLogger<CampaignService>.Instance,
            NullLogger<Repository.Campaign.CampaignDatabase>.Instance);

    [Fact]
    public void Defaults_NoDatabase_IsNullAndCreatesNothing()
    {
        using var service = NewService();

        Assert.Null(service.Defaults());
        Assert.Null(service.DefaultEdition(out var slug));
        Assert.Null(slug);
        Assert.False(File.Exists(Path.Combine(_data, "campaigns.db")));
    }

    [Fact]
    public void Defaults_UnreadableFile_IsNullRatherThanAnError()
    {
        File.WriteAllText(Path.Combine(_data, "campaigns.db"), "this is not a SQLite database, just some bytes");
        using var service = NewService();

        Assert.Null(service.Defaults());
    }

    /// <summary>
    /// FH2 (R09): "campaigns.db exists but could not be read" is told apart from "no campaign", with the file, so the rules
    /// tools can say their 2024 answer is not the campaign's choice; no file at all is no campaign.
    /// </summary>
    [Fact]
    public void ReadDefaults_UnreadableFile_NamesTheFileButNoFileIsNoCampaign()
    {
        using var none = NewService();
        Assert.Equal(new CampaignDefaultsReading(null, null), none.ReadDefaults());

        var path = Path.Combine(_data, "campaigns.db");
        File.WriteAllText(path, "this is not a SQLite database, just some bytes");
        using var service = NewService();

        Assert.Equal(new CampaignDefaultsReading(null, path), service.ReadDefaults());
    }

    /// <summary>
    /// Kills E04 (FH10, M03): with no home directory and no data-directory override, campaigns.db has no path. The service
    /// says so with the store's own message (what to set), and the defaults the rules tools read are simply absent, as is
    /// any "could not read" note: there is no file to have failed. Escaping as InvalidOperationException, which the call-tool
    /// filter does not translate, every campaign, rules, encounter and balance call would end in the SDK's generic error.
    /// </summary>
    [Fact]
    public void Database_NoPathForCampaignsDb_IsTheStoresMessageAndThereAreNoDefaults()
    {
        using var service = new CampaignService(new DndMcpServerOptions { Paths = () => new DndMcpPaths(_ => null, homeDirectory: null) },
            NullLogger<CampaignService>.Instance, NullLogger<Repository.Campaign.CampaignDatabase>.Instance);

        var ex = Assert.Throws<Repository.Campaign.CampaignStoreUnavailableException>(() => service.Database);

        Assert.StartsWith("dnd-mcp cannot decide where campaigns.db lives", ex.Message, StringComparison.Ordinal);
        Assert.Null(service.Defaults());
        Assert.Null(service.DefaultEdition(out _));
        Assert.Equal(new CampaignDefaultsReading(null, null), service.ReadDefaults());
    }

    /// <summary>
    /// FH1 (R04): a SQLite failure a person can fix (busy, damaged, not a database, I/O, full, read-only) raised by a
    /// statement on this process's campaigns.db becomes the store's message naming the file, never with SQL; any other code
    /// stays unmapped (a bug), and so does every code before this process ever opened campaigns.db (it cannot be the cause).
    /// </summary>
    [Theory]
    [InlineData(5, "is locked by another dnd-mcp process")]
    [InlineData(11, "is damaged or is not a dnd-mcp database")]
    [InlineData(26, "is damaged or is not a dnd-mcp database")]
    [InlineData(10, "A disk I/O error stopped dnd-mcp")]
    [InlineData(1, null)]
    [InlineData(19, null)]
    public void TryMapStoreFailure_AStatementsFailureOnCampaignsDb_IsTheStoresMessageOnlyForWhatAPersonCanFix(int code, string? phrase)
    {
        using var service = NewService();
        var failure = new Microsoft.Data.Sqlite.SqliteException("SQLite Error: near SELECT secret_md FROM entity", code);
        Assert.False(service.TryMapStoreFailure(failure, out _));

        _ = service.Database;
        var mapped = service.TryMapStoreFailure(failure, out var unavailable);

        Assert.Equal(phrase is not null, mapped);
        if (phrase is not null)
        {
            Assert.Contains(phrase, unavailable!.Message, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(_data, "campaigns.db"), unavailable.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT", unavailable.Message, StringComparison.Ordinal);
            Assert.Same(failure, unavailable.InnerException);
        }
    }

    [Fact]
    public void Resolve_NoCampaigns_SaysHowToCreateOne()
    {
        using var service = NewService();

        var ex = Assert.Throws<DndInputException>(() => service.Resolve(null));

        Assert.Contains("create", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_SetsTheProcessCampaign_AndAnotherProcessStartsFromThePersistedOne()
    {
        using var first = NewService();
        var belmakor = first.Store.Create("Belmakor", "player", "2014").Campaign;
        var onePiece = first.Store.Create("One Piece", "dm", "2024").Campaign;

        first.Use("belmakor");
        using var second = NewService();
        second.Use("one-piece");

        // Each process keeps its own choice; the persisted active campaign is the last one chosen anywhere.
        Assert.Equal(belmakor.Id, first.Resolve(null).Id);
        Assert.Equal(onePiece.Id, second.Resolve(null).Id);
        using var third = NewService();
        Assert.Equal(onePiece.Id, third.Resolve(null).Id);
    }

    [Theory]
    [InlineData("2014", "2014")]
    [InlineData("2024", "2024")]
    [InlineData("mixed", null)]
    public void DefaultEdition_TheCampaignsRuleset_OrNullForMixed(string ruleset, string? expected)
    {
        using var service = NewService();
        var campaign = service.Store.Create("Test", "dm", ruleset).Campaign;
        service.Use(campaign.Slug);

        Assert.Equal(expected, service.DefaultEdition(out var slug));
        Assert.Equal(expected is null ? null : campaign.Slug, slug);
    }
}
