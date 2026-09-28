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
    }

    public int RoundCap { get; }

    public long Fights;
    public long Wins;
    public long Defeats;
    public long Draws;
    public long AnyDeath;
    public long RoundsSum;
    public long RoundsSquares;
    public readonly long[] RoundsHistogram;
    public readonly CreatureTally[] Creatures;

    // Comparison (variant minus baseline, per fight).
    public long VariantWins;
    public long VariantAnyDeath;
    public long VariantRoundsSum;
    public long VariantRoundsSquares;
    public long WinDiffSum;
    public long WinDiffSquares;
    public long RoundsDiffSum;
    public long RoundsDiffSquares;
    public long DeathDiffSum;
    public long DeathDiffSquares;

    public (bool Win, bool AnyDeath) Add(FightOutcome outcome, Creature[] creatures)
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
        for (var i = 0; i < creatures.Length; i++)
        {
            Creatures[i].Add(creatures[i]);
            anyDeath |= creatures[i].Side == 0 && creatures[i].Dead;
        }

        if (anyDeath)
        {
            AnyDeath++;
        }

        return (win, anyDeath);
    }

    /// <summary>The variant's fight on the same seed as the baseline's: its own outcome and the paired differences.</summary>
    public void AddVariant(FightOutcome variant, Creature[] creatures, (bool Win, bool AnyDeath) baseline, int baselineRounds)
    {
        var win = variant.Outcome == SimulationValues.Outcomes.PartyWins;
        var anyDeath = false;
        foreach (var c in creatures)
        {
            anyDeath |= c.Side == 0 && c.Dead;
        }

        if (win)
        {
            VariantWins++;
        }

        if (anyDeath)
        {
            VariantAnyDeath++;
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
    }

    public void Merge(RunTally other)
    {
        Fights += other.Fights;
        Wins += other.Wins;
        Defeats += other.Defeats;
        Draws += other.Draws;
        AnyDeath += other.AnyDeath;
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

        VariantWins += other.VariantWins;
        VariantAnyDeath += other.VariantAnyDeath;
        VariantRoundsSum += other.VariantRoundsSum;
        VariantRoundsSquares += other.VariantRoundsSquares;
        WinDiffSum += other.WinDiffSum;
        WinDiffSquares += other.WinDiffSquares;
        RoundsDiffSum += other.RoundsDiffSum;
        RoundsDiffSquares += other.RoundsDiffSquares;
        DeathDiffSum += other.DeathDiffSum;
        DeathDiffSquares += other.DeathDiffSquares;
    }
}

/// <summary>One creature's integer accumulators over many fights.</summary>
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
    public long HpLostSum;
    public long HpLostSquares;
    public readonly long[] HpLostHistogram;
    public long StartHpSum;
    public long DealtRaw;
    public long DealtRawSquares;
    public long DealtEffective;
    public long DealtEffectiveSquares;
    public long TakenRaw;
    public long TakenRawSquares;
    public long TakenEffective;
    public long TakenEffectiveSquares;
    public long Kills;
    public long KillsSquares;
    public long LegendaryResistance;
    public long LegendaryActions;
    public readonly long[] ResourceUsed;
    public readonly long[] LimitedUsed;
    public readonly long[] PoolUsed;

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

        var lost = c.Dead ? c.MaxHp : Math.Max(0, c.MaxHp - c.Hp);
        HpLostSum += lost;
        HpLostSquares += (long)lost * lost;
        HpLostHistogram[Math.Min(lost, HpLostHistogram.Length - 1)]++;
        StartHpSum += c.MaxHp;
        DealtRaw += c.DealtRaw;
        DealtRawSquares += c.DealtRaw * c.DealtRaw;
        DealtEffective += c.DealtEffective;
        DealtEffectiveSquares += c.DealtEffective * c.DealtEffective;
        TakenRaw += c.TakenRaw;
        TakenRawSquares += c.TakenRaw * c.TakenRaw;
        TakenEffective += c.TakenEffective;
        TakenEffectiveSquares += c.TakenEffective * c.TakenEffective;
        Kills += c.Kills;
        KillsSquares += (long)c.Kills * c.Kills;
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
        HpLostSum += other.HpLostSum;
        HpLostSquares += other.HpLostSquares;
        for (var i = 0; i < HpLostHistogram.Length; i++)
        {
            HpLostHistogram[i] += other.HpLostHistogram[i];
        }

        StartHpSum += other.StartHpSum;
        DealtRaw += other.DealtRaw;
        DealtRawSquares += other.DealtRawSquares;
        DealtEffective += other.DealtEffective;
        DealtEffectiveSquares += other.DealtEffectiveSquares;
        TakenRaw += other.TakenRaw;
        TakenRawSquares += other.TakenRawSquares;
        TakenEffective += other.TakenEffective;
        TakenEffectiveSquares += other.TakenEffectiveSquares;
        Kills += other.Kills;
        KillsSquares += other.KillsSquares;
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
