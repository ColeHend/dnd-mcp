namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The names a document answers to by curation rather than by a rule: which 2014 subsection headings are names of
/// their section (<see cref="Headings"/>), and a few everyday names no record carries (<see cref="Names"/>).
///
/// <para>
/// Why a list and not a rule. Every <c>####</c>/<c>#####</c> heading of a 2014 rule used to be an alias, and a heading
/// alias is an exact name: 2014 "Alignment" then answered with Creating Sentient Magic Items (whose "#### Alignment" is
/// a sentient sword's alignment), "Bonus Action" with Casting Time's paragraph on bonus-action spells, "Spellcasting
/// Ability" with Charisma, and the tool told the model the topic was "covered by this entry's #### Alignment
/// subsection". A heading is listed here only when it names a rule a player looks up by that name (Grappling,
/// Concentration, Hiding), never a sub-attribute of a narrow section, a heading several sections share, or a name
/// another 2014 entry owns. Each group says why.
/// </para>
/// <para>
/// The builder skips (with a warning) an entry whose document or heading the content no longer has, and
/// <c>SrdIndexSchemaVersionTests</c> pins a digest of both lists: editing either needs a
/// <see cref="SrdIndexSchema.Version"/> bump.
/// </para>
/// </summary>
public static class SrdCuratedAliases
{
    /// <summary>
    /// 2014 rule headings that are aliases of their section (<see cref="SrdAliasSources.Heading"/>), by the rule's slug
    /// and the heading exactly as the text has it (markdown emphasis removed).
    /// </summary>
    public static IReadOnlyList<SrdHeadingAlias> Headings { get; } =
    [
        // Combat: the rules a player asks for by name, which the 2014 SRD keeps inside longer sections.
        new("melee-attacks", "Opportunity Attacks"),
        new("melee-attacks", "Two-Weapon Fighting"),
        new("melee-attacks", "Contests in Combat"),
        new("melee-attacks", "Grappling"),
        new("melee-attacks", "Shoving a Creature"),
        new("ranged-attacks", "Ranged Attacks in Close Combat"),        // not "Range": weapon range or spell range?
        new("damage-rolls", "Critical Hits"),
        new("damage-rolls", "Damage Types"),
        new("dropping-to-0-hit-points", "Instant Death"),
        new("dropping-to-0-hit-points", "Falling Unconscious"),
        new("dropping-to-0-hit-points", "Death Saving Throws"),
        new("dropping-to-0-hit-points", "Stabilizing a Creature"),
        new("dropping-to-0-hit-points", "Monsters and Death"),
        new("attack-rolls", "Rolling 1 or 20"),                         // not "Modifiers to the Roll", a sub-part
        new("your-turn", "Bonus Actions"),
        new("your-turn", "Other Activity on Your Turn"),
        new("breaking-up-your-move", "Moving between Attacks"),
        new("breaking-up-your-move", "Using Different Speeds"),

        // Spellcasting. Not Casting Time's "Bonus Action" and "Reactions" (casting times of spells, not the actions,
        // which Your Turn and Reactions cover) nor Duration's "Instantaneous" (a kind of duration, not a rule).
        new("casting-time", "Longer Casting Times"),
        new("duration", "Concentration"),
        new("components", "Verbal (V)"),
        new("components", "Somatic (S)"),
        new("components", "Material (M)"),
        new("spell-slots", "Casting a Spell at a Higher Level"),
        new("spell-slots", "Casting in Armor"),
        new("targets", "A Clear Path to the Target"),
        new("targets", "Targeting Yourself"),
        new("areas-of-effect", "Cone"),                                 // the area shapes spells name
        new("areas-of-effect", "Cube"),
        new("areas-of-effect", "Cylinder"),
        new("areas-of-effect", "Line"),
        new("areas-of-effect", "Sphere"),

        // Ability scores: each ability's checks, Strength's carrying rules and Dexterity's hiding rule. Not the headings
        // several abilities share ("Spellcasting Ability", "Attack Rolls and Damage"), not Dexterity's "Armor Class"
        // (Dexterity's part in it) and not headings another 2014 entry owns ("Initiative", Constitution's "Hit
        // Points"), nor the ability names that head Skills' lists ("Strength" is the Strength section).
        new("strength", "Strength Checks"),
        new("strength", "Lifting and Carrying"),
        new("strength", "Variant: Encumbrance"),
        new("dexterity", "Dexterity Checks"),
        new("dexterity", "Hiding"),
        new("constitution", "Constitution Checks"),
        new("intelligence", "Intelligence Checks"),
        new("wisdom", "Wisdom Checks"),
        new("charisma", "Charisma Checks"),
        new("skills", "Variant: Skills with Different Abilities"),
        new("working-together", "Group Checks"),

        // Movement and the environment. Not Speed's "Difficult Terrain": 2014 has a Difficult Terrain entry of its own.
        new("special-types-of-movement", "Climbing, Swimming, and Crawling"),
        new("special-types-of-movement", "Jumping"),
        new("speed", "Travel Pace"),
        new("creature-size", "Size Categories"),
        new("creature-size", "Space"),
        new("creature-size", "Squeezing into a Smaller Space"),
        new("vision-and-light", "Blindsight"),
        new("vision-and-light", "Darkvision"),
        new("vision-and-light", "Truesight"),
        new("food-and-water", "Food"),
        new("food-and-water", "Water"),

        // Between adventures and the game master's rules: each heading is the name the rule goes by.
        new("downtime-activities", "Crafting"),
        new("downtime-activities", "Practicing a Profession"),
        new("downtime-activities", "Recuperating"),
        new("downtime-activities", "Researching"),
        new("downtime-activities", "Training"),
        new("madness-effects", "Short-Term Madness"),
        new("madness-effects", "Long-Term Madness"),
        new("madness-effects", "Indefinite Madness"),
        new("statistics-for-objects", "Object Armor Class"),
        new("statistics-for-objects", "Object Hit Points"),
        new("outer-planes", "Demiplanes"),
        new("the-celtic-pantheon", "Celtic Deities"),
        new("the-egyptian-pantheon", "Egyptian Deities"),
        new("the-greek-pantheon", "Greek Deities"),
        new("the-norse-pantheon", "Norse Deities"),

        // Named diseases and traps, each a heading of its own. Not Sample Traps' "Sphere of Annihilation": the magic
        // item owns that name, and the trap is a use of it.
        new("sample-diseases", "Cackle Fever"),
        new("sample-diseases", "Sewer Plague"),
        new("sample-diseases", "Sight Rot"),
        new("sample-traps", "Collapsing Roof"),
        new("sample-traps", "Falling Net"),
        new("sample-traps", "Fire-Breathing Statue"),
        new("sample-traps", "Pits"),
        new("sample-traps", "Poison Darts"),
        new("sample-traps", "Poison Needle"),
        new("sample-traps", "Rolling Sphere"),
        new("traps-in-play", "Triggering a Trap"),
        new("traps-in-play", "Detecting and Disabling a Trap"),
        new("traps-in-play", "Trap Effects"),
        new("traps-in-play", "Trap Save DCs and Attack Bonuses"),
        new("traps-in-play", "Damage Severity by Level"),
        new("traps-in-play", "Complex Traps"),

        // Not listed at all: Creating Sentient Magic Items' "Abilities", "Alignment", "Communication", "Senses" and
        // "Special Purpose", properties of one sentient item that hijacked the general words.
    ];

    /// <summary>
    /// Everyday names no record carries (<see cref="SrdAliasSources.Curated"/>), each with the evidence that it was
    /// asked for and missed. They rank like the other edition's name, so none beats a document that owns the name.
    /// </summary>
    public static IReadOnlyList<SrdCuratedName> Names { get; } =
    [
        // "Shove" and "Shoving" missed in both editions and suggested the Shovel. 2024 folds the shove into Unarmed
        // Strike (its Shove option); 2014 has it as Melee Attacks' "#### Shoving a Creature". The 2014 heading name also
        // reaches the 2024 rule, so edition "both" for "Shoving a Creature" compares the shove, not 2024 Grappling.
        new(SrdEdition.Edition2024, SrdKinds.Rule, "unarmed-strike", "Shove"),
        new(SrdEdition.Edition2024, SrdKinds.Rule, "unarmed-strike", "Shoving"),
        new(SrdEdition.Edition2024, SrdKinds.Rule, "unarmed-strike", "Shoving a Creature"),
        new(SrdEdition.Edition2014, SrdKinds.Rule, "melee-attacks", "Shove"),
        new(SrdEdition.Edition2014, SrdKinds.Rule, "melee-attacks", "Shoving"),

        // "Grapple" missed in both editions; 2014 search ranked Melee Attacks (which holds "#### Grappling") eighth.
        new(SrdEdition.Edition2024, SrdKinds.Rule, "grappling", "Grapple"),
        new(SrdEdition.Edition2014, SrdKinds.Rule, "melee-attacks", "Grapple"),

        // 2024 "Damage Resistance" answered with five dragonborn traits (the 2014 trait's name) and search ranked the
        // Resistance rule ninth; the glossary calls the rule Resistance.
        new(SrdEdition.Edition2024, SrdKinds.Rule, "resistance", "Damage Resistance"),
    ];
}

/// <summary>A 2014 rule's subsection heading that is an alias of the rule. <c>slug: heading</c> in the schema digest.</summary>
public sealed record SrdHeadingAlias(string Slug, string Heading)
{
    public override string ToString() => $"2014/rule/{Slug}: {Heading}";
}

/// <summary>A curated alias of one document. <c>edition/kind/slug: alias</c> in the schema digest.</summary>
public sealed record SrdCuratedName(string Edition, string Kind, string Slug, string Alias)
{
    public override string ToString() => $"{Edition}/{Kind}/{Slug}: {Alias}";
}
