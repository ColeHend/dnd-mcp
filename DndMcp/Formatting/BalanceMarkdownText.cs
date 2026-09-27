using System.Globalization;
using System.Text;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Srd;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Formatting;

/// <summary>
/// The blocks <c>balance_dpr</c> and <c>balance_compare</c> share: the build as read, the target, the per-attack and
/// per-rider breakdown, the round-1 spread, power attacks, save effects, the day's arithmetic, the assumptions and the
/// sources. One renderer for both tools, so a build reads the same in either and a number can be traced from one to the
/// other.
///
/// <para>
/// <b>Every number carries what it was computed against.</b> A DPR figure is useless without its horizon, its target
/// and the rulings, so each block says them where they apply ("per round over a 3-round fight", "+7 vs AC 15") instead of
/// once at the top where a reader of one table would miss them. <b>Probabilities never round to certainty</b>: 0.99996
/// prints as "> 99.99%", never "100%", because "P(all four die) 100%" is a claim the maths does not make.
/// </para>
/// <para>
/// <b>Bounded</b>: every list here is bounded by the DSL's limits (10 attacks, 20 modifiers, 20 targets, 20 levels × 16
/// ACs), so the largest result stays well under the 10,000-token warning; the integration tests pin the ceiling with a
/// build that uses nearly every modifier kind over 20 levels and a 16-AC grid.
/// </para>
/// </summary>
internal static class BalanceMarkdownText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// What a figure the engine could not give prints as. The engine guards its divisions, so this should never show; if a
    /// non-finite value slips through, a dash in one cell is honest, where an exception would replace the whole answer with
    /// the SDK's generic error.
    /// </summary>
    private const string NotANumber = "—";

    /// <summary>The sources behind the default target and the reference curves: never SRD text, so always named.</summary>
    public const string DmgRowSource =
        "the DMG 2014 \"Monster Statistics by Challenge Rating\" (pp. 274–275; not SRD text)";

    public const string AreaSource = "the DMG 2014 \"Targets in Areas of Effect\" (p. 249; not SRD text)";

    /// <summary>"19.61": a damage figure, to the two decimals the research prints (8.505 → "8.51").</summary>
    public static string Dpr(double value)
    {
        if (!double.IsFinite(value))
        {
            return NotANumber;
        }

        var rounded = Round(value, 2);
        return rounded < 0 ? "−" + (-rounded).ToString("0.00", Invariant) : rounded.ToString("0.00", Invariant);
    }

    /// <summary>"+0.80", "−1.25" (a true minus sign), "0.00": a difference.</summary>
    public static string SignedDpr(double value)
    {
        if (!double.IsFinite(value))
        {
            return NotANumber;
        }

        var rounded = Round(value, 2);
        return rounded switch
        {
            > 0 => "+" + rounded.ToString("0.00", Invariant),
            < 0 => "−" + (-rounded).ToString("0.00", Invariant),
            _ => "0.00",
        };
    }

    /// <summary>
    /// <paramref name="value"/> rounded half away from zero on its decimal digits, not its binary expansion: the double
    /// nearest 8.505 is 8.50499999…, which "0.00" prints as 8.50, while the exact value (1701/200, the smite golden) and
    /// every table a reader checks against say 8.51. The conversion to decimal keeps 15 significant digits, which is where
    /// the double's representation error lives, as <see cref="LevelEquivalents.Significant"/> reads it.
    /// </summary>
    public static decimal Round(double value, int decimals) =>
        double.IsFinite(value) && Math.Abs(value) < 1e20
            ? Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Only a finite number below 1e20 is printed here.");

    /// <summary>"+3.3%", "−0.4%": a relative difference; null (the baseline deals nothing) prints as "—".</summary>
    public static string SignedPercent(double? fraction)
    {
        if (fraction is not { } value || !double.IsFinite(value))
        {
            return NotANumber;
        }

        var rounded = Round(value * 100, 1);
        return rounded switch
        {
            > 0 => "+" + rounded.ToString("0.0", Invariant) + "%",
            < 0 => "−" + (-rounded).ToString("0.0", Invariant) + "%",
            _ => "0.0%",
        };
    }

    /// <summary>
    /// "65%", "87.75%", "99.93%": a probability to at most two decimals, and never rounded onto 0% or 100% when it is
    /// neither ("< 0.01%", "> 99.99%").
    /// </summary>
    public static string Percent(double probability)
    {
        if (double.IsNaN(probability))
        {
            return NotANumber;
        }

        if (probability <= 0)
        {
            return "0%";
        }

        if (probability >= 1)
        {
            return "100%";
        }

        var percent = Round(probability * 100, 2);
        return percent switch
        {
            <= 0 => "< 0.01%",
            >= 100 => "> 99.99%",
            _ => percent.ToString("0.##", Invariant) + "%",
        };
    }

    /// <summary>"2", "0.8775", "0.3333": a count per round, to four decimals, trailing zeros dropped.</summary>
    public static string Rate(double value) => double.IsFinite(value) ? Round(value, 4).ToString("0.####", Invariant) : NotANumber;

    public static string Number(long value) => value.ToString(Invariant);

    /// <summary>
    /// "90%" in a grid cell, where two decimals on every cell of a 20 × 16 table cost more than they tell; never rounded
    /// onto 0% or 100%, which the cell's "PA off" and plain forms mean.
    /// </summary>
    public static string WholePercent(double probability)
    {
        if (double.IsNaN(probability))
        {
            return NotANumber;
        }

        var percent = Round(probability * 100, 0);
        return percent switch
        {
            <= 0 => "<1%",
            >= 100 => ">99%",
            _ => percent.ToString("0", Invariant) + "%",
        };
    }

    /// <summary>
    /// A level-equivalent as the Domain rounded it (two significant figures, "0.080", "1.3"), with a true minus sign like
    /// every other signed number in the result ("−1.0").
    /// </summary>
    public static string LevelEquivalentText(LevelEquivalent levelEquivalent) =>
        levelEquivalent.Text.StartsWith('-') ? "−" + levelEquivalent.Text[1..] : levelEquivalent.Text;

    /// <summary>"once", "0.3333 times": how often something happens a round.</summary>
    public static string Times(double value) => Rate(value) == "1" ? "once" : $"{Rate(value)} times";

    public static string Signed(int value) => SrdMarkdownText.Signed(value);

    public static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "- " + l));

    /// <summary>"1 target", "4 targets".</summary>
    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{Number(count)} {noun}s";

    /// <summary>"Str 18 (+4)".</summary>
    private static string Score(ResolvedAbilities abilities, string ability) =>
        $"{V.Abilities.Display(ability)} {Number(abilities.Score(ability))} ({Signed(abilities.Modifier(ability))})";

    // ---------------------------------------------------------------------------------------------------------------
    // The build as read
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The build as the resolver read it at one level: proficiency and scores, each attack with its to-hit and damage
    /// parts, and each modifier the engine decides or tracks. Folded modifiers (to_hit, bonus_damage, crit_range, remaps,
    /// lucky, elven_accuracy, ignore_cover) show up inside the attacks they changed, which is where their effect is.
    /// </summary>
    public static string BuildAsRead(ResolvedBuild build, string heading)
    {
        var lines = new List<string>
        {
            $"Proficiency bonus {Signed(build.ProficiencyBonus)}; {string.Join(", ", V.Abilities.All.Select(a => Score(build.Abilities, a)))}." +
            (build.FightingStyle is { } style ? $" Fighting style: {V.FightingStyles.Display(style)}." : string.Empty),
        };

        var blocks = new List<string?> { heading, string.Join("\n", lines) };
        if (build.Attacks.Count > 0)
        {
            blocks.Add(SrdMarkdownText.Table(
                ["Attack", "Per turn", "To hit", "Damage on a hit", "Also"],
                build.Attacks.Select(a => (IReadOnlyList<string>)[a.Name, PerTurn(a), ToHit(a), DamageText(a), AttackTraits(a)])));
        }
        else
        {
            blocks.Add("No attacks at this level.");
        }

        var modifiers = ModifierLines(build).ToList();
        if (modifiers.Count > 0)
        {
            blocks.Add(Bullets(modifiers));
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    private static string PerTurn(ResolvedAttack attack) =>
        $"{Number(attack.Count)} × {(attack.Action == V.AttackActions.BonusAction ? "Bonus Action" : "Attack action")}";

    // "+7 (Str +4, proficiency +3) +1d4 (Bless); crit 19–20; Lucky".
    private static string ToHit(ResolvedAttack attack)
    {
        var text = new StringBuilder(Signed(attack.AttackBonus));
        if (attack.ToHitParts.Count > 0)
        {
            text.Append(" (").Append(string.Join(", ", attack.ToHitParts.Select(p => $"{p.Label} {Signed(p.Value)}"))).Append(')');
        }

        foreach (var dice in attack.ToHitDice)
        {
            text.Append(' ').Append(SignedDice(dice.Dice.Text)).Append(" (").Append(dice.Label).Append(')');
        }

        if (attack.CritMin < 20)
        {
            text.Append("; crit ").Append(Number(attack.CritMin)).Append("–20");
        }

        if (attack.Lucky)
        {
            text.Append("; Lucky");
        }

        if (attack.ElvenAccuracy)
        {
            text.Append("; Elven Accuracy");
        }

        return text.ToString();
    }

    // "1d4" → "+1d4", "-1d4" → "−1d4": dice added to a roll read as a sum.
    private static string SignedDice(string text) => text.StartsWith('-') ? "−" + text[1..] : "+" + text;

    // "2d6 slashing +4 (Str +4) +3 (Great Weapon Master, Attack action only)".
    private static string DamageText(ResolvedAttack attack)
    {
        var text = new StringBuilder(attack.Damage.Text).Append(' ').Append(attack.DamageType ?? "typeless");
        foreach (var part in attack.DamageParts)
        {
            text.Append(' ').Append(Signed(part.Value)).Append(" (").Append(part.Label);
            if (part.AttackActionOnly)
            {
                text.Append(", Attack action only");
            }

            text.Append(')');
        }

        return text.ToString();
    }

    // "melee, heavy, two-handed; mastery graze; Great Weapon Fighting (fighting style): 1–2 count as 3".
    private static string AttackTraits(ResolvedAttack attack)
    {
        var traits = new List<string>();
        if (attack.Properties.Count > 0)
        {
            traits.Add(string.Join(", ", attack.Properties));
        }

        if (attack.Offhand)
        {
            traits.Add("offhand");
        }

        if (attack.Mastery is { } mastery)
        {
            traits.Add($"mastery {mastery}");
        }

        if (attack.Cantrip is { } cantrip)
        {
            traits.Add($"cantrip {cantrip} ×{Number(attack.CantripMultiplier)}");
        }

        if (attack.WeaponRemap is { } remap)
        {
            traits.Add($"{attack.WeaponRemapSource ?? remap}: {RemapText(remap)}");
        }

        if (attack.ElementalAdeptTypes.Count > 0)
        {
            traits.Add($"Elemental Adept ({string.Join(", ", attack.ElementalAdeptTypes)}): a 1 counts as 2");
        }

        if (attack.IgnoresCover)
        {
            traits.Add("ignores cover");
        }

        return string.Join("; ", traits);
    }

    private static string RemapText(string remap) => remap switch
    {
        V.Remaps.Gwf2014 => "reroll a 1 or 2 once",
        V.Remaps.Gwf2024 => "a 1 or 2 counts as 3",
        V.Remaps.ElementalAdept => "a 1 counts as 2",
        _ => remap,
    };

    /// <summary>One line per modifier the engine decides or tracks, in build order.</summary>
    private static IEnumerable<string> ModifierLines(ResolvedBuild build)
    {
        var lines = new List<(int Number, string Text)>();
        foreach (var rider in build.Riders)
        {
            var parts = new List<string> { $"{rider.Damage.Text} {rider.DamageType ?? "typeless"}", WhenText(rider.When) };
            if (rider.IsOptional)
            {
                parts.Add(PolicyText(rider.Policy, rider.UseValue));
            }

            if (!rider.CritDoubles && rider.When != V.When.OnCrit && rider.When != V.When.OnMiss)
            {
                parts.Add("not doubled on a crit");
            }

            if (rider.AttackActionOnly)
            {
                parts.Add("Attack action only");
            }

            if (rider.ActionCost == V.ActionCosts.BonusAction)
            {
                parts.Add("spending it uses the Bonus Action");
            }

            lines.Add((rider.Source.Number, Line(rider.Source, parts, rider.Resource, rider.Concentration, build, rider.Attacks)));
        }

        foreach (var extra in build.ExtraAttacks)
        {
            var how = extra.Action switch
            {
                V.ExtraAttackActions.Action => "as a second Attack action",
                V.ExtraAttackActions.BonusAction => "with the Bonus Action",
                _ => $"as a reaction on {Percent(extra.TriggerProbability ?? 0)} of rounds",
            };
            var trigger = extra.Trigger switch
            {
                V.Triggers.Hit => ", after a hit this turn",
                V.Triggers.Crit => ", after a melee crit this turn",
                _ => string.Empty,
            };
            lines.Add((extra.Source.Number, Line(extra.Source, [$"{Number(extra.Count)} × {extra.Attack} {how}{trigger}"], extra.Resource, extra.Concentration, build, null)));
        }

        foreach (var effect in build.SaveEffects)
        {
            var parts = new List<string> { $"{V.Abilities.Display(effect.Ability)} save DC {Number(effect.Dc)} ({DcText(effect.DcParts)})" };
            if (effect.Damage is { } damage)
            {
                parts.Add($"{damage.Text} {effect.DamageType ?? "typeless"}, {(effect.OnSuccess == V.OnSuccess.Half ? "half" : "none")} on a success");
            }

            parts.Add(effect.TargetsFromArea
                ? $"{Plural(effect.Targets, "target")} ({Number(effect.Size ?? 0)}-ft {effect.Shape})"
                : Plural(effect.Targets, "target"));
            if (effect.Condition is { } condition)
            {
                parts.Add(effect.ConditionIsMechanical ? condition : $"{condition} (a label only)");
            }

            parts.Add(effect.ActionCost switch
            {
                V.ActionCosts.Action => "uses the Action",
                V.ActionCosts.BonusAction => "uses the Bonus Action",
                _ => "costs no action",
            });
            if (!effect.Magical)
            {
                parts.Add("not magical");
            }

            lines.Add((effect.Source.Number, Line(effect.Source, parts, effect.Resource, effect.Concentration, build, null)));
        }

        foreach (var condition in build.ConditionsOnHit)
        {
            var dcs = condition.Dcs.Select(d => d.Dc).Distinct().Count() == 1
                ? $"DC {Number(condition.Dcs[0].Dc)}"
                : string.Join(", ", condition.Dcs.Select(d => $"DC {Number(d.Dc)} ({d.Attack})"));
            var parts = new List<string>
            {
                $"{condition.Condition} on a failed {V.Abilities.Display(condition.Ability)} save, {dcs}",
                WhenText(condition.When),
                PolicyText(condition.Policy, condition.UseValue),
            };
            lines.Add((condition.Source.Number, Line(condition.Source, parts, condition.Resource, condition.Concentration, build, condition.Attacks)));
        }

        foreach (var source in build.AdvantageSources)
        {
            var mode = source.Mode == V.AdvantageModes.Advantage ? "Advantage" : "Disadvantage";
            var rate = source.Rate >= 1 ? "every turn" : $"on {Percent(source.Rate)} of turns";
            lines.Add((source.Source.Number, Line(source.Source, [$"{mode} {rate}"], null, false, build, source.Attacks)));
        }

        foreach (var power in build.PowerAttacks)
        {
            lines.Add((power.Source.Number, Line(power.Source, [$"−{Number(power.Penalty)} to hit, +{Number(power.Bonus)} damage", $"policy {power.Policy}"], null, false, build, power.Attacks)));
        }

        foreach (var reroll in build.DamageRerolls)
        {
            lines.Add((reroll.Source.Number, Line(
                reroll.Source, ["once per turn, roll a weapon hit's damage dice twice and use either", PolicyText(reroll.Policy, reroll.UseValue)], null, false, build, reroll.Attacks)));
        }

        foreach (var remap in build.Remaps.Where(r => r.Source is not null))
        {
            var text = remap.DamageType is { } type ? $"{RemapText(remap.Remap)} on {type} dice" : RemapText(remap.Remap);
            lines.Add((remap.Source!.Number, Line(remap.Source, [text], null, false, build, remap.Attacks)));
        }

        foreach (var defensive in build.Defensive)
        {
            var amount = defensive.Amount is { } value ? Signed(value) : string.Empty;
            var what = string.Join(" ", new[] { amount, defensive.DamageType }.Where(s => !string.IsNullOrEmpty(s)));
            var parts = new List<string> { "defensive: kept for the simulator, no effect on damage dealt" };
            if (what.Length > 0)
            {
                parts.Insert(0, what);
            }

            lines.Add((defensive.Source.Number, Line(defensive.Source, parts, defensive.Resource, false, build, null)));
        }

        return lines.OrderBy(l => l.Number).Select(l => l.Text);
    }

    // "Divine Smite (extra_damage): 2d8 radiant, first hit each turn, policy any_hit; 4 per long rest; on Longsword."
    private static string Line(
        ModifierRef source, IEnumerable<string> parts, ResolvedResource? resource, bool concentration, ResolvedBuild build, IReadOnlyList<string>? attacks)
    {
        var text = new StringBuilder($"{source.Label} ({source.Kind}): ").Append(string.Join(", ", parts));
        if (resource is not null)
        {
            text.Append("; ").Append(ResourceText(resource));
        }

        if (concentration)
        {
            text.Append("; Concentration");
        }

        if (attacks is not null && attacks.Count < build.Attacks.Count)
        {
            text.Append(attacks.Count == 0 ? "; applies to no attack at this level" : $"; on {string.Join(", ", attacks)}");
        }

        return text.Append('.').ToString();
    }

    /// <summary>"2 per short rest".</summary>
    public static string ResourceText(ResolvedResource resource) =>
        $"{Number(resource.Uses)} per {(resource.Per == V.Rests.ShortRest ? "short" : "long")} rest";

    private static string WhenText(string when) => when switch
    {
        V.When.EveryHit => "every hit",
        V.When.FirstHitPerTurn => "first hit each turn",
        V.When.OnCrit => "on a crit (added once)",
        V.When.OnMiss => "on a miss",
        _ => when,
    };

    /// <summary>"policy optimal (use_value 9)", "policy any_hit".</summary>
    public static string PolicyText(string policy, double useValue) =>
        policy == V.Policies.Optimal ? $"policy optimal (use_value {Rate(useValue)})" : $"policy {policy}";

    // "given", "8 + proficiency 3 + Int 4".
    private static string DcText(IReadOnlyList<NamedValue> parts) =>
        parts.Count == 1 && parts[0].Label == "given"
            ? "given"
            : string.Join(" + ", parts.Select((p, i) => i == 0 && p.Label == "base" ? Number(p.Value) : $"{p.Label} {Number(p.Value)}"));

    // ---------------------------------------------------------------------------------------------------------------
    // The target
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "AC 15", or for a build with no attack rolls "a target with +2 on every save": what the headline says the build was
    /// measured against. The AC of a save-only build's target is true but beside the point.
    /// </summary>
    public static string TargetShort(ResolvedTarget target, ResolvedBuild build)
    {
        if (build.Attacks.Count == 0)
        {
            return "a target with " + SavesText(target);
        }

        return $"AC {Number(target.ArmorClass)}" +
               (target.CoverBonus > 0 ? $" (+{Number(target.CoverBonus)} for {target.Cover?.Replace('_', '-')} cover)" : string.Empty);
    }

    // "+2 on every save", or "Str +0, Dex +2, …" when they differ.
    private static string SavesText(ResolvedTarget target) =>
        target.SaveBonuses.Values.Distinct().Count() == 1
            ? $"{Signed(target.SaveBonuses.Values.First())} on every save"
            : string.Join(", ", V.Abilities.All.Select(a => $"{V.Abilities.Display(a)} {Signed(target.SaveBonus(a))}"));

    /// <summary>The target at one level: AC and saves with their sources, and every other setting that was given.</summary>
    public static string Target(ResolvedTarget target, string heading)
    {
        var uniform = target.SaveBonuses.Values.Distinct().Count() == 1;
        var saves = SavesText(target);
        var lines = new List<string>
        {
            $"AC {Number(target.ArmorClass)} ({target.ArmorClassSource}).",
            $"Saves {saves} ({SaveSource(target, uniform)}).",
        };

        var other = new List<string>();
        if (target.HitPoints is { } hp)
        {
            other.Add($"{Number(hp)} hit points");
        }

        AddTypes(other, "resists", target.Resistances);
        AddTypes(other, "vulnerable to", target.Vulnerabilities);
        AddTypes(other, "immune to", target.Immunities);
        if (target.MagicResistance)
        {
            other.Add("Magic Resistance (Advantage on saves against magical effects)");
        }

        if (target.Evasion)
        {
            other.Add("Evasion");
        }

        if (target.Condition is { } condition)
        {
            other.Add($"{condition} at the start of every turn");
        }

        if (target.Cover is { } cover)
        {
            other.Add($"{cover.Replace('_', '-')} cover: +{Number(target.CoverBonus)} to AC (except against attacks that ignore cover) and to Dex saves");
        }

        if (target.LegendaryResistance > 0)
        {
            other.Add($"{Number(target.LegendaryResistance)} Legendary Resistance{(target.LegendaryResistance == 1 ? "" : "s")}");
        }

        if (target.SaveDice is { } dice)
        {
            other.Add($"{SignedDice(dice.Text)} on its saves");
        }

        if (target.SecondTargetRate > 0)
        {
            other.Add($"a second creature within reach {Percent(target.SecondTargetRate)} of the time (Cleave)");
        }

        if (other.Count > 0)
        {
            lines.Add(char.ToUpperInvariant(other[0][0]) + string.Join("; ", other)[1..] + ".");
        }

        return heading + "\n\n" + string.Join(" ", lines);
    }

    // The resolver's source text opens with the bonus itself ("+2, the typical save bonus …"); beside a uniform "+2 on
    // every save" that number is said twice, so it is dropped there. Any other wording is printed as it is.
    private static string SaveSource(ResolvedTarget target, bool uniform)
    {
        var source = target.SaveBonusSource;
        if (!uniform)
        {
            return source;
        }

        var value = target.SaveBonuses.Values.First();
        var prefix = (value >= 0 ? "+" : "-") + Math.Abs(value).ToString(Invariant) + ", ";
        return source.StartsWith(prefix, StringComparison.Ordinal) ? source[prefix.Length..] : source;
    }

    private static void AddTypes(List<string> into, string verb, IReadOnlyList<string> types)
    {
        if (types.Count > 0)
        {
            into.Add($"{verb} {string.Join(", ", types)}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The breakdown
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>"per round over a 3-round fight", "in round 1": what a breakdown's per-round numbers average over.</summary>
    public static string PerRound(DprResult result) =>
        result.Horizon == DprHorizons.Round1 ? "in round 1" : $"per round over a {Number(result.Rounds)}-round fight";

    /// <summary>
    /// Where the damage came from: one row per attack line (Attack action, Action Surge, Bonus Action, Hew, Cleave,
    /// reaction) with the odds the turns actually saw, then the riders with uses and damage per use, then what the Bonus
    /// Action and Reaction went to and how conditions landed.
    /// </summary>
    public static string Breakdown(DprResult result, string heading)
    {
        var blocks = new List<string?> { heading };
        if (result.Attacks.Count > 0)
        {
            blocks.Add(SrdMarkdownText.Table(
                ["Attack", "Made as", "Attacks", "To hit", "P(hit)", "P(crit)", "Damage"],
                result.Attacks.Select(a => (IReadOnlyList<string>)
                [
                    a.Attack,
                    UseText(a),
                    Rate(a.AttacksPerRound),
                    $"{Signed(a.AttackBonus)} vs AC {Number(a.TargetArmorClass)}",
                    Percent(a.HitChance),
                    Percent(a.CritChance),
                    Dpr(a.DamagePerRound),
                ])));
        }

        if (result.PowerAttacks.Count > 0)
        {
            blocks.Add("P(hit) is averaged over the turns and includes the power attack's penalty when it is on; To hit is the bonus before it.");
        }

        if (result.Riders.Count > 0)
        {
            blocks.Add(SrdMarkdownText.Table(
                ["Rider", "When", "Uses", "Damage per use", "Damage"],
                result.Riders.Select(r => (IReadOnlyList<string>)
                [
                    r.Name,
                    RiderWhen(r),
                    Rate(r.UsesPerRound),
                    r.DamagePerUse is { } perUse ? Dpr(perUse) : "never used",
                    Dpr(r.DamagePerRound),
                ])));
        }

        // An extra attack's own row is in the table; its uses are worth a line only when they are a resource's (Action
        // Surge), where "attacks a round" and "uses a round" differ by its count.
        var lines = new List<string>();
        foreach (var extra in result.ExtraAttacks.Where(e => e.Resource is not null && e.Action != V.ExtraAttackActions.Reaction))
        {
            lines.Add($"{extra.Name}: taken {Times(extra.UsesPerRound)} a round ({ResourceText(extra.Resource!)}), {Dpr(extra.DamagePerRound)} damage a round.");
        }

        foreach (var condition in result.Conditions)
        {
            var perFight = condition.LandChancePerFight is { } fight ? $", at least once in the fight {Percent(fight)}" : string.Empty;
            lines.Add(
                $"{condition.Name}: {Rate(condition.AttemptsPerRound)} {V.Abilities.Display(condition.Ability)} saves forced and " +
                $"{Rate(condition.LandsPerRound)} failed a round; the target is {condition.Condition} in a turn {Percent(condition.LandChancePerTurn)}{perFight}.");
        }

        // The engine tallies a choice only where one was open (an option available, the Bonus Action still free): "none"
        // is "chose nothing although it could", not "unused", so rounds with nothing to choose are simply not counted.
        // With one option and nothing declined, the option's own row already says how often it was taken.
        var chosen = result.BonusActionChoices.Where(c => c.ChosenPerRound > 0).ToList();
        if (result.BonusActionChoices.Count > 2 || chosen.Any(c => c.Option == "none"))
        {
            lines.Add(
                "Bonus Action, when there was something to spend it on: " +
                string.Join("; ", chosen.Select(c => $"{(c.Option == "none" ? "nothing, by choice," : c.Option)} {Percent(c.ChosenPerRound)} of rounds")) + ".");
        }

        if (result.Reaction is { } reaction)
        {
            lines.Add(
                $"Reaction: {reaction.Name} ({reaction.Attack}) on {Percent(reaction.TriggerProbability)} of rounds, {Dpr(reaction.DamageWhenTaken)} " +
                $"damage when taken, {Dpr(reaction.DamagePerRound)} a round.");
        }

        if (result.Horizon == DprHorizons.Fight && result.DamageByRound.Count > 1)
        {
            lines.Add("By round: " + string.Join(" · ", result.DamageByRound.Select((d, i) => $"{Number(i + 1)}: {Dpr(d)}")) + ".");
        }

        if (lines.Count > 0)
        {
            blocks.Add(Bullets(lines));
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    private static string UseText(AttackReport attack) => attack.Use switch
    {
        AttackUses.AttackAction => "Attack action",
        AttackUses.BonusAction => "Bonus Action",
        AttackUses.ActionSurge => $"second Attack action ({attack.Source ?? "Action Surge"})",
        AttackUses.BonusActionExtra => $"Bonus Action ({attack.Source ?? "extra attack"})",
        AttackUses.Cleave => "Cleave, second creature",
        AttackUses.Reaction => $"Reaction ({attack.Source ?? "reaction"})",
        _ => attack.Use,
    };

    private static string RiderWhen(RiderReport rider)
    {
        var parts = new List<string> { rider.When is { } when ? WhenText(when) : "once per turn, a weapon hit" };
        if (rider.Policy is { } policy)
        {
            parts.Add(PolicyText(policy, rider.UseValue ?? 0));
        }

        if (rider.Resource is { } resource)
        {
            parts.Add(ResourceText(resource));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The round-1 turn's exact damage distribution: percentiles, P(no damage), P(≥ the target's HP). Null when the
    /// evaluation carries none (only the detail level does).
    /// </summary>
    public static string? Distribution(DamageDistribution? distribution, ResolvedTarget target)
    {
        if (distribution is null)
        {
            return null;
        }

        var q = distribution.Quantiles;
        var text = new StringBuilder(
            $"**Round 1 damage** (exact distribution): p10 {Number(q[0])} · p25 {Number(q[1])} · p50 {Number(q[2])} · " +
            $"p75 {Number(q[3])} · p90 {Number(q[4])}; P(no damage) {Percent(distribution.ZeroChance)}; range " +
            $"{Number(distribution.Min)}–{Number(distribution.Max)}");
        if (distribution.AtLeastHitPointsChance is { } kill)
        {
            text.Append($"; P(≥ {Number(target.HitPoints!.Value)}, the target's hit points) {Percent(kill)}");
        }
        else if (target.HitPoints is not null)
        {
            text.Append("; P(≥ the target's hit points) is not given, since the turn's damage is spread over several creatures (see the save effects)");
        }

        return text.Append('.').ToString();
    }

    /// <summary>Each power attack's decision: round 1's choice per advantage sample, how often it was on, and the rule of thumb per attack.</summary>
    public static string? PowerAttacks(DprResult result)
    {
        if (result.PowerAttacks.Count == 0)
        {
            return null;
        }

        var lines = result.PowerAttacks.Select(p =>
        {
            var round1 = p.Round1Choices.Count == 1
                ? $"{(p.Round1Choices[0].On ? "on" : "off")} in round 1"
                : "in round 1 " + string.Join(", ", p.Round1Choices.Select(c =>
                    $"{(c.On ? "on" : "off")} {(c.SourcesPresent.Count == 0 ? "with no rate source present" : "with " + string.Join(" and ", c.SourcesPresent))} ({Percent(c.Probability)})"));
            var toggles = p.Toggles.Select(t =>
                $"{t.Attack}: P {Percent(t.HitChance)} → P′ {Percent(t.HitChanceWithPenalty)}, D {Dpr(t.DamageOnHit)}, so P′/P " +
                $"{(t.HitChance > 0 ? (t.HitChanceWithPenalty / t.HitChance).ToString("0.###", Invariant) : "—")} vs D/(D + {Number(p.Bonus)}) " +
                $"{t.Threshold.ToString("0.###", Invariant)}: {(t.RuleSaysOn ? "on" : "off")}");
            return $"{p.Name} (−{Number(p.Penalty)}/+{Number(p.Bonus)}, {p.Policy}): {round1}; on in {Percent(p.OnChancePerRound)} of rounds. " +
                   $"Rule of thumb per attack, on iff P′/P > D/(D + bonus): {string.Join("; ", toggles)}.";
        });
        return "**Power attack:**\n" + Bullets(lines);
    }

    /// <summary>
    /// Each save effect per cast and per round, and with the target's HP the kill figures over the SHARED damage roll
    /// (every target takes the same roll, saves independently).
    /// </summary>
    public static string? SaveEffects(DprResult result)
    {
        if (result.SaveEffects.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var effect in result.SaveEffects)
        {
            var text = new StringBuilder(
                $"**{effect.Name}** ({V.Abilities.Display(effect.Ability)} save, DC {Number(effect.Dc)}; {Plural(effect.Targets, "target")}): " +
                $"P(a target fails) {Percent(effect.FailChance)}; {Dpr(effect.DamagePerTarget)} damage per target, {Dpr(effect.RawDamage)} " +
                $"in all per cast; cast {Times(effect.CastsPerRound)} a round, {Dpr(effect.DamagePerRound)} damage a round.");
            if (effect.EffectiveDamage is { } effective && result.Target.HitPoints is { } hp)
            {
                text.Append(
                    $" With {Number(hp)} hit points each (overkill removed; one damage roll shared by every target, saves independent): " +
                    $"effective {Dpr(effective)} per cast; P(each dies) {Percent(effect.KillChanceEach ?? 0)}; " +
                    $"P({(effect.Targets == 1 ? "it dies" : $"all {Number(effect.Targets)} die")}) {Percent(effect.AllDieChance ?? 0)}; " +
                    $"expected kills {Dpr(effect.ExpectedKills ?? 0)}");
                if (effect.KillDistribution is { Count: > 2 } kills)
                {
                    text.Append($"; P(exactly k die), k = 0–{Number(kills.Count - 1)}: {string.Join(" · ", kills.Select(Percent))}");
                }

                text.Append('.');
            }

            if (effect.Condition is { } condition)
            {
                var parts = new List<string>();
                if (effect.LandChancePerTurn is { } perTurn)
                {
                    parts.Add($"lands on a given target in a turn {Percent(perTurn)}");
                }

                if (effect.LandChancePerFight is { } perFight)
                {
                    parts.Add($"at least once in the fight {Percent(perFight)}");
                }

                if (effect.ExpectedCastsToLand is { } casts)
                {
                    parts.Add(double.IsPositiveInfinity(casts)
                        ? "it never lands (no chance to fail)"
                        : $"expected casts to land it past {Plural(result.Target.LegendaryResistance, "Legendary Resistance")}: {casts.ToString("0.##", Invariant)}");
                }

                if (parts.Count > 0)
                {
                    text.Append($" {char.ToUpperInvariant(condition[0])}{condition[1..]}: {string.Join("; ", parts)}.");
                }
            }

            lines.Add(text.ToString());
        }

        return "**Save effects:**\n" + Bullets(lines);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The day
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The day horizon's arithmetic: the base fight without limited features, then what each feature adds.</summary>
    public static string Day(DayResult day, string heading)
    {
        var assumptions = day.Assumptions;
        var intro =
            $"The day assumed is {assumptions.Label} (E = {DayText(assumptions.EncountersPerDay)} encounters of R = " +
            $"{Number(day.Rounds)} rounds, E × R = {day.RoundsPerDay.ToString("0.##", Invariant)} rounds; S = " +
            $"{Number(assumptions.ShortRests)} short rest{(assumptions.ShortRests == 1 ? "" : "s")}). Without its resource-limited " +
            $"features the build deals {Dpr(day.Base.DamagePerRound)} a round.";
        var blocks = new List<string?> { heading, intro };
        if (day.Features.Count > 0)
        {
            blocks.Add(SrdMarkdownText.Table(
                ["Feature", "Uses per day U", "Uses per round u", "Damage per use d", "Uses wanted u × E × R", "Adds"],
                day.Features.Select(f => (IReadOnlyList<string>)
                [
                    f.Source.Label,
                    $"{Number(f.UsesPerDay)} ({ResourceText(f.Resource)})",
                    Rate(f.UsesPerRound),
                    f.DamagePerUse is { } d ? Dpr(d) : "never used",
                    f.UsesWantedPerDay.ToString("0.##", Invariant),
                    SignedDpr(f.Contribution),
                ])));
            blocks.Add(
                $"Day: {Dpr(day.Base.DamagePerRound)} {string.Join(" ", day.Features.Select(f => SignedDpr(f.Contribution)))} = " +
                $"**{Dpr(day.DamagePerRound)}** a round, each feature adding min(U, u × E × R) × d ÷ (E × R).");
        }

        // The day's own notes stay with its arithmetic: in a comparison the two builds' days differ, and "no resource-limited
        // features" in a shared list would be read about the wrong build.
        if (day.Notes.Count > 0)
        {
            blocks.Add(Bullets(day.Notes));
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    private static string DayText(double encounters) => encounters.ToString("0.##", Invariant);

    // ---------------------------------------------------------------------------------------------------------------
    // Assumptions and sources
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>"Round 1 (the nova)", "A 3-round fight (the mean per round)", "An adventuring day (…)".</summary>
    public static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The three horizons' labels, in the order results list them.</summary>
    public static IReadOnlyList<(string Horizon, string Label)> HorizonLabels(HorizonSettings settings) =>
    [
        (DprHorizons.Round1, "Round 1 (the nova)"),
        (DprHorizons.Fight, Capitalized(settings.FightLabel)),
        (DprHorizons.Day, Capitalized(settings.DayLabel)),
    ];

    /// <summary>What each horizon means, and which one the headline is.</summary>
    public static string HorizonAssumption(HorizonSettings horizon) =>
        $"Horizons: round 1 is a fight's first turn from fresh resources, setup costs paid; the fight is " +
        $"{Number(horizon.Rounds)} rounds from fresh resources, averaged per round; the day is {horizon.DayLabel}, each " +
        $"limited feature's uses spread evenly over its rounds. The headline is the {HorizonName(horizon.Horizon)}.";

    private static string HorizonName(string horizon) => horizon switch
    {
        DprHorizons.Round1 => "round 1 figure",
        DprHorizons.Fight => "fight's",
        _ => "day's",
    };

    /// <summary>
    /// The table rulings the evaluations reported as able to change the numbers, each with its value, or a line saying
    /// none can. Only rulings that matter are listed: echoing all four would bury the one that changed the answer.
    /// </summary>
    public static string Rulings(IEnumerable<RulingUsed> rulings)
    {
        var used = rulings.DistinctBy(r => (r.Name, r.Value)).ToList();
        return used.Count == 0
            ? "Rulings: none of the four table rulings (hew_gets_pb, cleave_part_of_attack_action, gwf_on_riders, savage_attacker_on_crit_dice) changes this build."
            : "Rulings: " + string.Join("; ", used.Select(r => $"{r.Name} = {(r.Value ? "true" : "false")} (true means: {r.Meaning.TrimEnd('.')})")) + ".";
    }

    /// <summary>
    /// The spending policy of every optional rider, reroll, condition and power attack, with what each policy used means,
    /// or null when there is none.
    /// </summary>
    public static string? Policies(ResolvedBuild build)
    {
        var used = build.Riders.Where(r => r.IsOptional).Select(r => (r.Source, r.Policy, UseValue: (double?)r.UseValue))
            .Concat(build.DamageRerolls.Select(r => (r.Source, r.Policy, UseValue: (double?)r.UseValue)))
            .Concat(build.ConditionsOnHit.Select(c => (c.Source, c.Policy, UseValue: (double?)c.UseValue)))
            .Concat(build.PowerAttacks.Select(p => (p.Source, p.Policy, UseValue: (double?)null)))
            .OrderBy(p => p.Source.Number)
            .ToList();
        if (used.Count == 0)
        {
            return null;
        }

        var items = used.Select(p => p.Policy == V.Policies.Optimal ? $"{p.Source.Label} optimal (use_value {Rate(p.UseValue ?? 0)})" : $"{p.Source.Label} {p.Policy}");
        var meanings = used.Select(p => p.Policy).Distinct().Select(p => $"{p}: {PolicyMeaning(p)}");
        return $"Policies ({build.Name}): {string.Join("; ", items)} ({string.Join("; ", meanings)}).";
    }

    private static string PolicyMeaning(string policy) => policy switch
    {
        V.Policies.AnyHit => "spent on a hit whenever allowed",
        V.Policies.CritsOnly => "spent only on a crit",
        V.Policies.CritOrLast => "spent on a crit, or on a normal hit when it is the turn's last chance",
        V.Policies.Optimal => "spent where it adds the most expected damage net of use_value per use (exact backward induction)",
        V.PowerAttackPolicies.Auto => "on for a turn whenever it adds expected damage",
        V.PowerAttackPolicies.Always => "always on",
        V.PowerAttackPolicies.Never => "never on",
        _ => policy,
    };

    /// <summary>
    /// The sources behind what the result used: the DMG row (a default target), the typical save bonus, the area table,
    /// the reference curves. None of them is SRD text, and the model must be able to say whose convention a number is.
    /// </summary>
    public static string Sources(IEnumerable<ResolvedTarget> targets, IEnumerable<ResolvedBuild> builds, bool references)
    {
        var targetList = targets.ToList();
        var sources = new List<string>();
        if (targetList.Any(t => t.Row is not null))
        {
            sources.Add($"the target's CR row (the CR = level convention unless cr was given): {DmgRowSource}");
        }

        if (targetList.Any(t => t.SaveBonusSource.Contains(TypicalSaveBonus.Source, StringComparison.Ordinal)))
        {
            sources.Add($"typical save bonus: The Finished Book's \"Baseline Monster Stats\" ({TypicalSaveBonus.SourceUrl}; not a DMG table)");
        }

        if (builds.Any(b => b.SaveEffects.Any(s => s.TargetsFromArea)))
        {
            sources.Add($"creatures in an area: {AreaSource}");
        }

        if (references)
        {
            sources.Add("reference curves: RPGBOT's DPR target and the community Warlock Baseline (Form of Dread), community conventions, not rules");
        }

        var tables =
            $"The tables: rules_get ref \"{RulesTables.UriPrefix}{BalanceRulesTables.TargetsSlug}\", " +
            $"\"{RulesTables.UriPrefix}{BalanceRulesTables.GwfSlug}\", \"{RulesTables.UriPrefix}{BalanceRulesTables.AreaSlug}\".";
        return sources.Count == 0 ? $"*{tables}*" : $"*Sources: {string.Join("; ", sources)}. {tables}*";
    }

    /// <summary>Notes with duplicates removed, as a block, or null when there are none.</summary>
    public static string? Notes(IEnumerable<string> notes)
    {
        var distinct = notes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : "**Notes:**\n" + Bullets(distinct);
    }
}
