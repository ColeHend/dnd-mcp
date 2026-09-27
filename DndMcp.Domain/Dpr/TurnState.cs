namespace DndMcp.Domain.Dpr;

/// <summary>
/// One state of the turn's dynamic programme (research A3): where the turn is, and everything that can change what
/// happens from here on. Two histories that reach the same state have the same future, which is what makes the
/// programme exact and small: the remaining damage depends on the state alone (no kill triggers are modelled).
///
/// <para>
/// <b>Why each field is here.</b> <see cref="Spent"/>: once-per-turn riders, Savage Attacker, first-hit conditions and
/// Cleave, used or not. <see cref="Uses"/>: resources left (8 bits a slot, <see cref="Unlimited"/> for a slot that never
/// runs out). <see cref="Flags"/>: Vex pending on the next attack roll, the Bonus Action still free, the hit/crit
/// triggers seen (Hew, the 2014 GWM bonus attack), a Sap hit, the conditions imposed this turn and the save effects that
/// landed (both for "P(lands)"), and the first round (setup costs). Leaving any of these out would merge states whose
/// futures differ; adding anything else (damage so far) would only multiply states with identical futures.
/// </para>
/// </summary>
/// <param name="Segment">The <see cref="TurnPlan.Segments"/> entry the turn is in.</param>
/// <param name="Position">The next queue position in an attack segment.</param>
/// <param name="CleaveLine">The line of a Cleave attack to make before <see cref="Position"/>, or −1.</param>
internal readonly record struct TurnState(int Segment, int Position, int CleaveLine, ulong Spent, ulong Uses, int Flags)
{
    public const int MaxResourceSlots = 8;

    public const ulong Unlimited = 0xFF;

    public const int VexFlag = 1 << 0;
    public const int BonusActionFlag = 1 << 1;
    public const int HitTriggerFlag = 1 << 2;
    public const int CritTriggerFlag = 1 << 3;
    public const int SapFlag = 1 << 4;
    public const int FirstRoundFlag = 1 << 5;

    /// <summary>Conditions imposed this turn: <see cref="TurnPlan.ProneBit"/>… shifted here.</summary>
    public const int ConditionShift = 8;

    /// <summary>Save effects whose condition landed on the first target this turn.</summary>
    public const int SaveLandedShift = 16;

    public bool Vex => (Flags & VexFlag) != 0;

    public bool BonusAction => (Flags & BonusActionFlag) != 0;

    public int AppliedConditions => (Flags >> ConditionShift) & 0x7F;

    public int SaveLanded => Flags >> SaveLandedShift;

    public bool Has(int flag) => (Flags & flag) != 0;

    public TurnState With(int flag, bool on) => this with { Flags = on ? Flags | flag : Flags & ~flag };

    public TurnState WithCondition(int bit) => this with { Flags = Flags | (1 << (ConditionShift + bit)) };

    public TurnState WithSaveLanded(int bit) => this with { Flags = Flags | (1 << (SaveLandedShift + bit)) };

    public ulong UsesLeft(int slot) => (Uses >> (8 * slot)) & 0xFF;

    public bool CanSpend(int slot) => slot < 0 || UsesLeft(slot) > 0;

    /// <summary>One use fewer in <paramref name="slot"/> (an unlimited slot stays unlimited; −1 is no slot).</summary>
    public TurnState Spend(int slot)
    {
        if (slot < 0)
        {
            return this;
        }

        var left = UsesLeft(slot);
        return left is Unlimited or 0 ? this : this with { Uses = Uses - (1UL << (8 * slot)) };
    }

    public static ulong PackUses(IReadOnlyList<ulong> uses)
    {
        ulong packed = 0;
        for (var i = 0; i < uses.Count; i++)
        {
            packed |= Math.Min(uses[i], Unlimited) << (8 * i);
        }

        return packed;
    }
}

/// <summary>What one turn hands the next: Vex pending, uses left, and what landed this turn (for "P(lands) per fight").</summary>
internal readonly record struct CarryKey(bool Vex, ulong Uses, int Landed);
