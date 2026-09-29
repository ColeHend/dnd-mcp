using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// The single catalogue of monster traits: every trait name either edition uses, mapped to what the simulator does
/// with it (<see cref="StatBlockValues.TraitKinds"/>).
///
/// <para>
/// <b>By name, with the text as the source of numbers.</b> Traits are recognised by their name (parenthetical usage
/// such as "(3/Day)" stripped): the SRD reuses names consistently, and a name catalogue makes a new trait a visible,
/// reviewable diff (<c>TraitCatalogueTests</c> lists every distinct name in both editions with its kind). The numbers a
/// kind needs (Regeneration's hit points and stopping damage types, Sneak Attack's dice, an aura's save and damage)
/// are read from the trait's own text; one the text does not give is a warning, never a guess.
/// </para>
/// <para>
/// A name the catalogue has never seen is <see cref="StatBlockValues.TraitKinds.NotModelled"/> with a warning that
/// says so: an unknown trait must never pass as one without effect.
/// </para>
/// </summary>
internal static partial class TraitCatalogue
{
    /// <summary>Traits with no effect in a fight without a grid, light, terrain, senses or days between fights: why.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoCombatEffect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Abduct"] = "movement while grappling",
        ["Agile"] = "opportunity attacks are not simulated",
        ["Air Form"] = "movement through spaces",
        ["Amorphous"] = "movement through narrow spaces",
        ["Amphibious"] = "breathing",
        ["Antimagic Susceptibility"] = "no antimagic fields in a simulated fight",
        ["Beast of Burden"] = "carrying capacity",
        ["Blind Senses"] = "senses",
        ["Bound"] = "an amulet's telepathic link",
        ["Brute"] = "its extra weapon die is already in its attacks",
        ["Celestial Restoration"] = "revives days later",
        ["Confer Fire Resistance"] = "a rider's resistance",
        ["Demonic Restoration"] = "revives elsewhere after dying",
        ["Devil's Sight"] = "sight in magical darkness (no light model)",
        ["Diabolical Restoration"] = "revives elsewhere after dying",
        ["Divine Awareness"] = "knows lies",
        ["Draconic Origin"] = "its damage type is chosen; the breath it sets is read as untyped",
        ["Earth Glide"] = "burrowing",
        ["Echolocation"] = "senses",
        ["Eldritch Restoration"] = "revives days later",
        ["Elemental Demise"] = "what is left after death",
        ["Elemental Restoration"] = "revives elsewhere after dying",
        ["Ephemeral"] = "carries nothing",
        ["Ethereal Jaunt"] = "moving between planes",
        ["Ethereal Sight"] = "senses",
        ["Exalted Restoration"] = "revives elsewhere after dying",
        ["False Appearance"] = "disguise while motionless",
        ["Faultless Tracker"] = "tracking",
        ["Fiendish Restoration"] = "revives elsewhere after dying",
        ["Flyby"] = "opportunity attacks are not simulated",
        ["Forbiddance"] = "entering residences",
        ["Freeze"] = "speed (no grid)",
        ["Heated Weapons"] = "its extra fire damage is already in its attacks",
        ["Hellish Rejuvenation"] = "revives days later",
        ["Hellish Restoration"] = "revives days later",
        ["Hold Breath"] = "breathing",
        ["Ice Walk"] = "terrain",
        ["Ignited Illumination"] = "light",
        ["Illumination"] = "light",
        ["Immutable Form"] = "shape-changing effects are not simulated",
        ["Incorporeal Movement"] = "movement through creatures and objects",
        ["Incubus Form"] = "changes form after a long rest",
        ["Inscrutable"] = "divination and Insight",
        ["Iron Scent"] = "senses",
        ["Jumper"] = "jumping",
        ["Labyrinthine Recall"] = "memory",
        ["Light Sensitivity"] = "bright light (no light model)",
        ["Limited Amphibiousness"] = "breathing",
        ["Limited Telepathy"] = "telepathy",
        ["Magic Rope"] = "the rope its Entangling Rope action needs",
        ["Mimicry"] = "imitating sounds",
        ["Mucous Cloud"] = "only while underwater",
        ["Mucus Cloud"] = "only while underwater",
        ["Night Hag Items"] = "items",
        ["Ooze Cube"] = "occupying its space (no grid)",
        ["Probing Telepathy"] = "telepathy",
        ["Reactive Heads"] = "extra reactions only for opportunity attacks, which are not simulated",
        ["Rejuvenation"] = "revives days later",
        ["Running Leap"] = "jumping",
        ["Running Water"] = "running water",
        ["Sense Magic"] = "senses",
        ["Shadow Stealth"] = "hiding",
        ["Shapechanger"] = "changing form outside a fight",
        ["Shark Telepathy"] = "commanding sharks",
        ["Shielded Mind"] = "divination",
        ["Siege Monster"] = "damage to objects",
        ["Snow Camouflage"] = "hiding",
        ["Soul Bag"] = "the bag its Nightmare Haunting needs",
        ["Speak with Beasts and Plants"] = "communication",
        ["Spider Climb"] = "climbing",
        ["Spirit Jar"] = "revives days later",
        ["Stake to the Heart"] = "destruction while incapacitated",
        ["Standing Leap"] = "jumping",
        ["Stone Camouflage"] = "hiding",
        ["Succubus Form"] = "changes form after a long rest",
        ["Sunlight"] = "sunlight (no light model)",
        ["Sunlight Sensitivity"] = "sunlight (no light model)",
        ["Sunlight Weakness"] = "sunlight (no light model)",
        ["Swarm"] = "occupying spaces (no grid)",
        ["Tail Spike Regrowth"] = "spikes regrow after a rest",
        ["Telepathic Bond"] = "telepathy",
        ["Training"] = "a skill",
        ["Transparent"] = "hiding",
        ["Treasure Sense"] = "senses",
        ["Tree Stride"] = "movement",
        ["Troll Spawn"] = "becomes a troll a day later",
        ["Tunneler"] = "burrowing",
        ["Turn Defiance"] = "turning is not simulated",
        ["Turn Resistance"] = "turning is not simulated",
        ["Underwater Camouflage"] = "hiding",
        ["Undead Restoration"] = "revives a day later",
        ["Vampire Weakness"] = "running water, sunlight and residences",
        ["Vampire Weaknesses"] = "running water, sunlight and residences",
        ["Vampiric Connection"] = "telepathy",
        ["Variable Illumination"] = "light",
        ["Wakeful"] = "sleep",
        ["Water Breathing"] = "breathing",
        ["Water Form"] = "movement through spaces",
        ["Water Susceptibility"] = "water",
        ["Web Sense"] = "senses",
        ["Web Walker"] = "webbing",
        ["Wishes"] = "a wish outside combat",
    };

    /// <summary>Traits that change a fight but that the simulator does not model: what they do.</summary>
    public static readonly IReadOnlyDictionary<string, string> NotModelled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Aberrant Ground"] = "difficult ground that can reduce Speed to 0",
        ["Acid Absorption"] = "acid damage heals it",
        ["Adhesive"] = "creatures that touch it stick to it",
        ["Aggressive"] = "a bonus action move toward an enemy",
        ["Ambusher"] = "Advantage against surprised creatures",
        ["Assassinate"] = "Advantage and critical hits against creatures that have not acted",
        ["Aura of Authority"] = "Advantage on allies' attacks and saves nearby",
        ["Aversion of Fire"] = "Disadvantage after taking fire damage",
        ["Aversion to Fire"] = "Disadvantage after taking fire damage",
        ["Barbed Hide"] = "damage to creatures grappling it",
        ["Berserk"] = "attacks the nearest creature while berserk",
        ["Blurred Form"] = "Disadvantage on attacks against it",
        ["Brave"] = "Advantage on saves against being frightened",
        ["Charge"] = "extra damage after moving 20+ feet",
        ["Consume Life"] = "kills a creature at 0 hit points",
        ["Corrode Metal"] = "corrodes weapons that hit it",
        ["Coven Magic"] = "spells while near two other hags",
        ["Cunning Action"] = "Dash, Disengage or Hide as a bonus action",
        ["Damage Transfer"] = "shares damage with the creature it is attached to",
        ["Dark Devotion"] = "Advantage on saves against being charmed or frightened",
        ["Divine Eminence"] = "extra radiant damage for a spell slot",
        ["Duergar Resilience"] = "Advantage on saves against poison, spells and illusions",
        ["Fey Ancestry"] = "Advantage on saves against being charmed",
        ["Fire Absorption"] = "fire damage heals it",
        ["Freedom of Movement"] = "magic cannot slow or restrain it",
        ["Gibbering"] = "random behaviour of creatures nearby",
        ["Gnome Cunning"] = "Advantage on mental saves against magic",
        ["Grappler"] = "Advantage against creatures it grapples",
        ["Grasping Tendrils"] = "tendrils that can be attacked",
        ["Horrific Appearance"] = "frightens creatures that see it",
        ["Invisibility"] = "it is invisible",
        ["Lightning Absorption"] = "lightning damage heals it",
        ["Loathsome Limbs"] = "severed limbs become troll limbs",
        ["Misty Escape"] = "turns to mist at 0 hit points",
        ["Multiple Heads"] = "Advantage on saves against several conditions while it has heads",
        ["Nimble Escape"] = "Disengage or Hide as a bonus action",
        ["Petrifying Gaze"] = "petrifies creatures that meet its gaze",
        ["Pounce"] = "knocks prone and bonus-attacks after moving 20+ feet",
        ["Rampage"] = "a bonus-action bite after dropping a creature",
        ["Reactive"] = "a reaction on every turn",
        ["Reflective Carapace"] = "reflects magic missiles, lines and ranged spell attacks",
        ["Rust Metal"] = "corrodes weapons that hit it",
        ["Spell Storing"] = "a stored spell",
        ["Steadfast"] = "cannot be frightened while an ally is near",
        ["Sure-Footed"] = "Advantage on saves against being knocked prone",
        ["Surprise Attack"] = "extra damage against surprised creatures",
        ["Trampling Charge"] = "knocks prone and bonus-attacks after moving 20+ feet",
        ["Two Heads"] = "Advantage on saves against several conditions",
        ["Two-Headed"] = "Advantage on saves against several conditions",
        ["Vile Appearance"] = "frightens creatures that see it",
    };

    /// <summary>The names the catalogue implements, besides <see cref="NoCombatEffect"/> and <see cref="NotModelled"/>.</summary>
    public static readonly IReadOnlyList<string> Implemented =
    [
        "Magic Resistance", "Greater Magic Resistance", "Limited Magic Immunity", "Pack Tactics", "Legendary Resistance",
        "Regeneration", "Undead Fortitude", "Relentless", "Evasion", "Sneak Attack", "Martial Advantage", "Blood Frenzy",
        "Bloodied Frenzy", "Bloodied Fury", "Reckless", "Magic Weapons", "Angelic Weapons", "Hellish Weapons",
        "Heated Body", "Corrosive Form", "Fire Form", "Fire Aura", "Heat Aura", "Stench", "Fear Aura", "Death Burst",
        "Death Throes",
    ];

    /// <summary>The catalogue name of a trait: its name without parenthetical usage ("Legendary Resistance (3/Day)").</summary>
    public static string CatalogueName(string name) => ProseText.StripParentheticals(name);

    /// <summary>
    /// A trait → its classified entries (usually one; Fire Aura is both an aura and retaliation). Numbers come from the
    /// text; the log gets a warning for anything not simulated or not readable.
    /// </summary>
    public static IReadOnlyList<StatBlockTrait> Classify(RecordAction trait, NormalizationLog log)
    {
        var name = CatalogueName(trait.Name);
        var text = ProseText.Normalize(trait.Desc);
        var where = $"trait {name}";
        StatBlockTrait Make(string kind) => new() { Name = trait.Name, Kind = kind, Text = text };

        if (name.StartsWith("Keen ", StringComparison.OrdinalIgnoreCase))
        {
            return [Make(StatBlockValues.TraitKinds.NoCombatEffect)];
        }

        if (NoCombatEffect.TryGetValue(name, out _))
        {
            return [Make(StatBlockValues.TraitKinds.NoCombatEffect)];
        }

        if (NotModelled.TryGetValue(name, out var what))
        {
            log.NotModelled(where, $"Not simulated: {what}.");
            return [Make(StatBlockValues.TraitKinds.NotModelled)];
        }

        switch (name.ToLowerInvariant())
        {
            case "magic resistance":
                return [Make(StatBlockValues.TraitKinds.MagicResistance)];
            case "greater magic resistance":
            case "limited magic immunity":
                log.Approximated(where, "Simulated as Magic Resistance (Advantage on saves against spells and magical effects), which understates it.");
                return [Make(StatBlockValues.TraitKinds.MagicResistance)];
            case "pack tactics":
                return [Make(StatBlockValues.TraitKinds.PackTactics)];
            case "legendary resistance":
                return [Make(StatBlockValues.TraitKinds.LegendaryResistance) with { Uses = trait.Usage?.Times }];
            case "regeneration":
                return [Regeneration(Make(StatBlockValues.TraitKinds.Regeneration), text, where, log)];
            case "undead fortitude":
                return [Make(StatBlockValues.TraitKinds.UndeadFortitude)];
            case "relentless":
            {
                var amount = RelentlessAmount().Match(text);
                if (!amount.Success)
                {
                    log.Unparsed(where, "The damage threshold could not be read; the trait is left out.");
                    return [Make(StatBlockValues.TraitKinds.NotModelled)];
                }

                return [Make(StatBlockValues.TraitKinds.Relentless) with { Amount = int.Parse(amount.Groups["n"].Value, CultureInfo.InvariantCulture) }];
            }

            case "evasion":
                return [Make(StatBlockValues.TraitKinds.Evasion)];
            case "sneak attack":
            case "martial advantage":
            {
                var kind = name.Equals("Sneak Attack", StringComparison.OrdinalIgnoreCase)
                    ? StatBlockValues.TraitKinds.SneakAttack
                    : StatBlockValues.TraitKinds.MartialAdvantage;
                var dice = ProseText.DamageMentions(text).FirstOrDefault()?.Dice;
                if (dice is null)
                {
                    log.Unparsed(where, "Its extra damage dice could not be read; the trait is left out.");
                    return [Make(StatBlockValues.TraitKinds.NotModelled)];
                }

                return [Make(kind) with { Dice = dice }];
            }

            case "blood frenzy":
                return [Make(StatBlockValues.TraitKinds.BloodFrenzy)];
            case "bloodied fury":
                return [Make(StatBlockValues.TraitKinds.AdvantageWhileBloodied)];
            case "bloodied frenzy":
                log.Approximated(where, "Its Advantage on saving throws while Bloodied is not simulated; its Advantage on attack rolls is.");
                return [Make(StatBlockValues.TraitKinds.AdvantageWhileBloodied)];
            case "reckless":
                return [Make(StatBlockValues.TraitKinds.Reckless)];
            case "magic weapons":
            case "angelic weapons":
            case "hellish weapons":
                return [Make(StatBlockValues.TraitKinds.MagicWeapons)];
            case "heated body":
            case "corrosive form":
            case "fire form":
            {
                var retaliation = Retaliation(Make(StatBlockValues.TraitKinds.RetaliationDamage), text, where, log);
                if (name.Equals("Fire Form", StringComparison.OrdinalIgnoreCase) || name.Equals("Corrosive Form", StringComparison.OrdinalIgnoreCase))
                {
                    log.Approximated(where, "Only the damage to a creature that hits it in melee is simulated (not entering spaces or corroding weapons).");
                }

                return [retaliation];
            }

            case "fire aura":
            case "heat aura":
                return Aura(trait, text, where, log);
            case "stench":
            case "fear aura":
                return [AuraCondition(Make(StatBlockValues.TraitKinds.AuraDamage), text, where, log)];
            case "death burst":
            case "death throes":
                return [DeathBurst(Make(StatBlockValues.TraitKinds.DeathBurst), text, where, log)];
            default:
                log.NotModelled(where, "A trait the normalizer's catalogue does not know; it is not simulated.");
                return [Make(StatBlockValues.TraitKinds.NotModelled)];
        }
    }

    private static StatBlockTrait Regeneration(StatBlockTrait trait, string text, string where, NormalizationLog log)
    {
        var amount = RegainsAmount().Match(text);
        if (!amount.Success)
        {
            log.Unparsed(where, "The hit points it regains could not be read; the trait is left out.");
            return trait with { Kind = StatBlockValues.TraitKinds.NotModelled };
        }

        var types = new List<string>();
        var stop = StoppedBy().Match(text);
        if (stop.Success)
        {
            foreach (var word in stop.Groups["types"].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (ProseText.DamageType(word) is { } type && !types.Contains(type))
                {
                    types.Add(type);
                }
            }
        }

        if (text.Contains("sunlight", StringComparison.OrdinalIgnoreCase) || text.Contains("holy water", StringComparison.OrdinalIgnoreCase))
        {
            log.Approximated(where, "Sunlight, running water and holy water are not simulated; only the damage types stop it.");
        }

        return trait with { Amount = int.Parse(amount.Groups["n"].Value, CultureInfo.InvariantCulture), DamageTypes = types };
    }

    private static StatBlockTrait Retaliation(StatBlockTrait trait, string text, string where, NormalizationLog log)
    {
        var mention = ProseText.DamageMentions(text).FirstOrDefault(m => m.Type is not null);
        if (mention is null)
        {
            log.NotModelled(where, $"Not simulated: {text[..ProseText.SentenceEnd(text, 0)]}");
            return trait with { Kind = StatBlockValues.TraitKinds.NotModelled };
        }

        return trait with { Damage = [new DamageRoll(mention.Dice, mention.Type)] };
    }

    // Balor/fire elemental "Fire Aura": damage to creatures nearby at a turn boundary, and (2014) to creatures that hit it;
    // what it sets alight is warned: not_modelled when creatures start burning (2024 fire elemental; the Burning hazard's
    // damage is dropped), approximated for the 2014 "ignite" wording (the balor's aura sets only objects alight).
    private static IReadOnlyList<StatBlockTrait> Aura(RecordAction trait, string text, string where, NormalizationLog log)
    {
        var mentions = ProseText.DamageMentions(text).Where(m => m.Type is not null).ToList();
        if (mentions.Count == 0)
        {
            log.Unparsed(where, "Its damage could not be read; the trait is left out.");
            return [new StatBlockTrait { Name = trait.Name, Kind = StatBlockValues.TraitKinds.NotModelled, Text = text }];
        }

        var area = ProseText.ReadArea(text) ?? (WithinFeet().Match(text) is { Success: true } within
            ? new AreaSpec(StatBlockValues.Shapes.Emanation, int.Parse(within.Groups["n"].Value, CultureInfo.InvariantCulture))
            : new AreaSpec(StatBlockValues.Shapes.Emanation, 5));
        var save = ProseText.ReadSave(text);
        var aura = new StatBlockTrait
        {
            Name = trait.Name,
            Kind = StatBlockValues.TraitKinds.AuraDamage,
            Text = text,
            Damage = [new DamageRoll(mentions[0].Dice, mentions[0].Type)],
            Area = area,
            Save = save is null ? null : new SaveSpec(save.Ability, save.Dc, ProseText.SaysHalf(text) ? StatBlockValues.OnSuccess.Half : StatBlockValues.OnSuccess.None),
        };

        var result = new List<StatBlockTrait> { aura };
        if (HitsItInMelee().IsMatch(text))
        {
            var last = mentions[^1];
            result.Add(new StatBlockTrait
            {
                Name = trait.Name,
                Kind = StatBlockValues.TraitKinds.RetaliationDamage,
                Text = text,
                Damage = [new DamageRoll(last.Dice, last.Type)],
            });
        }

        if (ProseText.StartsBurning().Match(text) is { Success: true } burning)
        {
            // 2024 fire elemental: every creature in the aura also takes the Burning hazard's damage each turn until
            // the fire is put out. That is damage the simulator drops, not a simplification of the aura's.
            var sentence = text[ProseText.SentenceStart(text, burning.Index)..ProseText.SentenceEnd(text, burning.Index)].Trim();
            log.NotModelled(where, $"Not simulated: \"{sentence}\" ({ProseText.BurningHazard}) The aura's own damage is.");
        }
        else if (text.Contains("ignite", StringComparison.OrdinalIgnoreCase) || text.Contains("catches fire", StringComparison.OrdinalIgnoreCase))
        {
            // 2014 balor: "flammable objects in the aura that aren't being worn or carried ignite".
            log.Approximated(where, "Setting objects or creatures alight is not simulated; the aura's damage is.");
        }

        return result;
    }

    // Stench / Fear Aura: a save at the start of a nearby creature's turn, a condition on a failure, no damage.
    private static StatBlockTrait AuraCondition(StatBlockTrait trait, string text, string where, NormalizationLog log)
    {
        var save = ProseText.ReadSave(text);
        var conditions = ProseText.ConditionMentions(text);
        if (save is null || conditions.Count == 0)
        {
            log.Unparsed(where, "Its save or condition could not be read; the trait is left out.");
            return trait with { Kind = StatBlockValues.TraitKinds.NotModelled };
        }

        var spec = new SaveSpec(save.Ability, save.Dc, StatBlockValues.OnSuccess.None);
        var reading = ProseText.ReadDuration(text, conditions[0].Index, spec);
        var area = ProseText.ReadArea(text) ?? (WithinFeet().Match(text) is { Success: true } within
            ? new AreaSpec(StatBlockValues.Shapes.Emanation, int.Parse(within.Groups["n"].Value, CultureInfo.InvariantCulture))
            : new AreaSpec(StatBlockValues.Shapes.Emanation, 5));
        if (text.Contains("immune", StringComparison.OrdinalIgnoreCase))
        {
            log.Approximated(where, "A creature that succeeds is immune to it for 24 hours in the text; the simulator has it save every turn.");
        }

        return trait with
        {
            Save = spec,
            Area = area,
            Condition = new ConditionEffect
            {
                Condition = conditions[0].Condition,
                Duration = reading?.Duration ?? StatBlockValues.Durations.UntilStartOfTargetTurn,
                Rounds = reading?.Rounds,
            },
        };
    }

    private static StatBlockTrait DeathBurst(StatBlockTrait trait, string text, string where, NormalizationLog log)
    {
        var save = ProseText.ReadSave(text);
        if (save is null)
        {
            log.Unparsed(where, "Its save could not be read; the trait is left out.");
            return trait with { Kind = StatBlockValues.TraitKinds.NotModelled };
        }

        var effect = text[save.Index..];
        var damage = ProseText.DamageMentions(effect).Where(m => m.Type is not null).Select(m => new DamageRoll(m.Dice, m.Type)).ToList();
        var spec = new SaveSpec(save.Ability, save.Dc, ProseText.SaysHalf(effect) ? StatBlockValues.OnSuccess.Half : StatBlockValues.OnSuccess.None);
        var conditions = ProseText.ConditionMentions(effect);
        ConditionEffect? condition = null;
        if (conditions.Count > 0)
        {
            var reading = ProseText.ReadDuration(effect, conditions[0].Index, spec);
            condition = new ConditionEffect
            {
                Condition = conditions[0].Condition,
                Duration = reading?.Duration ?? StatBlockValues.Durations.Fight,
                Rounds = reading?.Rounds,
                SaveEnds = reading?.Repeats == true ? spec with { OnSuccess = StatBlockValues.OnSuccess.None } : null,
            };
        }

        if (damage.Count == 0 && condition is null)
        {
            log.Unparsed(where, "It names no damage or condition the normalizer can read; the trait is left out.");
            return trait with { Kind = StatBlockValues.TraitKinds.NotModelled };
        }

        var area = ProseText.ReadArea(text) ?? (WithinFeet().Match(text) is { Success: true } within
            ? new AreaSpec(StatBlockValues.Shapes.Emanation, int.Parse(within.Groups["n"].Value, CultureInfo.InvariantCulture))
            : null);
        if (area is null)
        {
            log.Unparsed(where, "Its area could not be read; a 5-foot emanation is used.");
            area = new AreaSpec(StatBlockValues.Shapes.Emanation, 5);
        }

        return trait with { Save = spec, Damage = damage, Area = area, Condition = condition };
    }

    [GeneratedRegex(@"takes (?<n>\d+) damage or less", RegexOptions.IgnoreCase)]
    private static partial Regex RelentlessAmount();

    [GeneratedRegex(@"regains (?<n>\d+) (?:hit points|Hit Points)", RegexOptions.IgnoreCase)]
    private static partial Regex RegainsAmount();

    [GeneratedRegex(@"takes (?<types>(?:(?:acid|bludgeoning|cold|fire|force|lightning|necrotic|piercing|poison|psychic|radiant|slashing|thunder)(?:,\s*|\s+or\s+|,\s*or\s+)?)+) damage", RegexOptions.IgnoreCase)]
    private static partial Regex StoppedBy();

    [GeneratedRegex(@"within (?<n>\d+) (?:feet|ft\.)", RegexOptions.IgnoreCase)]
    private static partial Regex WithinFeet();

    [GeneratedRegex(@"hits it with a melee attack|hits the [a-z ]+ with a melee attack", RegexOptions.IgnoreCase)]
    private static partial Regex HitsItInMelee();
}
