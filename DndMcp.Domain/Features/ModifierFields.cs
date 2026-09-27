using K = DndMcp.Domain.Features.DslValues.Kinds;

namespace DndMcp.Domain.Features;

/// <summary>
/// The per-kind field table of <see cref="ModifierSpec"/>: which fields each kind takes, and which were given.
///
/// <para>
/// <b>Why explicit accessors and not reflection:</b> the wire names must be the snake_case names the model sees, and the
/// check must never silently skip a field. <c>ModifierFieldsTests</c> pins that every public property of
/// <see cref="ModifierSpec"/> appears in <see cref="All"/> under its snake_case name, so a new field cannot be added
/// without deciding which kinds take it.
/// </para>
/// <para>
/// <b>Deliberate narrowing of "common fields"</b>: <c>attacks</c> is not taken by kinds that do not act on attacks
/// (extra_attack names its one <c>attack</c>; save_effect and the defensive kinds have none), and <c>resource</c> only by
/// kinds that are SPENT per use (extra_damage, extra_attack, save_effect, condition_on_hit) or that the simulator will
/// track (the defensive kinds). A resource on an always-on modifier such as to_hit has no per-use meaning in the DPR
/// engine, and accepting it would suggest it limits something.
/// </para>
/// </summary>
public static class ModifierFields
{
    /// <summary>A field of <see cref="ModifierSpec"/> by its wire name, and whether a spec gave it.</summary>
    public sealed record Field(string Name, Func<ModifierSpec, bool> IsGiven);

    /// <summary>Fields every kind takes.</summary>
    public static readonly IReadOnlyList<string> Common = ["kind", "name", "from_level", "until_level", "concentration", "setup"];

    /// <summary>Every field of <see cref="ModifierSpec"/>, in declaration order.</summary>
    public static readonly IReadOnlyList<Field> All =
    [
        new("kind", m => m.Kind is not null),
        new("name", m => m.Name is not null),
        new("attacks", m => m.Attacks is not null),
        new("from_level", m => m.FromLevel is not null),
        new("until_level", m => m.UntilLevel is not null),
        new("resource", m => m.Resource is not null),
        new("concentration", m => m.Concentration is not null),
        new("setup", m => m.Setup is not null),
        new("amount", m => LevelValue.IsGiven(m.Amount)),
        new("dice", m => LevelValue.IsGiven(m.Dice)),
        new("type", m => m.Type is not null),
        new("when", m => m.When is not null),
        new("policy", m => m.Policy is not null),
        new("use_value", m => m.UseValue is not null),
        new("crit_doubles", m => m.CritDoubles is not null),
        new("attack_action_only", m => m.AttackActionOnly is not null),
        new("action_cost", m => m.ActionCost is not null),
        new("min", m => LevelValue.IsGiven(m.Min)),
        new("mode", m => m.Mode is not null),
        new("rate", m => m.Rate is not null),
        new("remap", m => m.Remap is not null),
        new("attack", m => m.Attack is not null),
        new("count", m => LevelValue.IsGiven(m.Count)),
        new("action", m => m.Action is not null),
        new("trigger", m => m.Trigger is not null),
        new("trigger_probability", m => m.TriggerProbability is not null),
        new("penalty", m => m.Penalty is not null),
        new("bonus", m => m.Bonus is not null),
        new("ability", m => m.Ability is not null),
        new("dc", m => m.Dc is not null),
        new("dc_ability", m => m.DcAbility is not null),
        new("dc_bonus", m => m.DcBonus is not null),
        new("on_success", m => m.OnSuccess is not null),
        new("targets", m => m.Targets is not null),
        new("shape", m => m.Shape is not null),
        new("size", m => m.Size is not null),
        new("magical", m => m.Magical is not null),
        new("condition", m => m.Condition is not null),
        new("cantrip", m => m.Cantrip is not null),
    ];

    /// <summary>The fields each kind takes beyond <see cref="Common"/>, in the order a message lists them.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByKind = new Dictionary<string, IReadOnlyList<string>>
    {
        [K.ToHit] = ["amount", "dice", "attacks"],
        [K.ExtraDamage] =
        [
            "dice", "amount", "type", "when", "policy", "use_value", "crit_doubles", "attack_action_only", "action_cost", "attacks",
            "resource",
        ],
        [K.BonusDamage] = ["amount", "attack_action_only", "attacks"],
        [K.CritRange] = ["min", "attacks"],
        [K.Advantage] = ["mode", "rate", "attacks"],
        [K.Lucky] = ["attacks"],
        [K.ElvenAccuracy] = ["attacks"],
        [K.DamageDieRemap] = ["remap", "type", "attacks"],
        [K.RerollDamageTakeBest] = ["policy", "use_value", "attacks"],
        [K.ExtraAttack] = ["attack", "count", "action", "trigger", "trigger_probability", "resource"],
        [K.PowerAttack] = ["penalty", "bonus", "policy", "attacks"],
        [K.SaveEffect] =
        [
            "ability", "dc", "dc_ability", "dc_bonus", "dice", "amount", "type", "on_success", "targets", "shape", "size", "magical",
            "condition", "action_cost", "cantrip", "resource",
        ],
        [K.ConditionOnHit] = ["condition", "ability", "dc", "dc_ability", "when", "policy", "use_value", "magical", "attacks", "resource"],
        [K.IgnoreCover] = ["attacks"],
        [K.Ac] = ["amount", "type", "resource"],
        [K.Resistance] = ["amount", "type", "resource"],
        [K.TempHp] = ["amount", "type", "resource"],
    };

    /// <summary>Whether <paramref name="kind"/> takes the field <paramref name="field"/>.</summary>
    public static bool Takes(string kind, string field) => Common.Contains(field) || ByKind[kind].Contains(field);

    /// <summary>The given fields of <paramref name="spec"/> that <paramref name="kind"/> does not take, in declaration order.</summary>
    public static IReadOnlyList<string> Refused(string kind, ModifierSpec spec) =>
        All.Where(f => f.IsGiven(spec) && !Takes(kind, f.Name)).Select(f => f.Name).ToList();

    /// <summary>"amount, dice, attacks, name, from_level, until_level, concentration, setup": what a message lists.</summary>
    public static string Describe(string kind) => string.Join(", ", ByKind[kind].Concat(Common.Where(c => c != "kind")));
}
