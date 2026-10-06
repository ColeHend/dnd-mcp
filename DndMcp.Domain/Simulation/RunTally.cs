namespace DndMcp.Domain.Simulation;

/// <summary>
/// Integer accumulators for a set of fights: counts, Σx and Σx² as <see cref="long"/>, histograms as <c>long[]</c>. Each
/// thread fills its own and they are merged at the end; since integer addition is exact and order-free, the merged
/// totals — and so the report — are the same whichever thread ran which chunk (contract §5.6).
/// </summary>
internal sealed class RunTally
{
    public RunTally(FightSetup setup)
    {
        RoundCap = setup.RoundCap;
        RoundsHistogram = new long[setup.RoundCap];
        Creatures = setup.Templates.Select(t => new CreatureTally(t)).ToArray();
        Entries = setup.Entries.Select(ids => new EntryTally(ids)).ToArray();
    }

    public int RoundCap { get; }

    public long Fights;
    public long Wins;
    public long Defeats;
    public long Draws;

    /// <summary>Fights that ended with at least one party member dead (placeholders, dead before a resume, do not count).</summary>
    public long AnyDeath;

    /// <summary>
    /// Fights that ended with at least one party member dying (<see cref="CreatureTally.IsDying"/>): the fight stops when a
    /// side has nobody above 0 HP, win or lose, and its death saves are never finished.
    /// </summary>
    public long AnyDying;

    public long RoundsSum;
    public long RoundsSquares;
    public readonly long[] RoundsHistogram;
    public readonly CreatureTally[] Creatures;

    /// <summary>Per entry: the squares of each fight's totals over its copies, for the per-entry means' intervals.</summary>
    public readonly EntryTally[] Entries;

    // Comparison (variant minus baseline, per fight).
    public long VariantWins;
    public long VariantAnyDeath;

    /// <summary>The variant's fights that ended with a party member dying (<see cref="AnyDying"/>'s twin).</summary>
    public long VariantAnyDying;
    public long VariantRoundsSum;
    public long VariantRoundsSquares;
    public long WinDiffSum;
    public long WinDiffSquares;
    public long RoundsDiffSum;
    public long RoundsDiffSquares;
    public long DeathDiffSum;
    public long DeathDiffSquares;
    public long DyingDiffSum;
    public long DyingDiffSquares;

    /// <summary>One fight's outcome and creatures; returns what the variant's same fight is paired against.</summary>
    public (bool Win, bool AnyDeath, bool AnyDying) Add(FightOutcome outcome, Creature[] creatures)
    {
        Fights++;
        var win = outcome.Outcome == SimulationValues.Outcomes.PartyWins;
        if (win)
        {
            Wins++;
        }
        else if (outcome.Outcome == SimulationValues.Outcomes.PartyDefeated)
        {
            Defeats++;
        }
        else
        {
            Draws++;
        }

        RoundsSum += outcome.Rounds;
        RoundsSquares += (long)outcome.Rounds * outcome.Rounds;
        RoundsHistogram[Math.Clamp(outcome.Rounds, 1, RoundCap) - 1]++;
        var anyDeath = false;
        var anyDying = false;
        for (var i = 0; i < creatures.Length; i++)
        {
            Creatures[i].Add(creatures[i]);
            anyDeath |= CreatureTally.Counts(creatures[i]) && creatures[i].Dead;
            anyDying |= CreatureTally.Counts(creatures[i]) && CreatureTally.IsDying(creatures[i]);
        }

        if (anyDeath)
        {
            AnyDeath++;
        }

        if (anyDying)
        {
            AnyDying++;
        }

        foreach (var entry in Entries)
        {
            entry.Add(creatures);
        }

        return (win, anyDeath, anyDying);
    }

    /// <summary>The variant's fight on the same seed as the baseline's: its own outcome and the paired differences.</summary>
    public void AddVariant(FightOutcome variant, Creature[] creatures, (bool Win, bool AnyDeath, bool AnyDying) baseline, int baselineRounds)
    {
        var win = variant.Outcome == SimulationValues.Outcomes.PartyWins;
        var anyDeath = false;
        var anyDying = false;
        foreach (var c in creatures)
        {
            anyDeath |= CreatureTally.Counts(c) && c.Dead;
            anyDying |= CreatureTally.Counts(c) && CreatureTally.IsDying(c);
        }

        if (win)
        {
            VariantWins++;
        }

        if (anyDeath)
        {
            VariantAnyDeath++;
        }

        if (anyDying)
        {
            VariantAnyDying++;
        }

        VariantRoundsSum += variant.Rounds;
        VariantRoundsSquares += (long)variant.Rounds * variant.Rounds;
        var winDiff = (win ? 1 : 0) - (baseline.Win ? 1 : 0);
        WinDiffSum += winDiff;
        WinDiffSquares += winDiff * winDiff;
        var roundsDiff = variant.Rounds - baselineRounds;
        RoundsDiffSum += roundsDiff;
        RoundsDiffSquares += (long)roundsDiff * roundsDiff;
        var deathDiff = (anyDeath ? 1 : 0) - (baseline.AnyDeath ? 1 : 0);
        DeathDiffSum += deathDiff;
        DeathDiffSquares += deathDiff * deathDiff;
        var dyingDiff = (anyDying ? 1 : 0) - (baseline.AnyDying ? 1 : 0);
        DyingDiffSum += dyingDiff;
        DyingDiffSquares += dyingDiff * dyingDiff;
    }

    public void Merge(RunTally other)
    {
        Fights += other.Fights;
        Wins += other.Wins;
        Defeats += other.Defeats;
        Draws += other.Draws;
        AnyDeath += other.AnyDeath;
        AnyDying += other.AnyDying;
        RoundsSum += other.RoundsSum;
        RoundsSquares += other.RoundsSquares;
        for (var i = 0; i < RoundsHistogram.Length; i++)
        {
            RoundsHistogram[i] += other.RoundsHistogram[i];
        }

        for (var i = 0; i < Creatures.Length; i++)
        {
            Creatures[i].Merge(other.Creatures[i]);
        }

        for (var i = 0; i < Entries.Length; i++)
        {
            Entries[i].Merge(other.Entries[i]);
        }

        VariantWins += other.VariantWins;
        VariantAnyDeath += other.VariantAnyDeath;
        VariantAnyDying += other.VariantAnyDying;
        VariantRoundsSum += other.VariantRoundsSum;
        VariantRoundsSquares += other.VariantRoundsSquares;
        WinDiffSum += other.WinDiffSum;
        WinDiffSquares += other.WinDiffSquares;
        RoundsDiffSum += other.RoundsDiffSum;
        RoundsDiffSquares += other.RoundsDiffSquares;
        DeathDiffSum += other.DeathDiffSum;
        DeathDiffSquares += other.DeathDiffSquares;
        DyingDiffSum += other.DyingDiffSum;
        DyingDiffSquares += other.DyingDiffSquares;
    }
}

/// <summary>
/// One creature's integer accumulators over many fights: sums, which pool into its entry's; the squares that size the
/// entry's intervals are the entry's own (<see cref="EntryTally"/>).
/// </summary>
internal sealed class CreatureTally
{
    public CreatureTally(CombatantTemplate template)
    {
        var maxHp = template.RolledHp is { } dice ? dice.Dice.Sum(d => d.Count * d.Sides) + Math.Max(0, dice.Flat) : template.AverageHp;
        HpLostHistogram = new long[Math.Max(maxHp, template.AverageHp) + 1];
        ResourceUsed = new long[template.Pc?.Resources.Length ?? 0];
        LimitedUsed = new long[template.Limited.Length];
        PoolUsed = new long[template.PoolSizes.Length];
    }

    public long Dropped;
    public long Dead;

    /// <summary>Fights it ended dying (<see cref="IsDying"/>).</summary>
    public long Dying;

    public long HpLostSum;
    public readonly long[] HpLostHistogram;
    public long StartHpSum;
    public long DealtRaw;
    public long DealtEffective;
    public long TakenRaw;
    public long TakenEffective;
    public long Kills;
    public long LegendaryResistance;
    public long LegendaryActions;
    public readonly long[] ResourceUsed;
    public readonly long[] LimitedUsed;
    public readonly long[] PoolUsed;

    /// <summary>
    /// At 0 HP, making death saves, neither stable nor dead: what it was when the fight stopped, which the report shows as
    /// it is rather than guessing the saves the fight never rolled. A troll down at 0 HP waits to regenerate, not dying.
    /// </summary>
    public static bool IsDying(Creature c) => c.Down && !c.Dead && !c.Stable && c.T.PcLike;

    /// <summary>
    /// Hit points lost by the end of the fight, from where it started (<see cref="Creature.StartHp"/>: the maximum, or the
    /// seeded HP of a resumed fight): all of those when dead. Measured from the maximum, a resumed creature would count the
    /// damage it took before the resume as the simulation's.
    /// </summary>
    public static int HpLost(Creature c) => c.Dead ? c.StartHp : Math.Max(0, c.StartHp - c.Hp);

    /// <summary>
    /// A party member whose death or dying counts in the outcomes: not a placeholder (dead before the resume, kept only for
    /// its place in the order), which would otherwise make every resumed fight "a party member dies".
    /// </summary>
    public static bool Counts(Creature c) => c.Side == 0 && c.T.Start is not { Placeholder: true };

    public void Add(Creature c)
    {
        if (c.Dropped)
        {
            Dropped++;
        }

        if (c.Dead)
        {
            Dead++;
        }

        if (IsDying(c))
        {
            Dying++;
        }

        var lost = HpLost(c);
        HpLostSum += lost;
        HpLostHistogram[Math.Min(lost, HpLostHistogram.Length - 1)]++;
        StartHpSum += c.StartHp;
        DealtRaw += c.DealtRaw;
        DealtEffective += c.DealtEffective;
        TakenRaw += c.TakenRaw;
        TakenEffective += c.TakenEffective;
        Kills += c.Kills;
        LegendaryResistance += c.LegendaryResistanceSpent;
        LegendaryActions += c.LegendaryActionsUsed;
        if (c.Pc is { } pc)
        {
            for (var i = 0; i < ResourceUsed.Length; i++)
            {
                ResourceUsed[i] += pc.Used[i];
            }
        }

        for (var i = 0; i < LimitedUsed.Length; i++)
        {
            LimitedUsed[i] += c.LimitedUsed[i];
        }

        for (var i = 0; i < PoolUsed.Length; i++)
        {
            PoolUsed[i] += c.PoolUsed[i];
        }
    }

    public void Merge(CreatureTally other)
    {
        Dropped += other.Dropped;
        Dead += other.Dead;
        Dying += other.Dying;
        HpLostSum += other.HpLostSum;
        for (var i = 0; i < HpLostHistogram.Length; i++)
        {
            HpLostHistogram[i] += other.HpLostHistogram[i];
        }

        StartHpSum += other.StartHpSum;
        DealtRaw += other.DealtRaw;
        DealtEffective += other.DealtEffective;
        TakenRaw += other.TakenRaw;
        TakenEffective += other.TakenEffective;
        Kills += other.Kills;
        LegendaryResistance += other.LegendaryResistance;
        LegendaryActions += other.LegendaryActions;
        for (var i = 0; i < ResourceUsed.Length; i++)
        {
            ResourceUsed[i] += other.ResourceUsed[i];
        }

        for (var i = 0; i < LimitedUsed.Length; i++)
        {
            LimitedUsed[i] += other.LimitedUsed[i];
        }

        for (var i = 0; i < PoolUsed.Length; i++)
        {
            PoolUsed[i] += other.PoolUsed[i];
        }
    }
}

/// <summary>
/// One entry's squares for its per-creature means' CLT intervals: per fight, the total over its copies, squared. Each
/// fight is one sample because copies are not independent (they fight the same fight); squaring each creature's value
/// instead would treat them as if they were, and narrow the interval by up to √copies. The sums are the creatures' own
/// (<see cref="CreatureTally"/>), pooled.
/// </summary>
internal sealed class EntryTally(int[] ids)
{
    public long HpLostSquares;
    public long DealtRawSquares;
    public long DealtEffectiveSquares;
    public long TakenRawSquares;
    public long TakenEffectiveSquares;
    public long KillsSquares;

    public void Add(Creature[] creatures)
    {
        long lost = 0, dealt = 0, dealtEffective = 0, taken = 0, takenEffective = 0, kills = 0;
        foreach (var id in ids)
        {
            var c = creatures[id];
            lost += CreatureTally.HpLost(c);
            dealt += c.DealtRaw;
            dealtEffective += c.DealtEffective;
            taken += c.TakenRaw;
            takenEffective += c.TakenEffective;
            kills += c.Kills;
        }

        HpLostSquares += lost * lost;
        DealtRawSquares += dealt * dealt;
        DealtEffectiveSquares += dealtEffective * dealtEffective;
        TakenRawSquares += taken * taken;
        TakenEffectiveSquares += takenEffective * takenEffective;
        KillsSquares += kills * kills;
    }

    public void Merge(EntryTally other)
    {
        HpLostSquares += other.HpLostSquares;
        DealtRawSquares += other.DealtRawSquares;
        DealtEffectiveSquares += other.DealtEffectiveSquares;
        TakenRawSquares += other.TakenRawSquares;
        TakenEffectiveSquares += other.TakenEffectiveSquares;
        KillsSquares += other.KillsSquares;
    }
}
