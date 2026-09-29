using DndMcp.Domain.Core;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.IntegrationTests.Srd;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <see cref="StatBlockService"/> turns a monster named by ref or name into the stat block the simulator,
/// <c>rules_get</c>'s combatant format and <c>balance_dpr</c>'s <c>target.monster</c> all read — by
/// <c>encounter_difficulty</c>'s resolution rules, with the notes a result must show — normalizes each document once,
/// and says what to do when it cannot (a name the SRD lacks, a missing overrides file).
///
/// <para>
/// It is the one service three tools share, so its contract is pinned here rather than through any one tool: a change to
/// how "Vampire" or "2014/monster/ogre" resolves would move all three at once.
/// </para>
/// </summary>
public sealed class StatBlockServiceTests : IClassFixture<McpServerHarness>
{
    private readonly StatBlockService _service;

    public StatBlockServiceTests(McpServerHarness server)
    {
        _service = server.Services.GetRequiredService<StatBlockService>();
    }

    private Task<ResolvedStatBlock> Resolve(string text, string edition) => _service.ResolveAsync(text, edition, null, CancellationToken.None);

    [Theory]
    [InlineData("Ogre", "2024", "2024/monster/ogre")]
    [InlineData("ogre", "2014", "2014/monster/ogre")]
    [InlineData("ADULT RED DRAGON", "2024", "2024/monster/adult-red-dragon")]
    [InlineData("2024/monster/lich", "2024", "2024/monster/lich")]
    // A 2024 name answers in 2014 with its recorded 2014 counterpart (Thug is the 2014 name of the 2024 Tough).
    [InlineData("Tough", "2014", "2014/monster/thug")]
    public async Task ResolveAsync_ByNameOrRef_GivesThatEditionsStatBlockWithoutNotes(string text, string edition, string expected)
    {
        var (block, notes) = await Resolve(text, edition);

        Assert.Equal(expected, block.Ref);
        Assert.Equal(expected[..4], block.Edition);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task ResolveAsync_RefFromTheOtherEdition_IsUsedAsGivenWithANote()
    {
        var (block, notes) = await Resolve("2014/monster/ogre", "2024");

        Assert.Equal("2014/monster/ogre", block.Ref);
        Assert.Equal(["Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 fight."], notes);
    }

    [Fact]
    public async Task ResolveAsync_NameOnlyTheOtherEditionHas_UsesThatStatBlockWithANote()
    {
        var (block, notes) = await Resolve("Pirate Captain", "2014");

        Assert.Equal("2024/monster/pirate-captain", block.Ref);
        Assert.Equal(
            ["Pirate Captain: the 2014 SRD has no counterpart of `2024/monster/pirate-captain`, so its 2024 stat block is used in this 2014 fight."],
            notes);
    }

    [Fact]
    public async Task ResolveAsync_ANameTheDataSplitsIntoForms_UsesTheNamedFormWithANote()
    {
        var (block, notes) = await Resolve("Vampire", "2024");

        Assert.Equal("2024/monster/vampire-vampire", block.Ref);
        Assert.StartsWith("Vampire: the 2024 SRD data splits this stat block into forms (", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_FormsThatDifferInCr_AreRefusedWithTheFormsListed()
    {
        // Every name the SRD splits into forms shares one CR today. Should a re-vendor split one whose forms differ, one
        // form's numbers must not silently stand for the others: the name is refused and the forms are offered instead.
        // A private content copy whose srd-corrections.json makes the werewolf's hybrid form CR 5 in both editions.
        var root = Directory.CreateTempSubdirectory("dnd-mcp-forms-").FullName;
        try
        {
            var content = Path.Combine(root, "content");
            CopyDirectory(new DndMcpServerOptions().ContentRoot, content);
            File.WriteAllText(Path.Combine(content, "srd-corrections.json"), $$"""{"corrections": [{{HybridAtCr5("2014")}}, {{HybridAtCr5("2024")}}]}""");
            var options = new DndMcpServerOptions { ContentRoot = content, CacheDirectory = Path.Combine(root, "cache") };
            using var index = new SrdIndexService(options, NullLogger<SrdIndexService>.Instance);
            var service = new StatBlockService(index, options);

            var ex = await Assert.ThrowsAsync<DndInputException>(() => service.ResolveAsync("Werewolf", "2024", null, CancellationToken.None));

            Assert.StartsWith("monster: no monster in the 2014 or 2024 SRD is named \"Werewolf\". Close SRD names: ", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Werewolf, Hybrid Form (`2024/monster/werewolf-hybrid`)", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static string HybridAtCr5(string edition) =>
            $$"""{"ref": "{{edition}}/monster/werewolf-hybrid", "set": {"challenge_rating": 5, "xp": 1800}, "reason": "Test: forms that differ in CR.", "source": "test"}""";
    }

    [Fact]
    public async Task ResolveAsync_SameMonsterTwice_NormalizesOnce()
    {
        // Cached by ref: every simulation of three ogres, and every later call, shares one stat block.
        var first = (await Resolve("Ogre", "2024")).Block;
        var second = (await Resolve("2024/monster/ogre", "2024")).Block;

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ResolveAsync_ManyCallsAtOnce_AllGetTheSameStatBlock()
    {
        var blocks = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => Resolve("Adult Blue Dragon", "2014"))));

        Assert.All(blocks, b => Assert.Same(blocks[0].Block, b.Block));
    }

    [Fact]
    public async Task ResolveAsync_UnknownName_ListsCloseNamesWithTheDefaultFallback()
    {
        var ex = await Assert.ThrowsAsync<DndInputException>(() => Resolve("Orge", "2024"));

        Assert.StartsWith("monster: no monster in the 2014 or 2024 SRD is named \"Orge\". Close SRD names: Ogre (`", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("For a creature the SRD does not have, describe it yourself instead of naming it.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_CallersWording_NamesItsItemAndItsFallback()
    {
        // balance_dpr's target.monster words its own fallback (the target's ac and saves); the lookup is the same.
        var wording = StatBlockService.Wording("target monster", "fight", "For a monster not in the SRD, give the target's ac and saves instead.");

        var ex = await Assert.ThrowsAsync<DndInputException>(() => _service.ResolveAsync("2024/spell/fireball", "2024", null, CancellationToken.None, wording));

        Assert.Equal(
            "target monster: ref `2024/spell/fireball` is a spell, not a monster. Give a monster's ref (e.g. \"2024/monster/ogre\") or its name. " +
            "For a monster not in the SRD, give the target's ac and saves instead.",
            ex.Message);
    }

    [Fact]
    public async Task ResolveAsync_SeveralRequests_ReportsEveryFailureTogetherInOrder()
    {
        var requests = new[] { "Ogre", "Orge", "Goblin", "Gobblin" }
            .Select((name, i) => new StatBlockRequest(name, "2024", StatBlockService.Wording($"item {i + 1}")))
            .ToList();

        var ex = await Assert.ThrowsAsync<DndInputException>(() => _service.ResolveAsync(requests, null, CancellationToken.None));

        var lines = ex.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("item 2: no monster", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("item 4: no monster", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_WithoutTheOverridesFiles_SaysWhereTheyShipAndHowToFixIt()
    {
        // A publish copied without content/overrides: the model gets the install fix, never the SDK's generic error.
        var empty = Directory.CreateTempSubdirectory("dnd-mcp-no-overrides-").FullName;
        try
        {
            var options = new DndMcpServerOptions { ContentRoot = empty };
            using var index = new SrdIndexService(options, NullLogger<SrdIndexService>.Instance);
            var service = new StatBlockService(index, options);

            var ex = Assert.Throws<McpException>(() => service.Normalize(VendoredSrdLookup.Instance.Require("2024/monster/ogre"), VendoredSrdLookup.Instance));

            Assert.StartsWith("The monster overrides that turn SRD stat blocks into combatants could not be read (", ex.Message, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(empty, "overrides"), ex.Message, StringComparison.Ordinal);
            Assert.EndsWith("reinstall dnd-mcp into an empty directory so the whole publish output, content/ included, is in place.", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
