using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Index;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// One normalization in progress: the record, its overrides, and the lists being built. Split by concern across
/// partial files (numbers and traits here; actions, spells and routines beside it). Not reused: a new reading per call.
/// </summary>
internal sealed partial class MonsterReading
{
    private readonly MonsterNormalizer _normalizer;
    private readonly MonsterRecord _m;
    private readonly MonsterOverride? _override;
    private readonly ISrdLookup _lookup;
    private readonly NormalizationLog _log = new();

    private readonly List<StatBlockTrait> _traits = [];
    private readonly List<StatBlockAction> _actions = [];
    private readonly List<StatBlockAction> _bonusActions = [];
    private readonly List<StatBlockAction> _reactions = [];
    private readonly List<StatBlockAction> _spells = [];
    private readonly List<StatBlockAction> _legendary = [];
    private readonly Dictionary<int, int> _slots = [];
    private readonly List<MultiattackRoutine> _routines = [];
    private bool _magicWeapons;

    /// <summary>Header actions and their members ("Roar" → First Roar, Second Roar, Third Roar).</summary>
    private readonly Dictionary<string, List<string>> _families = new(StringComparer.Ordinal);

    public MonsterReading(MonsterNormalizer normalizer, MonsterRecord record, MonsterOverride? overrides, ISrdLookup lookup)
    {
        _normalizer = normalizer;
        _m = record;
        _override = overrides;
        _lookup = lookup;
    }

    private string Edition => _m.Edition;

    private bool Is2014 => _m.Edition == SrdEdition.Edition2014;

    public StatBlock Build(string reference)
    {
        var cr = ChallengeRating.FromNumber(_m.ChallengeRating) ?? ChallengeRating.Zero;
        if (ChallengeRating.FromNumber(_m.ChallengeRating) is null)
        {
            _log.Unparsed("Challenge Rating", string.Create(CultureInfo.InvariantCulture, $"CR {_m.ChallengeRating} is not a table value; CR 0 is used."));
        }

        ReadTraits();
        ReadSpellcasting();
        ReadActionList(_m.Actions, StatBlockValues.ActionSlots.Action, _actions);
        ReadActionList(_m.BonusActions, StatBlockValues.ActionSlots.BonusAction, _bonusActions);
        ReadActionList(_m.Reactions, StatBlockValues.ActionSlots.Reaction, _reactions);
        ResolvePlaceholders(_actions);
        ResolvePlaceholders(_bonusActions);
        ReadMultiattacks();
        var legendary = ReadLegendary();
        var (lr, lrLair) = LegendaryResistance();

        return new StatBlock
        {
            Ref = reference,
            Name = _m.Name,
            Edition = Edition,
            Size = _m.Size,
            CreatureType = _m.CreatureType,
            ChallengeRating = cr,
            Xp = _m.Xp,
            XpInLair = _m.XpInLair,
            ProficiencyBonus = _m.ProficiencyBonus,
            ArmorClass = _m.ArmorClass.Count > 0 ? _m.ArmorClass[0].Value : 10,
            ArmorClassNote = ArmorClassNote(),
            HitPoints = _m.HitPoints,
            HitDice = HitDice(),
            Abilities = _m.Abilities,
            SaveBonuses = SaveBonuses(),
            InitiativeBonus = Initiative(),
            Speeds = Speeds(),
            Hovers = _m.Speed.Hover == true,
            Resistances = Adjustments(_m.Resistances, "resistance"),
            Immunities = Adjustments(_m.Immunities, "immunity"),
            Vulnerabilities = Adjustments(_m.Vulnerabilities, DamageAdjustmentReader.Vulnerability),
            ConditionImmunities = ConditionImmunities(),
            Traits = _traits,
            Actions = _actions,
            BonusActions = _bonusActions,
            Reactions = _reactions,
            Spells = _spells,
            SpellSlots = _slots,
            Multiattacks = _routines,
            Legendary = legendary,
            LegendaryResistance = lr,
            LegendaryResistanceInLair = lrLair,
            Forms = _m.Forms.Select(f => $"{Edition}/{SrdKinds.Monster}/{f}").ToList(),
            Notes = _log.Notes,
            Warnings = _log.Warnings,
        };
    }

    private string? ArmorClassNote()
    {
        var parts = new List<string>();
        if (_m.ArmorClass.Count > 0 && _m.ArmorClass[0].Source is { } source)
        {
            parts.Add(source);
        }

        foreach (var (value, other) in _m.ArmorClass.Skip(1))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{value} {other ?? "otherwise"}"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private DamageFormula HitDice()
    {
        if (ProseText.Dice(_m.HitPointsRoll) is { } dice)
        {
            return dice;
        }

        _log.Unparsed("hit points", $"The hit point roll \"{_m.HitPointsRoll}\" could not be read; rolled hit points use the average.");
        return DamageFormula.Constant(_m.HitPoints);
    }

    private IReadOnlyDictionary<string, int> SaveBonuses()
    {
        var saves = DslValues.Abilities.All.ToDictionary(a => a, a => _m.Abilities.Modifier(a), StringComparer.Ordinal);
        foreach (var proficiency in _m.Proficiencies)
        {
            const string prefix = "saving-throw-";
            if (proficiency.Proficiency.Index.StartsWith(prefix, StringComparison.Ordinal) &&
                ProseText.AbilityKey(proficiency.Proficiency.Index[prefix.Length..]) is { } ability)
            {
                saves[ability] = proficiency.Value;
            }
        }

        return saves;
    }

    private int Initiative()
    {
        var dex = _m.Abilities.Modifier(DslValues.Abilities.Dex);
        if (_override?.Initiative is { } initiative)
        {
            _log.Note(string.Create(CultureInfo.InvariantCulture, $"Initiative {initiative:+0;-0;+0} as the stat block prints it (overrides file; the data has no initiative)."));
            return initiative;
        }

        if (!Is2014)
        {
            _log.Note(string.Create(CultureInfo.InvariantCulture, $"Initiative {dex:+0;-0;+0}: the Dexterity modifier (the 2024 data has no initiative and no override gives one)."));
        }

        return dex;
    }

    private IReadOnlyDictionary<string, int> Speeds()
    {
        var speeds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (mode, text) in new[]
                 {
                     ("walk", _m.Speed.Walk), ("fly", _m.Speed.Fly), ("swim", _m.Speed.Swim), ("climb", _m.Speed.Climb),
                     ("burrow", _m.Speed.Burrow),
                 })
        {
            if (text is null)
            {
                continue;
            }

            var match = Feet().Match(text);
            if (match.Success)
            {
                speeds[mode] = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                _log.Unparsed("speed", $"The {mode} speed \"{text}\" could not be read.");
            }
        }

        speeds.TryAdd("walk", 0);
        return speeds;
    }

    private IReadOnlyList<DamageAdjustment> Adjustments(IReadOnlyList<string> entries, string what) =>
        entries.SelectMany(e => DamageAdjustmentReader.Read(e, what, _log)).ToList();

    private IReadOnlyList<string> ConditionImmunities()
    {
        var result = new List<string>();
        foreach (var reference in _m.ConditionImmunities)
        {
            if (ProseText.ConditionKey(reference.Index) is { } condition)
            {
                result.Add(condition);
                if (reference.Note is { } note)
                {
                    _log.Approximated("condition immunity", $"Immune to {condition} only {note}; the simulator treats it as always immune.");
                }
            }
            else
            {
                _log.Unparsed("condition immunity", $"\"{reference.Index}\" is not a condition the simulator knows.");
            }
        }

        return result;
    }

    private (int Uses, int? InLair) LegendaryResistance()
    {
        var trait = _m.Traits.FirstOrDefault(t => TraitCatalogue.CatalogueName(t.Name).Equals("Legendary Resistance", StringComparison.OrdinalIgnoreCase));
        if (trait is null)
        {
            return (0, null);
        }

        if (trait.Usage?.Times is not { } times)
        {
            _log.Unparsed("trait Legendary Resistance", "Its uses per day could not be read; 3 is used.");
            return (3, null);
        }

        return (times, trait.Usage.TimesInLair is { } lair && lair != times ? lair : null);
    }

    private void ReadTraits()
    {
        foreach (var trait in _m.Traits)
        {
            if (trait.Spellcasting is not null && IsSpellcastingName(trait.Name))
            {
                // Its spells are read into Spells (ReadSpellcasting); the trait itself is not repeated in Traits.
                continue;
            }

            var classified = TraitCatalogue.Classify(trait, _log);
            _traits.AddRange(classified);
            foreach (var t in classified.Where(t => t.Kind == StatBlockValues.TraitKinds.NoCombatEffect))
            {
                var why = TraitCatalogue.NoCombatEffect.GetValueOrDefault(TraitCatalogue.CatalogueName(t.Name)) ?? "senses";
                _log.Note($"Trait {TraitCatalogue.CatalogueName(t.Name)}: no combat effect in the simulator ({why}).");
            }
        }

        _magicWeapons = _traits.Any(t => t.Kind == StatBlockValues.TraitKinds.MagicWeapons);
    }

    private static bool IsSpellcastingName(string name) =>
        name.StartsWith("Spellcasting", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Innate Spellcasting", StringComparison.OrdinalIgnoreCase);

    /// <summary>The data's usage → the stat block's; recharge after a rest is once per fight.</summary>
    private UsageSpec Usage(MonsterUsage? usage, string where)
    {
        if (usage is null)
        {
            return UsageSpec.AtWill;
        }

        switch (usage.Type)
        {
            case UsageTypes.RechargeOnRoll:
                if (usage.MinValue is { } min && usage.Dice is null or "1d6")
                {
                    return new UsageSpec(StatBlockValues.UsageKinds.Recharge, RechargeMin: min);
                }

                _log.Unparsed(where, $"Recharge {usage.Dice} {usage.MinValue} could not be read; it is used once.");
                return new UsageSpec(StatBlockValues.UsageKinds.PerDay, Uses: 1);
            case UsageTypes.PerDay:
                return new UsageSpec(StatBlockValues.UsageKinds.PerDay, Uses: usage.Times ?? 1);
            case UsageTypes.RechargeAfterRest:
                _log.Note($"{where}: recharges after a rest, so it is used once in a fight.");
                return new UsageSpec(StatBlockValues.UsageKinds.PerDay, Uses: 1);
            case UsageTypes.AtWill:
                return UsageSpec.AtWill;
            default:
                _log.Unparsed(where, $"The usage \"{usage.Type}\" is not known; it is treated as at will.");
                return UsageSpec.AtWill;
        }
    }

    [GeneratedRegex(@"(?<n>\d+)\s*ft")]
    private static partial Regex Feet();
}
