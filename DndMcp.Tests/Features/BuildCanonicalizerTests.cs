using System.Reflection;
using System.Text.Json;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation.Archetypes;
using DndMcp.Tests.Dpr;
using DndMcp.Tests.Simulation;
using Xunit;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: <see cref="BuildCanonicalizer.Canonical"/> only respells. Over every archetype build at every level in both
/// editions, every oracle case's build, the Phase 4/5 golden and fixture builds and deliberately messy spellings,
/// resolving the canonical build gives exactly what resolving the original gives, and canonicalising twice changes
/// nothing; each vocabulary word lands on the spelling the compiler would use for that field and that modifier kind, and
/// each quoted whole number in a whole-number step value is stored as the number, so a quoted and an unquoted profile
/// store the same text.
/// </summary>
public sealed class BuildCanonicalizerTests
{
    /// <summary>A build with every vocabulary field spelt as a model might: cases, spaces, hyphens, aliases, padding.</summary>
    private const string Messy = """
        { "name": "  Messy L11 ", "edition": " 2014 ", "level": 11, "fighting_style": "Great Weapon Fighting",
          "abilities": { "str": 18, "dex": "14", "cha": { "1": 16, "8": 18 } },
          "attacks": [
            { "name": " Greatsword ", "count": { "1": 1, "5": 2 }, "action": "Action", "to_hit": { "ability": "Strength" },
              "damage": "2d6", "damage_type": "Slashing", "properties": ["Melee", "HEAVY", "Two Handed", "two_handed"], "mastery": "Graze" },
            { "name": "Javelin", "action": "bonus-action", "to_hit": { "ability": "STR", "bonus": 1 }, "damage": "1d6",
              "damage_type": "piercing", "properties": ["ranged", "Thrown"] },
            { "name": "Fire Bolt", "to_hit": { "ability": "Charisma" }, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false,
              "properties": ["Ranged", "Spell"], "cantrip": "Dice" } ],
          "modifiers": [
            { "kind": "Extra Damage", "name": " Hex ", "dice": "1d6", "type": "Necrotic", "when": "First-Hit-Per-Turn",
              "policy": "Crit Or Last", "concentration": true, "attacks": ["greatsword", " GREATSWORD", "Javelin"] },
            { "kind": "Power-Attack", "name": "GWM", "policy": "ALWAYS", "attacks": ["Greatsword"] },
            { "kind": "extra attack", "name": "Hew", "attack": "greatsword ", "action": "Bonus Action", "trigger": "Crit-Or-Kill" },
            { "kind": "bonus_damage", "name": "Agonizing", "amount": "Charisma", "attacks": ["fire bolt"] },
            { "kind": "bonus damage", "name": "GWM 2024", "amount": "Proficiency Bonus", "attack_action_only": true },
            { "kind": "Condition On Hit", "name": "Topple", "condition": "Prone", "ability": "Constitution", "dc_ability": "Strength",
              "when": "first hit per turn", "duration": "End Of Next Turn" },
            { "kind": "Save Effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "Sphere",
              "size": 20, "on_success": "None", "action_cost": "Bonus Action", "condition": "Frightened", "duration": "Save Ends",
              "resource": { "uses": 2, "per": "Long Rest" } },
            { "kind": "Damage Die Remap", "remap": "Elemental Adept", "type": "Fire" },
            { "kind": "Advantage", "mode": "Disadvantage", "rate": 0.5 },
            { "kind": "heal", "name": "Second Wind", "dice": "1d10", "amount": 11, "action_cost": "bonus-action", "self_only": true,
              "resource": { "uses": 1, "per": "short rest" } },
            { "kind": "AC", "name": "Shield", "amount": "pb", "setup": "Bonus-Action" } ] }
        """;

    public static TheoryData<string, string, int> Archetypes()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var name in ArchetypeCatalog.Names)
        {
            foreach (var edition in new[] { DslValues.Editions.E2014, DslValues.Editions.E2024 })
            {
                for (var level = 1; level <= 20; level++)
                {
                    data.Add(name, edition, level);
                }
            }
        }

        return data;
    }

    public static TheoryData<string> OracleCaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in OracleCases.All)
        {
            data.Add(OracleCases.Id(c));
        }

        return data;
    }

    public static TheoryData<string> PhaseFourAndFiveBuilds() => new()
    {
        FeatureTestBuilds.EmberEdgeJson,
        FeatureTestBuilds.Fighter2014GwmJson,
        FeatureTestBuilds.Fighter2024GwmJson,
        FeatureTestBuilds.PaladinSmiteJson,
        FeatureTestBuilds.FireballJson,
        GoldenBuilds.Fighter2014(GoldenBuilds.Gwm2014 + ", " + GoldenBuilds.ActionSurge + ", " + GoldenBuilds.SavageAttacker),
        GoldenBuilds.Fighter2024(GoldenBuilds.Gwm2024 + ", " + GoldenBuilds.ActionSurge),
        SimKit.Fighter2024,
        SimKit.Commoner,
        """{ "name": "Warlock", "preset": "Warlock-Baseline", "edition": "2014", "level": 11 }""",
        Messy,
    };

    [Theory]
    [MemberData(nameof(Archetypes))]
    public void Canonical_EveryArchetypeAtEveryLevel_ResolvesTheSameAndIsIdempotent(string name, string edition, int level)
    {
        AssertPreserved(ArchetypeCatalog.Build(name, level, edition).Build, [level], BuildUse.Simulation);
    }

    [Theory]
    [MemberData(nameof(OracleCaseIds))]
    public void Canonical_EveryOracleCaseBuild_ResolvesTheSameAndIsIdempotent(string id)
    {
        var spec = DslJson.Deserialize<BuildSpec>(OracleCases.Case(id).GetProperty("build").GetRawText(), "build");

        AssertPreserved(spec, [spec.Level!.Value], BuildUse.Dpr);
    }

    [Theory]
    [MemberData(nameof(PhaseFourAndFiveBuilds))]
    public void Canonical_TestBuilds_ResolveTheSameAtEveryLevelAndAreIdempotent(string json)
    {
        AssertPreserved(DslJson.Deserialize<BuildSpec>(json, "build"), Enumerable.Range(1, 20).ToList(), BuildUse.Simulation);
    }

    [Fact]
    public void Canonical_MessyBuild_UsesTheCompilersSpellingPerFieldAndKind()
    {
        var spec = BuildCanonicalizer.Canonical(DslJson.Deserialize<BuildSpec>(Messy, "build"));

        Assert.Equal("Messy L11", spec.Name);
        Assert.Equal("2014", spec.Edition);
        Assert.Equal("gwf", spec.FightingStyle);
        var sword = spec.Attacks![0];
        Assert.Equal("Greatsword", sword.Name);
        Assert.Equal("action", sword.Action);
        Assert.Equal("str", sword.ToHit!.Ability);
        Assert.Equal("slashing", sword.DamageType);
        Assert.Equal(["melee", "heavy", "two-handed"], sword.Properties);
        Assert.Equal("graze", sword.Mastery);
        Assert.Equal("bonus_action", spec.Attacks[1].Action);
        Assert.Equal(1, spec.Attacks[1].ToHit!.Bonus);
        Assert.Equal("dice", spec.Attacks[2].Cantrip);

        var m = spec.Modifiers!;
        Assert.Equal(("extra_damage", "Hex", "necrotic", "first_hit_per_turn", "crit_or_last"), (m[0].Kind, m[0].Name, m[0].Type, m[0].When, m[0].Policy));
        Assert.Equal(["Greatsword", "Javelin"], m[0].Attacks);
        Assert.Equal(("power_attack", "always"), (m[1].Kind, m[1].Policy));
        Assert.Equal(("extra_attack", "Greatsword", "bonus_action", "crit_or_kill"), (m[2].Kind, m[2].Attack, m[2].Action, m[2].Trigger));
        Assert.Equal("cha", ((JsonElement)m[3].Amount!).GetString());
        Assert.Equal(["Fire Bolt"], m[3].Attacks);
        Assert.Equal("pb", ((JsonElement)m[4].Amount!).GetString());
        Assert.Equal(("condition_on_hit", "prone", "con", "str", "first_hit_per_turn", "end_of_next_turn"), (m[5].Kind, m[5].Condition, m[5].Ability, m[5].DcAbility, m[5].When, m[5].Duration));
        Assert.Equal(("save_effect", "sphere", "none", "bonus_action", "frightened", "save_ends", "long_rest"), (m[6].Kind, m[6].Shape, m[6].OnSuccess, m[6].ActionCost, m[6].Condition, m[6].Duration, m[6].Resource!.Per));
        Assert.Equal(("damage_die_remap", "elemental_adept", "fire"), (m[7].Kind, m[7].Remap, m[7].Type));
        Assert.Equal(("advantage", "disadvantage"), (m[8].Kind, m[8].Mode));
        Assert.Equal(("heal", "bonus_action", "short_rest"), (m[9].Kind, m[9].ActionCost, m[9].Resource!.Per));
        Assert.Equal(("ac", "bonus_action"), (m[10].Kind, m[10].Setup));
    }

    [Fact]
    public void Canonical_MessyBuild_StoresAsCanonicalText()
    {
        var text = JsonSerializer.Serialize(BuildCanonicalizer.Canonical(DslJson.Deserialize<BuildSpec>(Messy, "build")), DslJson.Options);

        Assert.DoesNotContain("Two Handed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("First-Hit", text, StringComparison.Ordinal);
        Assert.Contains("\"dex\":14", text, StringComparison.Ordinal);
        Assert.Contains("\"cha\":{\"1\":16,\"8\":18}", text, StringComparison.Ordinal);
        Assert.Contains("\"damage\":\"2d6\"", text, StringComparison.Ordinal);
    }

    // One row per whole-number step value: each field as a quoted scalar (with padding and a sign where the reader takes
    // them) and as a step map of quoted values, next to the same build with numbers. Damage formulas are not among them.
    [Theory]
    // An attack's count: quoted, padded, and a step map of quoted and unquoted values.
    [InlineData("""{ "name": "Sword", "count": "2", "damage": "1d8" }""", """{ "name": "Sword", "count": 2, "damage": "1d8" }""", null)]
    [InlineData("""{ "name": "Sword", "count": " 2 ", "damage": "1d8" }""", """{ "name": "Sword", "count": 2, "damage": "1d8" }""", null)]
    [InlineData("""{ "name": "Sword", "count": { "1": "1", "5": 2, "11": "3" }, "damage": "1d8" }""",
        """{ "name": "Sword", "count": { "1": 1, "5": 2, "11": 3 }, "damage": "1d8" }""", null)]
    // A modifier's count, a resource's uses, a crit_range's min and an amount (signed, and as a step map).
    [InlineData(null, null, """{ "kind": "extra_attack", "attack": "Sword", "action": "bonus_action", "count": "1" }""")]
    [InlineData(null, null, """{ "kind": "extra_attack", "attack": "Sword", "action": "bonus_action", "count": { "1": "1", "11": "2" } }""")]
    [InlineData(null, null, """{ "kind": "extra_damage", "name": "Smite", "dice": "2d8", "resource": { "uses": "3", "per": "long_rest" } }""")]
    [InlineData(null, null, """{ "kind": "extra_damage", "name": "Smite", "dice": "2d8", "resource": { "uses": { "1": "1", "17": "2" }, "per": "long_rest" } }""")]
    [InlineData(null, null, """{ "kind": "crit_range", "min": "19" }""")]
    [InlineData(null, null, """{ "kind": "crit_range", "min": { "1": "20", "15": "19" } }""")]
    [InlineData(null, null, """{ "kind": "to_hit", "name": "Bless-ish", "amount": "+1" }""")]
    [InlineData(null, null, """{ "kind": "bonus_damage", "name": "Rusty", "amount": "-1" }""")]
    [InlineData(null, null, """{ "kind": "bonus_damage", "name": "Dueling-ish", "amount": { "1": "2", "5": "3" } }""")]
    public void Canonical_QuotedWholeNumber_StoresTheTextTheUnquotedBuildStores(string? quotedAttack, string? unquotedAttack, string? quotedModifier)
    {
        var quoted = DslJson.Deserialize<BuildSpec>(QuotedBuild(quotedAttack, quotedModifier), "build");
        var unquoted = DslJson.Deserialize<BuildSpec>(QuotedBuild(unquotedAttack, Unquoted(quotedModifier)), "build");

        // The unquoted build is already stored as written (the row converts every quoted value), and the quoted one is not.
        Assert.Equal(JsonSerializer.Serialize(unquoted, DslJson.Options), Stored(unquoted));
        Assert.NotEqual(JsonSerializer.Serialize(quoted, DslJson.Options), Stored(quoted));
        Assert.Equal(Stored(unquoted), Stored(quoted));
        Assert.Equal(SimProfile.Prepare(unquoted, 12).Json, SimProfile.Prepare(quoted, 12).Json);
        AssertPreserved(quoted, Enumerable.Range(1, 20).ToList(), BuildUse.Simulation);
    }

    [Theory]
    [InlineData("str")]
    [InlineData("dex")]
    [InlineData("con")]
    [InlineData("int")]
    [InlineData("wis")]
    [InlineData("cha")]
    public void Canonical_QuotedAbilityScore_StoresTheTextTheUnquotedBuildStores(string ability)
    {
        // Each score as a padded quoted scalar, and as a step map with quoted values (in another build).
        foreach (var (quotedValue, unquotedValue) in new[] { ("\" 16 \"", "16"), ("""{ "1": "14", "4": 16, "8": "18" }""", """{ "1": 14, "4": 16, "8": 18 }""") })
        {
            var quoted = DslJson.Deserialize<BuildSpec>(AbilityBuild(ability, quotedValue), "build");
            var unquoted = DslJson.Deserialize<BuildSpec>(AbilityBuild(ability, unquotedValue), "build");

            Assert.Equal(Stored(unquoted), Stored(quoted));
            Assert.Contains($"\"{ability}\":{unquotedValue.Replace(" ", string.Empty, StringComparison.Ordinal)}", Stored(quoted), StringComparison.Ordinal);
            Assert.Equal(SimProfile.Prepare(unquoted, 12).Json, SimProfile.Prepare(quoted, 12).Json);
            AssertPreserved(quoted, Enumerable.Range(1, 20).ToList(), BuildUse.Simulation);
        }
    }

    [Fact]
    public void Canonical_TextThatIsNoWholeNumberAndDamageFormulas_AreKeptAsTyped()
    {
        // What the whole-number reader refuses stays as typed, so the validator's message quotes what the model sent; damage
        // and dice are formulas (text), even when one is a bare number.
        var spec = BuildCanonicalizer.Canonical(DslJson.Deserialize<BuildSpec>(
            """
            { "name": "K", "level": 5, "abilities": { "str": "16.0", "dex": "1e1", "con": "lots" },
              "attacks": [{ "name": "A", "count": "1.5", "damage": "5" }, { "name": "B", "count": { "1": "1.5", "5": "2" }, "damage": { "1": "3", "5": "1d6" } }],
              "modifiers": [{ "kind": "extra_damage", "dice": "2", "amount": "2.5", "resource": { "uses": "1e2", "per": "long_rest" } },
                            { "kind": "crit_range", "min": "nineteen" }, { "kind": "to_hit", "amount": "" }] }
            """,
            "build"));
        var text = JsonSerializer.Serialize(spec, DslJson.Options);

        Assert.Contains("\"abilities\":{\"str\":\"16.0\",\"dex\":\"1e1\",\"con\":\"lots\"}", text, StringComparison.Ordinal);
        Assert.Contains("{\"name\":\"A\",\"count\":\"1.5\",\"damage\":\"5\"}", text, StringComparison.Ordinal);
        Assert.Contains("\"count\":{\"1\":\"1.5\",\"5\":2}", text, StringComparison.Ordinal);
        Assert.Contains("\"damage\":{\"1\":\"3\",\"5\":\"1d6\"}", text, StringComparison.Ordinal);
        Assert.Contains("\"dice\":\"2\"", text, StringComparison.Ordinal);
        Assert.Contains("\"amount\":\"2.5\"", text, StringComparison.Ordinal);
        Assert.Contains("\"uses\":\"1e2\"", text, StringComparison.Ordinal);
        Assert.Contains("\"min\":\"nineteen\"", text, StringComparison.Ordinal);
        Assert.Contains("\"amount\":\"\"", text, StringComparison.Ordinal);
        Assert.Equal(text, JsonSerializer.Serialize(BuildCanonicalizer.Canonical(spec), DslJson.Options));
    }

    [Fact]
    public void Canonical_Preset_IsKeptAsAPresetInItsSpelling()
    {
        var spec = BuildCanonicalizer.Canonical(new BuildSpec { Name = "W", Preset = "Warlock Baseline", Level = 5 });

        Assert.Equal("warlock_baseline", spec.Preset);
        Assert.Null(spec.Attacks);
    }

    [Fact]
    public void Canonical_WordsThatMatchNothing_AreKeptAsTyped()
    {
        var spec = BuildCanonicalizer.Canonical(DslJson.Deserialize<BuildSpec>(
            """
            { "name": "X", "level": 5, "edition": "2020", "attacks": [{ "name": "A", "damage": "1d8", "damage_type": "Fire-ish", "properties": ["Spiky"] }],
              "modifiers": [{ "kind": "Smite", "attacks": ["B"], "amount": "lots", "when": "sometimes" }] }
            """,
            "build"));

        Assert.Equal("2020", spec.Edition);
        Assert.Equal("Fire-ish", spec.Attacks![0].DamageType);
        Assert.Equal(["Spiky"], spec.Attacks[0].Properties);
        Assert.Equal("Smite", spec.Modifiers![0].Kind);
        Assert.Equal(["B"], spec.Modifiers[0].Attacks);
        Assert.Equal("lots", ((JsonElement)spec.Modifiers[0].Amount!).GetString());
        Assert.Equal("sometimes", spec.Modifiers[0].When);
        Assert.Throws<Domain.Core.DndInputException>(() => BuildResolver.Validate(spec));
    }

    [Fact]
    public void Canonical_KindDecidesTheSet_SameWordDifferentField()
    {
        // "every hit" is an extra_damage and a condition_on_hit word; "crit" is only a trigger. "none" is a save_effect
        // action cost but not a heal's: kept as typed there, and the validator refuses it.
        var spec = BuildCanonicalizer.Canonical(new BuildSpec
        {
            Name = "K",
            Level = 1,
            Modifiers =
            [
                new ModifierSpec { Kind = "save effect", ActionCost = "None" },
                new ModifierSpec { Kind = "heal", ActionCost = "None" },
                new ModifierSpec { Kind = "condition_on_hit", When = "On Crit" },
                new ModifierSpec { Kind = "extra_damage", When = "On Crit" },
            ],
        });

        Assert.Equal("none", spec.Modifiers![0].ActionCost);
        Assert.Equal("None", spec.Modifiers[1].ActionCost);
        Assert.Equal("On Crit", spec.Modifiers[2].When);
        Assert.Equal("on_crit", spec.Modifiers[3].When);
    }

    [Fact]
    public void Canonical_NullItems_StayNullAndTheInputIsUnchanged()
    {
        var original = DslJson.Deserialize<BuildSpec>(Messy, "build");
        var before = JsonSerializer.Serialize(original, DslJson.Options);

        BuildCanonicalizer.Canonical(original);
        var withNulls = BuildCanonicalizer.Canonical(new BuildSpec { Name = "N", Level = 1, Attacks = [null!], Modifiers = [null!] });

        Assert.Equal(before, JsonSerializer.Serialize(original, DslJson.Options));
        Assert.Null(Assert.Single(withNulls.Attacks!));
        Assert.Null(Assert.Single(withNulls.Modifiers!));
    }

    [Fact]
    public void Canonical_EveryPropertyOfEverySpecType_IsCarriedOver()
    {
        // The canonicalizer rebuilds attacks, to-hit, modifiers and resources property by property: a property added to
        // the DSL later must not be dropped from stored profiles. Every property at every depth is set (to a word no
        // vocabulary matches, so it is kept as typed) and must come back unchanged.
        var spec = (BuildSpec)Filled(typeof(BuildSpec), "build");

        var canonical = BuildCanonicalizer.Canonical(spec);

        Assert.Equal(JsonSerializer.Serialize(spec, DslJson.Options), JsonSerializer.Serialize(canonical, DslJson.Options));
    }

    /// <summary>An instance of <paramref name="type"/> with every writable public property set to a non-default value.</summary>
    private static object Filled(Type type, string name)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string))
        {
            return "zz" + name;
        }

        if (t == typeof(int))
        {
            return 7;
        }

        if (t == typeof(double))
        {
            return 0.25;
        }

        if (t == typeof(bool))
        {
            return true;
        }

        if (t == typeof(object))
        {
            return JsonSerializer.SerializeToElement(3);
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var itemType = t.GetGenericArguments()[0];
            var array = Array.CreateInstance(itemType, 1);
            array.SetValue(Filled(itemType, name), 0);
            return array;
        }

        Assert.True(t.IsClass && t.GetConstructor(Type.EmptyTypes) is not null, $"No filler for {t.Name} ({name}): extend this test.");
        var instance = Activator.CreateInstance(t)!;
        var properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToList();
        foreach (var property in properties)
        {
            property.SetValue(instance, Filled(property.PropertyType, property.Name));
        }

        Assert.NotEmpty(properties);
        Assert.All(properties, p => Assert.NotNull(p.GetValue(instance)));
        return instance;
    }

    // A level-12 build around one attack (the Sword unless given) and at most one modifier.
    private static string QuotedBuild(string? attack, string? modifier) =>
        $$"""
        { "name": "Q", "edition": "2024", "level": 12, "abilities": { "str": 16 },
          "attacks": [{{attack ?? """{ "name": "Sword", "count": 2, "damage": "1d8" }"""}}],
          "modifiers": [{{modifier ?? string.Empty}}] }
        """;

    // The modifier with its quoted whole numbers written as numbers, by hand per row's spelling.
    private static string? Unquoted(string? modifier) => modifier?
        .Replace("\"count\": \"1\"", "\"count\": 1", StringComparison.Ordinal)
        .Replace("\"uses\": \"3\"", "\"uses\": 3", StringComparison.Ordinal)
        .Replace("\"min\": \"19\"", "\"min\": 19", StringComparison.Ordinal)
        .Replace("\"amount\": \"+1\"", "\"amount\": 1", StringComparison.Ordinal)
        .Replace("\"amount\": \"-1\"", "\"amount\": -1", StringComparison.Ordinal)
        .Replace("{ \"1\": \"1\", \"11\": \"2\" }", "{ \"1\": 1, \"11\": 2 }", StringComparison.Ordinal)
        .Replace("{ \"1\": \"1\", \"17\": \"2\" }", "{ \"1\": 1, \"17\": 2 }", StringComparison.Ordinal)
        .Replace("{ \"1\": \"20\", \"15\": \"19\" }", "{ \"1\": 20, \"15\": 19 }", StringComparison.Ordinal)
        .Replace("{ \"1\": \"2\", \"5\": \"3\" }", "{ \"1\": 2, \"5\": 3 }", StringComparison.Ordinal);

    private static string AbilityBuild(string ability, string value) =>
        $$"""
        { "name": "Q", "edition": "2024", "level": 12, "abilities": { "{{ability}}": {{value}} },
          "attacks": [{ "name": "Sword", "damage": "1d8", "to_hit": { "ability": "{{ability}}" } }] }
        """;

    private static string Stored(BuildSpec spec) => JsonSerializer.Serialize(BuildCanonicalizer.Canonical(spec), DslJson.Options);

    private static void AssertPreserved(BuildSpec original, IReadOnlyList<int> levels, BuildUse use)
    {
        var canonical = BuildCanonicalizer.Canonical(original);

        Assert.Equal(Resolve(original, levels, use), Resolve(canonical, levels, use));
        Assert.Equal(JsonSerializer.Serialize(canonical, DslJson.Options), JsonSerializer.Serialize(BuildCanonicalizer.Canonical(canonical), DslJson.Options));
        var stored = JsonSerializer.Serialize(canonical, DslJson.Options);
        Assert.Equal(stored, JsonSerializer.Serialize(DslJson.Deserialize<BuildSpec>(stored, "sim_profile"), DslJson.Options));
    }

    private static string Resolve(BuildSpec spec, IReadOnlyList<int> levels, BuildUse use) =>
        JsonSerializer.Serialize(BuildResolver.Resolve(spec, levels, use: use));
}
