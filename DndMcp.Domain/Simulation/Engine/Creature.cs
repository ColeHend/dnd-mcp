namespace DndMcp.Domain.Simulation;

/// <summary>
/// One creature's state during one fight. Allocated once per thread and reset per fight (<see cref="Reset"/>), so the
/// hot loop allocates nothing: the lists are cleared, not replaced.
/// </summary>
internal sealed class Creature
{
    public Creature(CombatantTemplate template)
    {
        T = template;
        Id = template.Id;
        Side = template.Side;
        RechargeReady = new bool[template.Limited.Length];
        UsesLeft = new int[template.Limited.Length];
        PoolLeft = new int[template.PoolSizes.Length];
        LegendaryUsedThisRound = new bool[template.LegendaryActions.Length];
        LimitedUsed = new int[template.Limited.Length];
        PoolUsed = new int[template.PoolSizes.Length];
        if (template.Pc is { } pc)
        {
            Pc = new PcState(pc);
        }
    }

    public CombatantTemplate T { get; }

    public int Id { get; }

    public int Side { get; }

    public string Label => T.Label;

    public int Hp;
    public int MaxHp;
    public int TempHp;

    /// <summary>Dead: out of the fight for good.</summary>
    public bool Dead;

    /// <summary>At 0 HP and not dead: a PC-like creature dying or stable, or a troll waiting to regenerate. Unconscious and prone.</summary>
    public bool Down;

    public bool Stable;
    public int DeathSuccesses;
    public int DeathFailures;

    public readonly List<ActiveCondition> Conditions = [];
    public readonly int[] CondCount = new int[Cond.Count];

    public bool Dodging;

    /// <summary>Reckless: attacks against it have Advantage until its next turn.</summary>
    public bool RecklessActive;

    /// <summary>Sap: the creature whose hit sapped it (its next attack roll has Disadvantage), or −1.</summary>
    public int SappedBy = -1;

    /// <summary>Vex: the creature this one has Advantage against on its next attack roll, and the turn it was granted.</summary>
    public int VexTarget = -1;

    public long VexTurn;

    public bool ReactionAvailable;

    /// <summary>2014 surprise: skips its first turn and has no reactions until that turn ends.</summary>
    public bool Surprised;

    public int TurnsTaken;
    public int Initiative;

    public int LegendaryUsesLeft;
    public int LegendaryResistanceLeft;
    public readonly bool[] LegendaryUsedThisRound;
    public readonly bool[] RechargeReady;
    public readonly int[] UsesLeft;
    public readonly int[] PoolLeft;

    /// <summary>The concentration it holds (0: none); conditions it maintains carry the same token.</summary>
    public int ConcentrationToken;

    /// <summary>The build modifier it concentrates on (its number), or 0.</summary>
    public int ConcentrationModifier;

    public string? ConcentrationLabel;

    /// <summary>Losing this concentration switches the build modifier off (Hex); a cast save effect's does not.</summary>
    public bool ConcentrationDeactivates;

    public bool RegenerationBlocked;
    public bool RelentlessUsed;
    public long SneakAttackTurn = -1;
    public long MartialAdvantageTurn = -1;

    public PcState? Pc { get; }

    // Per-fight statistics.
    public bool Dropped;
    public long DealtRaw;
    public long DealtEffective;
    public long TakenRaw;
    public long TakenEffective;
    public int Kills;
    public int LegendaryResistanceSpent;
    public int LegendaryActionsUsed;
    public readonly int[] LimitedUsed;
    public readonly int[] PoolUsed;

    /// <summary>
    /// Above 0 HP: what keeps its side in the fight (contract §5.4: the fight ends when a side has no creature above 0 HP).
    /// A troll at 0 HP is not standing even though it may regenerate: while its allies fight on it gets up at the start of
    /// its turn, but a side with nobody above 0 HP is beaten — otherwise a party without fire or acid could only ever lose
    /// to one troll, which no table plays (they finish it after the fight).
    /// </summary>
    public bool Standing => !Dead && Hp > 0;

    public bool Up => !Dead && Hp > 0;

    public bool Has(int condition) => CondCount[condition] > 0 || (T.PermanentConditions & (1 << condition)) != 0;

    /// <summary>Unconscious from a condition or from being at 0 HP.</summary>
    public bool Unconscious => Down || Has(Cond.Unconscious);

    /// <summary>No actions or reactions: incapacitated, stunned, paralyzed, petrified, unconscious (or down).</summary>
    public bool Incapacitated =>
        Down || Dead || Has(Cond.Incapacitated) || Has(Cond.Stunned) || Has(Cond.Paralyzed) || Has(Cond.Petrified) || Has(Cond.Unconscious);

    /// <summary>
    /// The Dodge action's benefits: lost while Incapacitated or at Speed 0 (grappled, restrained) — 2014 "if you are
    /// incapacitated … or if your speed drops to 0", 2024 alike.
    /// </summary>
    public bool IsDodging => (Dodging || T.PermanentDodging) && !Incapacitated && !Has(Cond.Restrained) && !Has(Cond.Grappled);

    public int Exhaustion => CondCount[Cond.Exhaustion];

    public void Reset(int hp)
    {
        Hp = T.StartsDown ? 0 : hp;
        MaxHp = hp;
        TempHp = T.TempHpAtStart;
        Dead = false;
        Down = T.StartsDown;
        Stable = false;
        DeathSuccesses = 0;
        DeathFailures = 0;
        Conditions.Clear();
        Array.Clear(CondCount);
        Dodging = false;
        RecklessActive = false;
        SappedBy = -1;
        VexTarget = -1;
        VexTurn = 0;
        ReactionAvailable = true;
        Surprised = false;
        TurnsTaken = 0;
        Initiative = 0;
        LegendaryUsesLeft = T.LegendaryUses;
        LegendaryResistanceLeft = T.LegendaryResistance;
        Array.Clear(LegendaryUsedThisRound);
        for (var i = 0; i < T.Limited.Length; i++)
        {
            var usage = T.Limited[i].Source.Usage;
            RechargeReady[i] = true;
            UsesLeft[i] = usage.Kind == StatBlockValues.UsageKinds.PerDay ? usage.Uses ?? 1 : 0;
        }

        Array.Copy(T.PoolSizes, PoolLeft, PoolLeft.Length);
        ConcentrationToken = 0;
        ConcentrationModifier = 0;
        ConcentrationLabel = null;
        ConcentrationDeactivates = false;
        RegenerationBlocked = false;
        RelentlessUsed = false;
        SneakAttackTurn = -1;
        MartialAdvantageTurn = -1;
        Dropped = false;
        DealtRaw = 0;
        DealtEffective = 0;
        TakenRaw = 0;
        TakenEffective = 0;
        Kills = 0;
        LegendaryResistanceSpent = 0;
        LegendaryActionsUsed = 0;
        Array.Clear(LimitedUsed);
        Array.Clear(PoolUsed);
        Pc?.Reset();
    }
}

/// <summary>A build's state across one fight: uses left, which gated modifiers are up, the pending reaction.</summary>
internal sealed class PcState
{
    public PcState(PcBuild build)
    {
        Build = build;
        UsesLeft = new int[build.Resources.Length];
        Used = new int[build.Resources.Length];
        Active = new bool[build.Gated.Length];
        Turn = new PcTurnContext(build);
        ReactionTurn = new PcTurnContext(build);
    }

    public PcBuild Build { get; }

    public readonly int[] UsesLeft;

    /// <summary>Uses spent this fight, per resource (for "resources used").</summary>
    public readonly int[] Used;

    /// <summary>Per modifier number: a gated modifier (one with a setup) is up.</summary>
    public readonly bool[] Active;

    /// <summary>The reaction attack will happen this round (drawn at the start of the creature's turn).</summary>
    public bool ReactionPending;

    /// <summary>It has taken an acting turn this fight (setups after that are re-establishments).</summary>
    public bool Acted;

    public int ReactionExtra = -1;

    public PcTurnContext Turn { get; }

    public PcTurnContext ReactionTurn { get; }

    public void Reset()
    {
        for (var i = 0; i < UsesLeft.Length; i++)
        {
            UsesLeft[i] = Build.Resources[i].Uses;
        }

        Array.Clear(Used);
        Array.Clear(Active);
        ReactionPending = false;
        ReactionExtra = -1;
        Acted = false;
    }

    /// <summary>Whether a modifier applies now: always, unless it waits for a setup that has not been paid (or was lost with concentration).</summary>
    public bool IsActive(int number) => number >= Active.Length || !Build.Gated[number] || Active[number];

    public bool CanSpend(int slot) => slot < 0 || UsesLeft[slot] > 0;

    public void Spend(int slot)
    {
        if (slot >= 0)
        {
            UsesLeft[slot]--;
            Used[slot]++;
        }
    }
}

/// <summary>
/// What one turn of a build tracks (the simulator's twin of the closed form's turn state): the advantage sample, the power
/// attacks switched on, once-per-turn spends, the Bonus Action, the triggers seen, and the current target.
/// </summary>
internal sealed class PcTurnContext
{
    public PcTurnContext(PcBuild build)
    {
        RiderOnce = new bool[build.Riders.Length];
        RerollOnce = new bool[build.Rerolls.Length];
        ConditionOnce = new bool[build.ConditionsOnHit.Length];
    }

    public bool[] RiderOnce { get; }

    public bool[] RerollOnce { get; }

    public bool[] ConditionOnce { get; }

    public ulong Present;
    public int PowerOn;
    public bool CleaveUsed;
    public bool BonusAction;
    public bool BonusActionRiderTaken;
    public bool AttackActionTaken;
    public bool HitTrigger;
    public bool CritTrigger;
    public bool KillTrigger;
    public bool IsReaction;
    public Creature? Target;

    public void Begin(bool reaction)
    {
        Array.Clear(RiderOnce);
        Array.Clear(RerollOnce);
        Array.Clear(ConditionOnce);
        Present = 0;
        PowerOn = 0;
        CleaveUsed = false;
        BonusAction = !reaction;
        BonusActionRiderTaken = false;
        AttackActionTaken = false;
        HitTrigger = false;
        CritTrigger = false;
        KillTrigger = false;
        IsReaction = reaction;
        Target = null;
    }
}
