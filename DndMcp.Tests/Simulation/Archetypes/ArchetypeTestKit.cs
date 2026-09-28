using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation.Archetypes;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Tests.Simulation.Archetypes;

/// <summary>
/// Shared plumbing for the archetype tests: every (archetype, edition, level), resolution for the simulator, and the
/// closed-form round-1 damage of an archetype's best Action routine.
/// </summary>
internal static class ArchetypeTestKit
{
    public static IReadOnlyList<string> Editions { get; } = [V.Editions.E2014, V.Editions.E2024];

    /// <summary>Every archetype × edition × level: 12 × 2 × 20 = 480 rows.</summary>
    public static IEnumerable<object[]> AllMembers() =>
        from name in ArchetypeCatalog.Names
        from edition in Editions
        from level in Enumerable.Range(1, 20)
        select new object[] { name, edition, level };

    /// <summary>Every archetype × edition: 24 rows.</summary>
    public static IEnumerable<object[]> AllArchetypes() =>
        from name in ArchetypeCatalog.Names
        from edition in Editions
        select new object[] { name, edition };

    public static ResolvedBuild Resolve(ArchetypeMember member) =>
        BuildResolver.Resolve(member.Build, member.Level, use: BuildUse.Simulation);

    public static ResolvedBuild Resolve(string name, string edition, int level) => Resolve(ArchetypeCatalog.Build(name, level, edition));

    /// <summary>
    /// Round-1 expected damage against the DMG 2014 row for CR = level, of the best Action routine the archetype has: its
    /// attack routine, or one of its Action save effects (with <paramref name="includeLimited"/>, also those that spend a
    /// resource: Fireball, Shatter). The DPR engine always spends the Action on the first Action save effect it meets (a
    /// DPR build is one routine), so each routine is evaluated alone and the best is kept — what the simulator's
    /// per-turn choice does. Everything else (Bonus Action attacks, setups, riders, save effects that cost no Action)
    /// stays in every routine.
    /// </summary>
    public static double BestRound1(ArchetypeMember member, bool includeLimited)
    {
        var modifiers = member.Build.Modifiers ?? [];
        var actionSaves = modifiers.Where(IsActionSaveEffect).ToList();
        var rest = modifiers.Where(m => !IsActionSaveEffect(m)).ToList();
        var candidates = new List<IReadOnlyList<ModifierSpec>> { rest };
        candidates.AddRange(actionSaves
            .Where(s => includeLimited || s.Resource is null)
            .Select(s => (IReadOnlyList<ModifierSpec>)[.. rest, s]));

        var target = TargetResolver.Resolve(null, member.Level);
        var best = double.NegativeInfinity;
        foreach (var candidate in candidates)
        {
            var spec = new BuildSpec
            {
                Name = member.Build.Name,
                Edition = member.Build.Edition,
                Level = member.Build.Level,
                Abilities = member.Build.Abilities,
                FightingStyle = member.Build.FightingStyle,
                Attacks = member.Build.Attacks,
                Modifiers = candidate,
            };

            ResolvedBuild build;
            try
            {
                build = BuildResolver.Resolve(spec, member.Level, use: BuildUse.Simulation);
            }
            catch (DndInputException)
            {
                continue; // nothing active in this routine at this level (a 2014 bard's attack routine is empty)
            }

            best = Math.Max(best, DprEngine.Evaluate(build, target, DprOptions.Round1, WorkMeter.Unlimited).DamagePerRound);
        }

        return best;
    }

    private static bool IsActionSaveEffect(ModifierSpec modifier) =>
        modifier.Kind == V.Kinds.SaveEffect && modifier.ActionCost is null or V.ActionCosts.Action;
}
