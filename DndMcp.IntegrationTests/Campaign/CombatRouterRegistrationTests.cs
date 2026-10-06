using Dapper;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Dice;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the D5 router the server registers (<see cref="ICombatRouter"/> in <c>AddDndMcpServer</c>) stamps the
/// combat_log rows it writes with the server options' clock, the clock campaigns.db stamps everything else with (the
/// combat service's rows, the dice, change_log). On the system clock instead, a routed change would be dated apart from
/// the rest of its fight, and under a test's fixed clock no scenario that routes a change would be deterministic.
/// </summary>
public sealed class CombatRouterRegistrationTests
{
    [Fact]
    public async Task RoutedDamage_IsStampedWithTheServerOptionsClock_LikeTheRestOfTheFight()
    {
        var at = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var server = McpServerHarness.WithOptions(options => options.Time = new FixedTime(at));
        await server.InitializeAsync();
        try
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            await CampaignWriteSetup.WriteAsync(server, slug, """[{"op": "upsert", "kind": "character", "name": "Iron Guts"}]""");
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            var campaign = campaigns.Resolve(slug);
            var writer = new CharacterWriter(campaigns.Database, server.Services.GetRequiredService<ICombatRouter>());
            writer.Update(campaign, "character:iron-guts", new SheetSpec { MaxHp = 30, Ac = 12 }, null, WriteContext.Default);
            new CombatService(campaigns.Database, server.Services.GetRequiredService<IDiceRoller>()).Start(campaign,
                new StartRequest { Name = "Clocked", AddParty = false, Combatants = [new CombatantRequest { Character = "character:iron-guts" }] });

            var routed = writer.Damage(campaign, "character:iron-guts", 3, null, WriteContext.Default);

            Assert.True(routed.Routed!.Applied);
            using var connection = new SqliteConnection($"Data Source={campaigns.Database.Path};Mode=ReadOnly;Pooling=False");
            var stamps = connection.Query<(string Kind, string At)>("SELECT kind, at FROM combat_log ORDER BY seq").ToList();
            Assert.Contains(stamps, s => s.Kind == "damage");
            Assert.All(stamps, s => Assert.Equal(CampaignDatabase.FormatTimestamp(at), s.At));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
