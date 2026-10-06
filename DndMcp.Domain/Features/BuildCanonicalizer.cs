using System.Text.Json;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// A <see cref="BuildSpec"/> with every vocabulary word in its canonical spelling and every quoted whole number a number:
/// what a stored sim_profile holds, so the sheet shows, exports (Phase 8) and compares one spelling however the model
/// typed it ("Two Handed", "GWF", "First-Hit-Per-Turn" → "two-handed", "gwf", "first_hit_per_turn"; <c>"count": "2"</c>
/// → <c>"count": 2</c>).
///
/// <para>
/// <b>The same sets the compiler uses, per field and per kind</b> (<see cref="CompiledBuild"/>,
/// <see cref="CompiledAttack"/>, <see cref="CompiledModifier"/>): a field whose vocabulary depends on the modifier's
/// kind (when, policy, action_cost, condition) is matched against that kind's set, because one word can be valid in one
/// set and not another. Attack references (<c>modifiers[].attacks</c>, <c>extra_attack.attack</c>) become the attack's own
/// name as written there (trimmed), which is how the compiler resolves them (case ignored). An <c>amount</c> written as a
/// word becomes <c>"pb"</c> or the ability key. Names are trimmed, as the compiler trims them.
/// </para>
/// <para>
/// <b>Quoted whole numbers become numbers</b> in the whole-number step values: an attack's <c>count</c>, a modifier's
/// <c>count</c>, <c>min</c> and <c>amount</c>, a resource's <c>uses</c> and the six ability scores, as a scalar or as a step
/// map's values (its keys, the levels, are untouched). The host binds numbers from strings, so a model that writes
/// <c>"count": "2"</c> gets the build that <c>"count": 2</c> gets, and the resolver reads both alike; stored as typed, the
/// two profiles would differ as text, which is what this class exists to prevent. Exactly the strings
/// <see cref="LevelValue.TryReadWholeNumber"/> reads as numbers are converted (<c>" 2 "</c>, <c>"+2"</c>, <c>"-1"</c>);
/// anything it refuses (<c>"1.5"</c>, <c>"1e2"</c>, a word) is kept, and damage formulas (<c>damage</c>, <c>dice</c>) are
/// text and stay as typed.
/// </para>
/// <para>
/// <b>Semantics preserved, idempotent, never throws.</b> Resolving the canonical build gives exactly what resolving the
/// original gives (a property test runs every archetype at every level in both editions and the Phase 4/5 test builds),
/// and <c>Canonical(Canonical(b))</c> serialises as <c>Canonical(b)</c>. A value that matches no set is left as typed:
/// canonicalising is not validating, and the validator's message should quote what the model sent.
/// </para>
/// </summary>
public static class BuildCanonicalizer
{
    /// <summary>The build with every vocabulary word canonical (see the class summary). The input is not changed.</summary>
    public static BuildSpec Canonical(BuildSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var attacks = spec.Attacks?.Select(a => a is null ? null! : Attack(a)).ToList();
        return spec with
        {
            Name = spec.Name?.Trim(),
            Preset = Match(V.Presets.Set, spec.Preset),
            Edition = Match(V.Editions.Set, spec.Edition),
            Abilities = spec.Abilities is null ? null : Abilities(spec.Abilities),
            FightingStyle = Match(V.FightingStyles.Set, spec.FightingStyle),
            Attacks = attacks,
            Modifiers = spec.Modifiers?.Select(m => m is null ? null! : Modifier(m, spec.Attacks ?? [])).ToList(),
        };
    }

    private static AbilitiesSpec Abilities(AbilitiesSpec a) => new()
    {
        Str = WholeNumbers(a.Str),
        Dex = WholeNumbers(a.Dex),
        Con = WholeNumbers(a.Con),
        Int = WholeNumbers(a.Int),
        Wis = WholeNumbers(a.Wis),
        Cha = WholeNumbers(a.Cha),
    };

    private static AttackSpec Attack(AttackSpec a) => new()
    {
        Name = a.Name?.Trim(),
        Count = WholeNumbers(a.Count),
        Action = Match(V.AttackActions.Set, a.Action),
        ToHit = a.ToHit is null
            ? null
            : new ToHitSpec { Ability = Match(V.Abilities.ToHitSet, a.ToHit.Ability), Proficient = a.ToHit.Proficient, Bonus = a.ToHit.Bonus, Total = a.ToHit.Total },
        Damage = a.Damage,
        DamageType = Match(V.DamageTypes.Set, a.DamageType),
        AbilityToDamage = a.AbilityToDamage,
        Properties = a.Properties is null ? null : Distinct(a.Properties.Select(p => Match(V.Properties.Set, p)!)),
        Offhand = a.Offhand,
        Mastery = Match(V.Masteries.Set, a.Mastery),
        Cantrip = Match(V.Cantrips.Set, a.Cantrip),
        FromLevel = a.FromLevel,
        UntilLevel = a.UntilLevel,
    };

    private static ModifierSpec Modifier(ModifierSpec m, IReadOnlyList<AttackSpec> attacks)
    {
        var kind = Match(V.Kinds.Set, m.Kind);
        return new ModifierSpec
        {
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(m.Name) ? m.Name : m.Name.Trim(),
            Attacks = m.Attacks is null ? null : Distinct(m.Attacks.Select(name => AttackName(name, attacks))),
            FromLevel = m.FromLevel,
            UntilLevel = m.UntilLevel,
            Resource = m.Resource is null ? null : new ResourceSpec { Uses = WholeNumbers(m.Resource.Uses), Per = Match(V.Rests.Set, m.Resource.Per) },
            Concentration = m.Concentration,
            Setup = Match(V.Setup.Set, m.Setup),
            Amount = WholeNumbers(Amount(m.Amount)),
            Dice = m.Dice,
            Type = Match(V.DamageTypes.Set, m.Type),
            When = Match(kind == V.Kinds.ConditionOnHit ? V.When.ConditionOnHitSet : V.When.ExtraDamageSet, m.When),
            Policy = Match(kind == V.Kinds.PowerAttack ? V.PowerAttackPolicies.Set : V.Policies.Set, m.Policy),
            UseValue = m.UseValue,
            CritDoubles = m.CritDoubles,
            AttackActionOnly = m.AttackActionOnly,
            ActionCost = Match(
                kind switch
                {
                    V.Kinds.SaveEffect => V.ActionCosts.SaveEffectSet,
                    V.Kinds.Heal => V.ActionCosts.HealSet,
                    _ => V.ActionCosts.RiderSet,
                },
                m.ActionCost),
            Min = WholeNumbers(m.Min),
            Mode = Match(V.AdvantageModes.Set, m.Mode),
            Rate = m.Rate,
            Remap = Match(V.Remaps.Set, m.Remap),
            Attack = m.Attack is null ? null : AttackName(m.Attack, attacks),
            Count = WholeNumbers(m.Count),
            Action = Match(V.ExtraAttackActions.Set, m.Action),
            Trigger = Match(V.Triggers.Set, m.Trigger),
            TriggerProbability = m.TriggerProbability,
            Penalty = m.Penalty,
            Bonus = m.Bonus,
            Ability = Match(V.Abilities.Set, m.Ability),
            Dc = m.Dc,
            DcAbility = Match(V.Abilities.Set, m.DcAbility),
            DcBonus = m.DcBonus,
            OnSuccess = Match(V.OnSuccess.Set, m.OnSuccess),
            Targets = m.Targets,
            Shape = Match(V.Shapes.Set, m.Shape),
            Size = m.Size,
            Magical = m.Magical,
            Condition = Match(kind == V.Kinds.SaveEffect ? V.Conditions.SaveEffectSet : V.Conditions.OnHitSet, m.Condition),
            Cantrip = m.Cantrip,
            Duration = Match(V.Durations.Set, m.Duration),
            SelfOnly = m.SelfOnly,
        };
    }

    /// <summary>The canonical value, or the text as typed when it matches nothing (null stays null).</summary>
    private static string? Match(DslValueSet set, string? text) =>
        text is null ? null : set.TryMatch(text, out var canonical) ? canonical : text;

    /// <summary>
    /// The referenced attack's own name, trimmed (the compiler matches the first attack whose trimmed name equals the
    /// trimmed reference, case ignored), or the reference as typed when no attack has that name.
    /// </summary>
    private static string AttackName(string reference, IReadOnlyList<AttackSpec> attacks)
    {
        var wanted = reference?.Trim();
        var match = attacks.FirstOrDefault(a => a?.Name is not null && string.Equals(a.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        return match?.Name?.Trim() ?? reference!;
    }

    /// <summary>
    /// A scalar amount written as a word: "PB" / "proficiency bonus" → "pb", "Charisma" → "cha" (what
    /// <see cref="LevelValue.ParseAmount"/> reads). Numbers, number strings, step maps and unknown words are kept here;
    /// <see cref="WholeNumbers"/> then turns the number strings into numbers.
    /// </summary>
    private static object? Amount(object? raw)
    {
        if (LevelValue.ToElement(raw) is not { ValueKind: JsonValueKind.String } element)
        {
            return raw;
        }

        var text = element.GetString() ?? string.Empty;
        var key = DslValueSet.Key(text);
        string? canonical = key is DslAmount.ProficiencyBonusKeyword or "proficiencybonus"
            ? DslAmount.ProficiencyBonusKeyword
            : V.Abilities.Set.TryMatch(text, out var ability) ? ability : null;
        return canonical is null || canonical == text ? raw : JsonSerializer.SerializeToElement(canonical, DslJson.Options);
    }

    /// <summary>
    /// A whole-number step value with each quoted whole number (as <see cref="LevelValue.TryReadWholeNumber"/> reads it) a
    /// JSON number: <c>"2"</c> → <c>2</c>, <c>{"1": "1", "5": 2}</c> → <c>{"1": 1, "5": 2}</c> (keys, the levels, as typed).
    /// Anything else, numbers included, is returned as given, so a canonical build canonicalises to itself.
    /// </summary>
    private static object? WholeNumbers(object? raw)
    {
        if (LevelValue.ToElement(raw) is not { } element)
        {
            return raw;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return LevelValue.TryReadWholeNumber(element, out var number) ? JsonSerializer.SerializeToElement(number, DslJson.Options) : raw;
        }

        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Any(step => step.Value.ValueKind == JsonValueKind.String && LevelValue.TryReadWholeNumber(step.Value, out _)))
        {
            return raw;
        }

        // Every step in its order, duplicates included (the validator refuses those, and canonicalising never throws).
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var step in element.EnumerateObject())
            {
                if (step.Value.ValueKind == JsonValueKind.String && LevelValue.TryReadWholeNumber(step.Value, out var number))
                {
                    writer.WritePropertyName(step.Name);
                    writer.WriteNumberValue(number);
                }
                else
                {
                    step.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static List<string> Distinct(IEnumerable<string> values)
    {
        var list = new List<string>();
        foreach (var value in values)
        {
            if (!list.Contains(value, StringComparer.Ordinal))
            {
                list.Add(value);
            }
        }

        return list;
    }
}
