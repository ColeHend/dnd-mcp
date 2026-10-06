using System.ComponentModel;

namespace DndMcp.Domain.Characters;

/// <summary>
/// The <c>sheet</c> argument of <c>campaign_character update</c>: the fields to set on a character's sheet. Every field is
/// optional; a field left out is left as it is (on a new sheet: its default, or derived, see <see cref="SheetUpdate"/>).
///
/// <para>
/// <b>These records ARE the tool's input schema</b>, as <c>BuildSpec</c> is for the balance tools: the MCP SDK builds the
/// schema the model reads from the properties and their <see cref="DescriptionAttribute"/>s and binds the model's JSON
/// straight into them (snake_case). So every property is a nullable <c>init</c> property, and the descriptions are short:
/// they count toward the tool's schema budget. Ranges, names and cross-field rules are checked by
/// <see cref="SheetSpecValidator"/> and <see cref="SheetUpdate"/> with messages that say what to send; the binder would
/// only say "an error occurred".
/// </para>
/// <para>
/// <c>sim_profile</c> is deliberately not here: it is the tool's own top-level parameter (a <c>BuildSpec</c>), so this
/// schema does not nest the whole build DSL (contract §7.2).
/// </para>
/// </summary>
public sealed record SheetSpec
{
    [Description("Who plays the character (author-only).")]
    public string? Player { get; init; }

    [Description("\"2014\" or \"2024\". Default: the campaign's.")]
    public string? Ruleset { get; init; }

    [Description("Species (2024) or race (2014), e.g. \"High Elf\".")]
    public string? Species { get; init; }

    [Description("Lineage or subrace (author-only).")]
    public string? Lineage { get; init; }

    [Description("Background (author-only).")]
    public string? Background { get; init; }

    [Description("The classes, starting class first, e.g. [{\"class\": \"wizard\", \"subclass\": \"Bladesinger\", \"level\": 12}]. Replaces the list; level = their sum.")]
    public IReadOnlyList<SheetClassSpec>? Classes { get; init; }

    [Description("Character level 1-20, only for a sheet without classes.")]
    public int? Level { get; init; }

    [Description("Experience points (leave out to record none).")]
    public int? Xp { get; init; }

    [Description("Ability scores 1-30, e.g. {\"dex\": 20, \"con\": 16}.")]
    public SheetAbilitiesSpec? Abilities { get; init; }

    [Description("Base AC 0-50 (armour and shield; not Bladesong or Shield).")]
    public int? Ac { get; init; }

    [Description("Hit point maximum 1-5000. Default: derived from the classes and Con.")]
    public int? MaxHp { get; init; }

    [Description("Hit point maximum reduction (Life Drain, mummy rot); a long rest clears it.")]
    public int? MaxHpReduction { get; init; }

    [Description("Current hit points. Default on a new sheet: the maximum.")]
    public int? Hp { get; init; }

    [Description("Temporary hit points.")]
    public int? TempHp { get; init; }

    [Description("Walking speed in feet.")]
    public int? Speed { get; init; }

    [Description("Default: the Dex modifier.")]
    public int? InitiativeBonus { get; init; }

    [Description("Passive Perception.")]
    public int? PassivePerception { get; init; }

    [Description("Spell save DC.")]
    public int? SpellSaveDc { get; init; }

    [Description("Spell attack bonus.")]
    public int? SpellAttack { get; init; }

    [Description("Proficient saves, e.g. [\"int\", \"wis\", \"con\"]. Default: the starting class's.")]
    public IReadOnlyList<string>? SaveProficiencies { get; init; }

    [Description("Flat extra per save, e.g. {\"con\": 1}.")]
    public SheetAbilitiesSpec? SaveBonus { get; init; }

    [Description("Damage resistances, immunities, vulnerabilities and condition immunities; each list given replaces it.")]
    public SheetDefensesSpec? Defenses { get; init; }

    [Description("Spell slot maxima by level, index 0 = 1st, e.g. [4, 3, 3]. Default for a single-class caster: its table.")]
    public IReadOnlyList<int>? Slots { get; init; }

    [Description("Pact Magic slots {\"level\": 3, \"max\": 2}; max 0 removes them.")]
    public SheetPactSpec? Pact { get; init; }

    [Description("Resources by name, merged into the sheet's; each counted (max, used, recharge) or a tracker (state).")]
    public IReadOnlyList<SheetResourceSpec>? Resources { get; init; }

    [Description("Replaces the list, e.g. [\"War Caster\", \"Tough\"].")]
    public IReadOnlyList<string>? Feats { get; init; }

    [Description("Replaces the list.")]
    public IReadOnlyList<string>? Features { get; init; }

    [Description("Replaces the list.")]
    public IReadOnlyList<string>? Spells { get; init; }

    [Description("Replaces the list.")]
    public IReadOnlyList<string>? Languages { get; init; }

    [Description("Heroic Inspiration (2024) or Inspiration (2014).")]
    public bool? Inspiration { get; init; }

    [Description("Free text (author-only).")]
    public string? Notes { get; init; }

    [Description("Where the sheet came from, e.g. \"manual\".")]
    public string? SheetSource { get; init; }
}

/// <summary>One class of <see cref="SheetSpec.Classes"/>.</summary>
public sealed record SheetClassSpec
{
    [Description("Required. An SRD class (barbarian … wizard) or a homebrew name.")]
    public string? Class { get; init; }

    [Description("The subclass, as named.")]
    public string? Subclass { get; init; }

    [Description("Required. Levels in this class, 1-20.")]
    public int? Level { get; init; }

    [Description("4, 6, 8, 10 or 12; required for a non-SRD class new to the sheet.")]
    public int? HitDie { get; init; }
}

/// <summary>Six optional integers by ability: scores (<see cref="SheetSpec.Abilities"/>) or save bonuses (<see cref="SheetSpec.SaveBonus"/>).</summary>
public sealed record SheetAbilitiesSpec
{
    [Description("Strength.")]
    public int? Str { get; init; }

    [Description("Dexterity.")]
    public int? Dex { get; init; }

    [Description("Constitution.")]
    public int? Con { get; init; }

    [Description("Intelligence.")]
    public int? Int { get; init; }

    [Description("Wisdom.")]
    public int? Wis { get; init; }

    [Description("Charisma.")]
    public int? Cha { get; init; }

    /// <summary>The given values by ability key, in str…cha order.</summary>
    public IEnumerable<(string Ability, int Value)> Given()
    {
        foreach (var (ability, value) in new[] { ("str", Str), ("dex", Dex), ("con", Con), ("int", Int), ("wis", Wis), ("cha", Cha) })
        {
            if (value is { } v)
            {
                yield return (ability, v);
            }
        }
    }
}

/// <summary>Damage and condition defences: replace each list given.</summary>
public sealed record SheetDefensesSpec
{
    [Description("Damage types, e.g. [\"fire\"].")]
    public IReadOnlyList<string>? Resist { get; init; }

    [Description("Damage types.")]
    public IReadOnlyList<string>? Immune { get; init; }

    [Description("Damage types.")]
    public IReadOnlyList<string>? Vulnerable { get; init; }

    [Description("SRD conditions, e.g. [\"poisoned\"].")]
    public IReadOnlyList<string>? ConditionImmune { get; init; }
}

/// <summary>Pact Magic slots (<see cref="SheetSpec.Pact"/>).</summary>
public sealed record SheetPactSpec
{
    [Description("Slot level 1-5.")]
    public int? Level { get; init; }

    [Description("Slots 0-4 (0 removes them).")]
    public int? Max { get; init; }
}

/// <summary>One resource of <see cref="SheetSpec.Resources"/>, matched to the sheet's by the slug of its name.</summary>
public sealed record SheetResourceSpec
{
    [Description("Required, e.g. \"Bladesong\".")]
    public string? Name { get; init; }

    [Description("Uses, 0-999.")]
    public int? Max { get; init; }

    [Description("Uses spent. Default 0.")]
    public int? Used { get; init; }

    [Description("short_rest, short_rest_one (one back on a short rest), long_rest (default), dawn or none.")]
    public string? Recharge { get; init; }

    [Description("A tracker's state instead of max, e.g. \"set\".")]
    public string? State { get; init; }

    [Description("A note.")]
    public string? Note { get; init; }

    [Description("true removes the resource.")]
    public bool? Remove { get; init; }
}
