using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Hand-built <see cref="StatBlock"/>s, transcribed from the SRD 5.1 stat blocks, for the simulator's tests. The
/// normalizer (another agent's) produces the real ones; these fix the simulator's inputs so its rules can be tested on
/// their own. Each is the stat block as the normalizer is specified to write it (kinds, usage, legendary costs).
/// </summary>
internal static class TestStatBlocks
{
    public static StatBlock Create(
        string name,
        int ac,
        int hp,
        string hitDice,
        (int Str, int Dex, int Con, int Int, int Wis, int Cha) abilities,
        IReadOnlyList<StatBlockAction> actions,
        string edition = "2014",
        string cr = "1",
        IReadOnlyList<MultiattackRoutine>? multiattacks = null,
        IReadOnlyList<StatBlockTrait>? traits = null,
        IReadOnlyDictionary<string, int>? saves = null,
        IReadOnlyList<DamageAdjustment>? resistances = null,
        IReadOnlyList<DamageAdjustment>? immunities = null,
        IReadOnlyList<string>? conditionImmunities = null,
        LegendaryActions? legendary = null,
        int legendaryResistance = 0,
        IReadOnlyList<StatBlockAction>? reactions = null,
        IReadOnlyList<StatBlockAction>? bonusActions = null,
        IReadOnlyDictionary<string, int>? speeds = null,
        string size = "Medium",
        int? initiative = null)
    {
        var resolved = new ResolvedAbilities(abilities.Str, abilities.Dex, abilities.Con, abilities.Int, abilities.Wis, abilities.Cha);
        var saveBonuses = DslValues.Abilities.All.ToDictionary(a => a, a => saves is not null && saves.TryGetValue(a, out var s) ? s : resolved.Modifier(a));
        return new StatBlock
        {
            Ref = $"{edition}/monster/{name.ToLowerInvariant().Replace(' ', '-')}",
            Name = name,
            Edition = edition,
            Size = size,
            CreatureType = "monstrosity",
            ChallengeRating = ChallengeRating.Parse(cr),
            Xp = 100,
            ProficiencyBonus = 2,
            ArmorClass = ac,
            HitPoints = hp,
            HitDice = HitDice(hitDice),
            Abilities = resolved,
            SaveBonuses = saveBonuses,
            InitiativeBonus = initiative ?? resolved.Modifier("dex"),
            Speeds = speeds ?? new Dictionary<string, int> { ["walk"] = 30 },
            Resistances = resistances ?? [],
            Immunities = immunities ?? [],
            Vulnerabilities = [],
            ConditionImmunities = conditionImmunities ?? [],
            Traits = traits ?? [],
            Actions = actions,
            BonusActions = bonusActions ?? [],
            Reactions = reactions ?? [],
            Spells = [],
            SpellSlots = new Dictionary<int, int>(),
            Multiattacks = multiattacks ?? [],
            Legendary = legendary,
            LegendaryResistance = legendaryResistance,
            Notes = [],
            Warnings = [],
        };
    }

    /// <summary>"19d12+133" as the normalizer writes it (the DSL's damage parser caps flat parts at 100, hit dice do not).</summary>
    public static DamageFormula HitDice(string text)
    {
        var plus = text.IndexOfAny(['+', '-']);
        var dice = plus < 0 ? text : text[..plus];
        var flat = plus < 0 ? 0 : int.Parse(text[plus..], System.Globalization.CultureInfo.InvariantCulture);
        var d = dice.Split('d');
        return DamageFormula.Of([new DiceTerm(int.Parse(d[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(d[1], System.Globalization.CultureInfo.InvariantCulture))], flat);
    }

    public static DamageRoll Roll(string dice, string? type) => new(DamageFormula.ParseDamage(dice, "damage"), type);

    public static StatBlockAction Attack(string name, int bonus, string dice, string type, string range = K.AttackRanges.Melee,
                                         bool alsoRanged = false, IReadOnlyList<ActionEffect>? onHit = null, IReadOnlyList<DamageRoll>? extra = null,
                                         string slot = K.ActionSlots.Action, int cost = 1) => new()
    {
        Name = name,
        Kind = K.ActionKinds.Attack,
        Slot = slot,
        AttackBonus = bonus,
        Range = range,
        AlsoRanged = alsoRanged,
        Damage = [Roll(dice, type), .. extra ?? []],
        OnHit = onHit ?? [],
        LegendaryCost = cost,
        Text = name,
    };

    public static StatBlockAction SaveAction(string name, string ability, int dc, string dice, string type, AreaSpec? area = null, int targets = 1,
                                             string onSuccess = K.OnSuccess.Half, UsageSpec? usage = null, ConditionEffect? condition = null,
                                             bool spell = false, bool concentration = false, string slot = K.ActionSlots.Action, int cost = 1,
                                             bool oncePerRound = false) => new()
    {
        Name = name,
        Kind = K.ActionKinds.Save,
        Slot = slot,
        Save = new SaveSpec(ability, dc, onSuccess),
        Damage = dice.Length == 0 ? [] : [Roll(dice, type)],
        Area = area,
        Targets = targets,
        Usage = usage ?? UsageSpec.AtWill,
        Condition = condition,
        IsSpell = spell,
        Magical = spell,
        Concentration = concentration,
        LegendaryCost = cost,
        OncePerRound = oncePerRound,
        Text = name,
    };

    public static MultiattackRoutine Multiattack(params (string Name, int Count)[] steps) =>
        new() { Label = "Multiattack", Steps = steps.Select(s => new ActionUse(s.Name, s.Count)).ToList() };

    public static StatBlockTrait Trait(string name, string kind, int? amount = null, IReadOnlyList<string>? types = null, string? text = null, string? dice = null) => new()
    {
        Name = name,
        Kind = kind,
        Amount = amount,
        DamageTypes = types ?? [],
        Dice = dice is null ? null : DamageFormula.ParseDamage(dice, "dice"),
        Text = text ?? name,
    };

    /// <summary>SRD 5.1 Ogre: AC 11, 59 HP, greatclub +6 2d8+4, javelin +6 2d6+4 (melee or ranged).</summary>
    public static StatBlock Ogre => Create("Ogre", 11, 59, "7d10+21", (19, 8, 16, 5, 7, 7),
        [Attack("Greatclub", 6, "2d8+4", "bludgeoning"), Attack("Javelin", 6, "2d6+4", "piercing", alsoRanged: true)], cr: "2", size: "Large");

    /// <summary>SRD 5.1 Goblin: AC 15, 7 HP, Dex +2, scimitar +4 1d6+2.</summary>
    public static StatBlock Goblin => Create("Goblin", 15, 7, "2d6", (8, 14, 10, 10, 8, 8),
        [Attack("Scimitar", 4, "1d6+2", "slashing"), Attack("Shortbow", 4, "1d6+2", "piercing", K.AttackRanges.Ranged)], cr: "1/4", size: "Small");

    /// <summary>SRD 5.1 Commoner: AC 10, 4 HP, club +2 1d4.</summary>
    public static StatBlock Commoner => Create("Commoner", 10, 4, "1d8", (10, 10, 10, 10, 10, 10),
        [Attack("Club", 2, "1d4", "bludgeoning")], cr: "0");

    /// <summary>SRD 5.1 Zombie: AC 8, 22 HP, Undead Fortitude, slam +3 1d6+1, poison immune.</summary>
    public static StatBlock Zombie => Create("Zombie", 8, 22, "3d8+9", (13, 6, 16, 3, 6, 5),
        [Attack("Slam", 3, "1d6+1", "bludgeoning")], cr: "1/4",
        saves: new Dictionary<string, int> { ["wis"] = 0 },
        traits: [Trait("Undead Fortitude", K.TraitKinds.UndeadFortitude)],
        immunities: [new DamageAdjustment("poison", null, "poison")],
        conditionImmunities: ["poisoned"]);

    /// <summary>SRD 5.1 Troll: AC 15, 84 HP, bite + 2 claws, Regeneration 10 (acid or fire stops it).</summary>
    public static StatBlock Troll => Create("Troll", 15, 84, "8d10+40", (18, 13, 20, 7, 9, 7),
        [Attack("Bite", 7, "1d6+4", "piercing"), Attack("Claw", 7, "2d6+4", "slashing")], cr: "5", size: "Large",
        multiattacks: [Multiattack(("Bite", 1), ("Claw", 2))],
        traits: [Trait("Regeneration", K.TraitKinds.Regeneration, amount: 10, types: ["acid", "fire"],
            text: "The troll regains 10 hit points at the start of its turn. If the troll takes acid or fire damage, this trait doesn't function at the start of the troll's next turn. The troll dies only if it starts its turn with 0 hit points and doesn't regenerate.")]);

    /// <summary>SRD 5.1 Wolf: AC 13, 11 HP, Pack Tactics, bite +4 2d4+2 (Str DC 11 or prone).</summary>
    public static StatBlock Wolf => Create("Wolf", 13, 11, "2d8+2", (12, 15, 12, 3, 12, 6),
        [Attack("Bite", 4, "2d4+2", "piercing", onHit: [new ActionEffect
        {
            Kind = K.EffectKinds.Save,
            Save = new SaveSpec("str", 11, K.OnSuccess.None),
            Condition = new ConditionEffect { Condition = "prone", Duration = K.Durations.UntilStands },
        }])],
        cr: "1/4", traits: [Trait("Pack Tactics", K.TraitKinds.PackTactics)]);

    /// <summary>
    /// SRD 5.1 Adult Red Dragon: AC 19, 256 HP, fire immune, Legendary Resistance 3; bite + 2 claws; Fire Breath (Recharge
    /// 5–6, DC 21 Dex, 18d6 fire, 60-ft cone); legendary actions (3): Tail Attack, Wing Attack (costs 2). Flies.
    /// </summary>
    public static StatBlock AdultRedDragon
    {
        get
        {
            var tail = Attack("Tail", 14, "2d8+8", "bludgeoning");
            return Create("Adult Red Dragon", 19, 256, "19d12+133", (27, 10, 25, 16, 13, 21),
                [
                    Attack("Bite", 14, "2d10+8", "piercing", extra: [Roll("2d6", "fire")]),
                    Attack("Claw", 14, "2d6+8", "slashing"),
                    tail,
                    SaveAction("Fire Breath", "dex", 21, "18d6", "fire", new AreaSpec(K.Shapes.Cone, 60), usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 5)),
                ],
                cr: "17", size: "Huge",
                multiattacks: [Multiattack(("Bite", 1), ("Claw", 2))],
                saves: new Dictionary<string, int> { ["dex"] = 6, ["con"] = 13, ["wis"] = 7, ["cha"] = 11 },
                immunities: [new DamageAdjustment("fire", null, "fire")],
                legendary: new LegendaryActions(3, null,
                [
                    new StatBlockAction { Name = "Tail Attack", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Legendary, Uses = [new ActionUse("Tail", 1)], Text = "Tail Attack" },
                    SaveAction("Wing Attack", "dex", 22, "2d6+8", "bludgeoning", new AreaSpec(K.Shapes.Sphere, 10), onSuccess: K.OnSuccess.None,
                        condition: new ConditionEffect { Condition = "prone", Duration = K.Durations.UntilStands }, slot: K.ActionSlots.Legendary, cost: 2),
                ]),
                legendaryResistance: 3,
                speeds: new Dictionary<string, int> { ["walk"] = 40, ["fly"] = 80 },
                initiative: 0);
        }
    }

    /// <summary>A sturdy test monster: many HP, a feeble attack, so fights last to the round cap.</summary>
    public static StatBlock Sandbag(string name = "Sandbag", int hp = 5000, int ac = 40, LegendaryActions? legendary = null, int legendaryResistance = 0,
                                    IReadOnlyList<StatBlockAction>? actions = null, IReadOnlyDictionary<string, int>? saves = null) =>
        Create(name, ac, hp, "1d4", (10, 10, 10, 10, 10, 10), actions ?? [Attack("Poke", -10, "1", "bludgeoning")], legendary: legendary,
            legendaryResistance: legendaryResistance, saves: saves);
}
