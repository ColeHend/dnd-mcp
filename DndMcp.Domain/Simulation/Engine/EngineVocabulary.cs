using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Damage types as small integers for the hot loop: the 13 DSL types in <see cref="DslValues.DamageTypes"/> order, then
/// <see cref="Typeless"/>. A hit is summed per type into an <c>int[Count]</c> buffer before resistances apply, so the
/// per-type halving and doubling happen on whole numbers exactly as the closed form's <c>DamageAdjustment</c> does.
/// </summary>
internal static class DamageTypes
{
    public const int Count = 14;

    /// <summary>Damage with no type: never resisted, doubled or ignored (only halved by a save, and by Petrified).</summary>
    public const int Typeless = 13;

    public static readonly IReadOnlyList<string> Names = DslValues.DamageTypes.Set.Values;

    private static readonly Dictionary<string, int> Index =
        Names.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);

    public static readonly int Radiant = Of("radiant");

    /// <summary>The index of a type, <see cref="Typeless"/> for null, and — for a type the data spells oddly — typeless too.</summary>
    public static int Of(string? type) => type is not null && Index.TryGetValue(type, out var i) ? i : Typeless;

    public static string Name(int index) => index == Typeless ? "typeless" : Names[index];
}

/// <summary>Conditions as small integers, in <see cref="StatBlockValues.Conditions.All"/> order, for per-creature counts.</summary>
internal static class Cond
{
    public const int Blinded = 0;
    public const int Charmed = 1;
    public const int Deafened = 2;
    public const int Exhaustion = 3;
    public const int Frightened = 4;
    public const int Grappled = 5;
    public const int Incapacitated = 6;
    public const int Invisible = 7;
    public const int Paralyzed = 8;
    public const int Petrified = 9;
    public const int Poisoned = 10;
    public const int Prone = 11;
    public const int Restrained = 12;
    public const int Stunned = 13;
    public const int Unconscious = 14;
    public const int Count = 15;

    private static readonly Dictionary<string, int> Index =
        StatBlockValues.Conditions.All.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);

    /// <summary>The index of a condition name, or −1 for one that is not an SRD condition.</summary>
    public static int Of(string? condition) => condition is not null && Index.TryGetValue(condition, out var i) ? i : -1;

    public static string Name(int condition) => StatBlockValues.Conditions.All[condition];

    /// <summary>Conditions that change a fight here (<see cref="StatBlockValues.Conditions.Mechanical"/>): Deafened alone does not.</summary>
    public static bool IsMechanical(int condition) => condition >= 0 && condition != Deafened;
}

/// <summary>How long an imposed condition lasts, compiled from <see cref="StatBlockValues.Durations"/> and <see cref="DslValues.Durations"/>.</summary>
internal enum DurationKind
{
    /// <summary>Ends when the source starts its next turn (a dead source: where that turn would start, <c>Fight.DeadCreaturesPlace</c>).</summary>
    UntilStartOfSourceTurn,

    /// <summary>Ends when the source's next turn ends (the turn it was imposed on, if any, does not count; a dead source: where that turn would end).</summary>
    UntilEndOfSourceTurn,

    /// <summary>The target repeats the save at the end of each of its turns.</summary>
    SaveEnds,

    /// <summary>A number of rounds, counted at the end of the source's turns (a dead source's too: where they would end).</summary>
    Rounds,

    /// <summary>Until the target escapes (its action, a check against the escape DC) or the source is incapacitated or dies.</summary>
    UntilEscape,

    /// <summary>Prone: until the target stands at the start of its turn (Speed above 0).</summary>
    UntilStands,

    /// <summary>For the rest of the fight.</summary>
    Fight,

    /// <summary>Ends when the TARGET's next turn ends (2024 "until the end of its next turn").</summary>
    UntilEndOfTargetTurn,

    /// <summary>Ends when the TARGET's next turn starts (an aura's condition, imposed as that turn begins).</summary>
    UntilStartOfTargetTurn,

    /// <summary>The dummy harness's starting condition: never ends (the closed form's target keeps it every turn).</summary>
    Permanent,
}

/// <summary>One roll of one damage type, compiled once: the dice as terms (so crits and remaps act per die), the flat part.</summary>
internal sealed class RollPart
{
    public RollPart(IReadOnlyList<DiceTerm> dice, int flat, int type)
    {
        Dice = dice.ToArray();
        Flat = flat;
        Type = type;
        DiceMean = Dice.Sum(d => (d.Negative ? -1 : 1) * d.Count * (d.Sides + 1) / 2.0);
    }

    public DiceTerm[] Dice { get; }

    public int Flat { get; }

    public int Type { get; }

    /// <summary>E[the dice], for the policies' expected values only (the simulator rolls; it never samples a mean).</summary>
    public double DiceMean { get; }

    public double Mean => DiceMean + Flat;

    public static RollPart From(DamageRoll roll) => new(roll.Dice.Dice, roll.Dice.Flat, DamageTypes.Of(roll.DamageType));

    public static RollPart[] From(IEnumerable<DamageRoll> rolls) => rolls.Select(From).ToArray();
}

/// <summary>
/// A condition an effect imposes, compiled: which, how it ends, and what ends it. Built from a stat block's
/// <see cref="ConditionEffect"/> or from a DSL condition with its edition default.
/// </summary>
internal sealed class ConditionTemplate
{
    public required int Condition { get; init; }

    public required DurationKind Duration { get; init; }

    /// <summary>Rounds for <see cref="DurationKind.Rounds"/>, or a cap on a save-ends condition (0: none).</summary>
    public int Rounds { get; init; }

    /// <summary>Until-escape: the escape DC.</summary>
    public int EscapeDc { get; init; }

    /// <summary>
    /// Save-ends: the repeated save's ability (null for another duration — except a seeded save-ends whose round cap has no
    /// counted turn end left, compiled as UntilStartOfSourceTurn with its save, <c>StartPreparation.Compile</c>).
    /// </summary>
    public string? SaveAbility { get; init; }

    public int SaveDc { get; init; }

    /// <summary>The repeated save is against a magical effect (Magic Resistance applies).</summary>
    public bool SaveMagical { get; init; }

    /// <summary>Damage at the start of each of the target's turns while it lasts.</summary>
    public RollPart[] Ongoing { get; init; } = [];

    /// <summary>A stat block condition and its extra conditions (imposed together), each compiled; nulls dropped.</summary>
    public static ConditionTemplate[] FromAll(ConditionEffect? main, IEnumerable<ConditionEffect> extra, SaveSpec? imposingSave, bool magical) =>
        new[] { main }.Concat(extra).Select(e => From(e, imposingSave, magical)).Where(t => t is not null).Select(t => t!).ToArray();

    /// <summary>
    /// A stat block condition. Prone always lasts until the creature stands, whatever the data says; a save-ends condition
    /// without its own save repeats the imposing one.
    /// </summary>
    public static ConditionTemplate? From(ConditionEffect? effect, SaveSpec? imposingSave, bool magical)
    {
        if (effect is null)
        {
            return null;
        }

        var condition = Cond.Of(effect.Condition);
        if (condition < 0)
        {
            return null;
        }

        var duration = condition == Cond.Prone
            ? DurationKind.UntilStands
            : effect.Duration switch
            {
                StatBlockValues.Durations.UntilStartOfSourceTurn => DurationKind.UntilStartOfSourceTurn,
                StatBlockValues.Durations.UntilEndOfSourceTurn => DurationKind.UntilEndOfSourceTurn,
                StatBlockValues.Durations.SaveEnds => DurationKind.SaveEnds,
                StatBlockValues.Durations.Rounds => DurationKind.Rounds,
                StatBlockValues.Durations.UntilEscape => DurationKind.UntilEscape,
                StatBlockValues.Durations.UntilStands => DurationKind.UntilStands,
                StatBlockValues.Durations.UntilEndOfTargetTurn => DurationKind.UntilEndOfTargetTurn,
                StatBlockValues.Durations.UntilStartOfTargetTurn => DurationKind.UntilStartOfTargetTurn,
                _ => DurationKind.Fight,
            };
        var save = effect.SaveEnds ?? imposingSave;
        if (duration == DurationKind.SaveEnds && save is null)
        {
            duration = effect.Rounds is > 0 ? DurationKind.Rounds : DurationKind.Fight;
        }

        if (duration == DurationKind.UntilEscape && effect.EscapeDc is null)
        {
            duration = DurationKind.Fight;
        }

        return new ConditionTemplate
        {
            Condition = condition,
            Duration = duration,
            Rounds = effect.Rounds ?? (duration == DurationKind.Rounds ? 10 : 0),
            EscapeDc = effect.EscapeDc ?? 0,
            SaveAbility = duration == DurationKind.SaveEnds ? save!.Ability : null,
            SaveDc = duration == DurationKind.SaveEnds ? save!.Dc : 0,
            SaveMagical = magical,
            Ongoing = RollPart.From(effect.OngoingDamage),
        };
    }

    /// <summary>
    /// A DSL condition (condition_on_hit or a save_effect's): <paramref name="duration"/> as given, or the default — for a
    /// condition on hit 2024 "until the start of your next turn", 2014 "until the end of your next turn"; for a save
    /// effect "save ends" (the same save at the end of each of the target's turns). Prone lasts until it stands.
    /// </summary>
    public static ConditionTemplate FromDsl(string condition, string? duration, bool onHit, string edition, string ability, int dc, bool magical)
    {
        var index = Cond.Of(condition);
        var kind = index == Cond.Prone
            ? DurationKind.UntilStands
            : (duration ?? (onHit
                ? (edition == DslValues.Editions.E2014 ? DslValues.Durations.EndOfNextTurn : DslValues.Durations.StartOfNextTurn)
                : DslValues.Durations.SaveEnds)) switch
            {
                DslValues.Durations.StartOfNextTurn => DurationKind.UntilStartOfSourceTurn,
                DslValues.Durations.EndOfNextTurn => DurationKind.UntilEndOfSourceTurn,
                DslValues.Durations.SaveEnds => DurationKind.SaveEnds,
                _ => DurationKind.Fight,
            };

        return new ConditionTemplate
        {
            Condition = index,
            Duration = kind,
            SaveAbility = kind == DurationKind.SaveEnds ? ability : null,
            SaveDc = kind == DurationKind.SaveEnds ? dc : 0,
            SaveMagical = magical,
        };
    }
}

/// <summary>A condition on a creature: what, who imposed it, how it ends, what concentration holds it.</summary>
internal struct ActiveCondition
{
    public int Condition;

    /// <summary>The creature that imposed it (its fight index), or −1.</summary>
    public int Source;

    public DurationKind Duration;

    /// <summary>Rounds left (Rounds, or a save-ends cap); counted down at the end of the source's turns.</summary>
    public int RoundsLeft;

    /// <summary>
    /// UntilEndOfSourceTurn / UntilEndOfTargetTurn: turn ends (of the source / of the target) still to pass before it ends
    /// — 1 when imposed during that creature's own turn, whose end does not count ("its NEXT turn").
    /// </summary>
    public int SkipTurnEnds;

    /// <summary>
    /// UntilStartOfSourceTurn / UntilStartOfTargetTurn, seeded only: turn starts (of the source / of the target) still to
    /// pass before it ends — 1 for a condition imposed during the turn a resumed fight starts at, whose start the resume plays
    /// again (<see cref="StartCondition.ImposedDuringResumedTurn"/>). 0 for everything imposed in a fight.
    /// </summary>
    public int SkipTurnStarts;

    public ConditionTemplate Template;

    /// <summary>The source's concentration that holds it (0: none): when it ends, so does this.</summary>
    public int ConcentrationToken;

    /// <summary>
    /// The imposing effect's <see cref="MonsterAction.ImmunityIndex"/>, −1 for none (set only through
    /// <c>Fight.AddCondition</c>, which is the only place a condition is made): when this condition ends, its creature
    /// becomes immune to that effect of <see cref="Source"/> — "if a creature's saving throw is successful or the effect
    /// ends for it, the creature is immune".
    /// </summary>
    public int Immunity;
}

/// <summary>How an attack is made this turn, the simulator's twin of the closed form's attack kinds (same rules per kind).</summary>
internal enum LineKind
{
    /// <summary>An attack of the Attack action.</summary>
    Action,

    /// <summary>An attack whose own action is the Bonus Action (an offhand attack).</summary>
    BonusAction,

    /// <summary>A second Attack action's attack (Action Surge).</summary>
    Surge,

    /// <summary>A Bonus Action extra attack (Hew, 2014 GWM's bonus attack).</summary>
    Hew,

    /// <summary>Cleave's attack against a second creature.</summary>
    Cleave,

    /// <summary>An attack on another creature's turn (an opportunity attack, Sentinel).</summary>
    Reaction,
}

internal static class LineKindRules
{
    /// <summary>Consumes and grants Vex, and triggers Topple, Cleave and conditions on hit (as in the closed form).</summary>
    public static bool IsMainTarget(this LineKind kind) => kind is not (LineKind.Cleave or LineKind.Reaction);

    /// <summary>Uses the turn's advantage-rate sample: everything but the reaction, which happens on another turn.</summary>
    public static bool UsesAdvantageSources(this LineKind kind) => kind != LineKind.Reaction;

    public static string Label(this LineKind kind) => kind switch
    {
        LineKind.Action => "attack",
        LineKind.BonusAction => "bonus action attack",
        LineKind.Surge => "Action Surge attack",
        LineKind.Hew => "bonus action extra attack",
        LineKind.Cleave => "Cleave attack",
        _ => "reaction attack",
    };
}
