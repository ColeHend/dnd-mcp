using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>target.monster</c> reaches the maths through the real client as the SRD stat block the name or ref
/// means — a bare name in the build's edition (the baseline's for balance_compare), a ref as given with a note — and the
/// result says which creature it is, reads its qualified resistances against the attack's properties, never lands a
/// condition it is immune to, and refuses a name the SRD lacks with close names and what to give instead.
///
/// <para>
/// The Domain's arithmetic against stat blocks is pinned in DndMcp.Tests with hand-built stat blocks; these pin only what
/// the host decides: the lookup, the edition it is made in, its notes, and its error messages.
/// </para>
/// </summary>
public sealed class BalanceMonsterTargetToolTests : IClassFixture<McpServerHarness>
{
    private const string DprPrefix = "An error occurred invoking 'balance_dpr': ";

    private readonly McpServerHarness _server;

    public BalanceMonsterTargetToolTests(McpServerHarness server)
    {
        _server = server;
    }

    private static string Fighter(string edition, string properties = "", string modifiers = "[]") =>
        $$"""
        { "name": "Fighter", "edition": "{{edition}}", "level": 5, "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee"{{properties}}] }],
          "modifiers": {{modifiers}} }
        """;

    private const string StunningStrike =
        """[{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 15, "when": "first_hit_per_turn" }]""";

    private async Task<string> Dpr(string arguments) => _server.SuccessText(await _server.CallToolJsonAsync("balance_dpr", arguments));

    private async Task<string> DprError(string arguments) => _server.ErrorText(await _server.CallToolJsonAsync("balance_dpr", arguments));

    [Theory]
    [InlineData("2024", "Ogre (2024 SRD stat block), CR 2. AC 11 (Ogre stat block).", "68 hit points")]
    [InlineData("2014", "Ogre (2014 SRD stat block), CR 2. AC 11 (Ogre stat block).", "59 hit points")]
    public async Task CallTool_MonsterByName_IsTheStatBlockOfTheBuildsEdition(string edition, string target, string hp)
    {
        var text = await Dpr($$$"""{"build": {{{Fighter(edition)}}}, "target": {"monster": "ogre"}}""");

        Assert.Contains("## Target at level 5\n\n" + target, text, StringComparison.Ordinal);
        Assert.Contains(hp, text, StringComparison.Ordinal);
        Assert.Contains($"against Ogre ({edition} SRD stat block), AC 11 — ", text, StringComparison.Ordinal);
        Assert.Contains($"the target's stat block: the {edition} SRD's Ogre (rules_get ref \"{edition}/monster/ogre\")", text, StringComparison.Ordinal);
        Assert.Contains("- Ogre is CR 2, below level 5: the level's reference target", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_MonsterByRef_IsUsedAsGivenWithANote()
    {
        var text = await Dpr($$$"""{"build": {{{Fighter("2024")}}}, "target": {"monster": "2014/monster/ogre"}}""");

        Assert.Contains("Ogre (2014 SRD stat block), CR 2.", text, StringComparison.Ordinal);
        Assert.Contains("- Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 calculation.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_2014Werewolf_ImmuneToMundaneWeaponsNotToMagicOnes()
    {
        // "Werewolf" is split into forms in the data; they share CR and immunities, and the lookup says which it used.
        var mundane = await Dpr($$$"""{"build": {{{Fighter("2014")}}}, "target": {"monster": "werewolf"}}""");
        var magical = await Dpr($$$"""{"build": {{{Fighter("2014", ", \"magical\"")}}}, "target": {"monster": "werewolf"}}""");

        Assert.StartsWith("# Damage per round: Fighter\n\n**0.00** damage per round at level 5 against Werewolf, Human Form (2014 SRD stat block), AC 11 — ", mundane, StringComparison.Ordinal);
        Assert.Contains("Immune to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered.", mundane, StringComparison.Ordinal);
        Assert.Contains(
            "- Immunity to bludgeoning, piercing and slashing from nonmagical attacks that aren't silvered: applied to Longsword (neither magical nor silvered).",
            mundane,
            StringComparison.Ordinal);
        Assert.Contains("- werewolf: the 2014 SRD data splits this stat block into forms (", mundane, StringComparison.Ordinal);
        Assert.DoesNotContain("**0.00** damage per round", magical, StringComparison.Ordinal);
        Assert.Contains("not applied to Longsword (magical)", magical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_StunImmuneMonster_StunningStrikeIsNeverAttempted()
    {
        var text = await Dpr($$$"""{"build": {{{Fighter("2024", modifiers: StunningStrike)}}}, "target": {"monster": "2024/monster/ettin"}}""");

        Assert.Contains("- Stunning Strike: never attempted, since the target is immune to the stunned condition.", text, StringComparison.Ordinal);
        Assert.Contains("Stunning Strike: the Ettin is immune to the stunned condition, so it is never attempted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_MonsterNotInTheSrd_IsAnErrorWithCloseNamesAndTheFallback()
    {
        var text = await DprError($$$"""{"build": {{{Fighter("2024")}}}, "target": {"monster": "Orge"}}""");

        Assert.StartsWith(DprPrefix + "target monster: no monster in the 2014 or 2024 SRD is named \"Orge\". Close SRD names: Ogre", text, StringComparison.Ordinal);
        Assert.EndsWith("For a monster not in the SRD, give the target's ac and saves instead.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"monster": "ogre", "cr": 2}""", "Invalid target: give monster or cr, not both")]
    [InlineData("""{"monster": "ogre", "profile": "mm2024"}""", "Invalid target: give monster or profile, not both")]
    [InlineData("""{"monster": "  "}""", "Invalid target: monster \"  \" is not a monster ref or name")]
    public async Task CallTool_MonsterWithARowOrBlank_IsRefusedBeforeAnyLookup(string target, string message)
    {
        var text = await DprError($$$"""{"build": {{{Fighter("2024")}}}, "target": {{{target}}}}""");

        Assert.StartsWith(DprPrefix + message, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Compare_AgainstAMonsterByName_UsesTheBaselinesEdition()
    {
        var arguments =
            $$$"""{"baseline": {{{Fighter("2014")}}}, "feature": {"name": "Stunning Strike", "modifiers": {{{StunningStrike}}}}, "target": {"monster": "swarm of rats"}}""";

        var text = _server.SuccessText(await _server.CallToolJsonAsync("balance_compare", arguments));

        Assert.Contains("against Swarm of Rats (2014 SRD stat block), AC 10 — ", text, StringComparison.Ordinal);
        Assert.Contains("**0.00** damage per round", text, StringComparison.Ordinal);
        Assert.Contains("| Stunning Strike | variant (new) | stunned | 0% (immune) | 0% (immune) |", text, StringComparison.Ordinal);
    }
}
