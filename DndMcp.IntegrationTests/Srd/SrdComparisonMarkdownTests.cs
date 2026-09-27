using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// Invariant: the at-a-glance table marks a row "yes" exactly when the value changed between editions, "wording only"
/// when the same value is spelled differently, and "—" when the text is identical; and it exists only for spells and
/// monsters.
///
/// <para>
/// The pairs are real 2014/2024 counterparts. The rows pin both directions of the one judgement the table makes:
/// 2024's renamed vocabulary ("1 action" → "Action", "19 (natural armor)" → "19") must not be called a change, and
/// real changes hidden in similar text (cure wounds' school, the red dragon's Charisma, the goblin's hit dice) must.
/// </para>
/// </summary>
public sealed class SrdComparisonMarkdownTests
{
    private const string Header = "| Field | 2014 | 2024 | Changed |";

    private static VendoredSrdLookup Lookup => VendoredSrdLookup.Instance;

    [Theory]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Level | 3 | 3 | — |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| School | Evocation | Evocation | — |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Casting Time | 1 action | Action | wording only |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Range | 150 feet | 150 feet | — |")]
    // The material is part of the row: its wording changed, its cost and consumption did not.
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Components | V, S, M (A tiny ball of bat guano and sulfur.) | V, S, M (a ball of bat guano and sulfur) | wording only |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Duration | Instantaneous | Instantaneous | — |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Concentration | no | no | — |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Ritual | no | no | — |")]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", "| Classes | Sorcerer, Wizard | Sorcerer, Wizard | — |")]
    [InlineData("2014/spell/cure-wounds", "2024/spell/cure-wounds", "| School | Evocation | Abjuration | yes |")]
    [InlineData("2014/spell/fire-bolt", "2024/spell/fire-bolt", "| Level | cantrip | cantrip | — |")]
    [InlineData("2014/spell/detect-magic", "2024/spell/detect-magic", "| Ritual | yes | yes | — |")]
    [InlineData("2014/spell/detect-magic", "2024/spell/detect-magic", "| Range | Self | Self | — |")]
    [InlineData("2014/spell/detect-magic", "2024/spell/detect-magic", "| Classes | Bard, Cleric, Druid, Paladin, Ranger, Sorcerer, Wizard | Bard, Cleric, Druid, Paladin, Ranger, Sorcerer, Warlock, Wizard | yes |")]
    [InlineData("2014/spell/bless", "2024/spell/bless", "| Duration | Up to 1 minute | up to 1 minute | wording only |")]
    [InlineData("2014/spell/bless", "2024/spell/bless", "| Concentration | yes | yes | — |")]
    // 2024 stores the trigger in the casting time; the 2014 data never does, so there is nothing to compare it with.
    [InlineData("2014/spell/shield", "2024/spell/shield", "| Casting Time | 1 reaction | Reaction, which you take when you are hit by an attack roll or targeted by the Magic Missile spell | unknown (no 2014 trigger in the data) |")]
    // The components scraped into 2024's range are compared after the repair, not as "Touch Component: V, S".
    [InlineData("2014/spell/guidance", "2024/spell/guidance", "| Range | Touch | Touch | — |")]
    [InlineData("2014/spell/guidance", "2024/spell/guidance", "| Components | V, S | V, S | — |")]
    // 2024 states concentration only in the duration text (its flag is false); the row agrees with the body.
    [InlineData("2014/spell/protection-from-evil-and-good", "2024/spell/protection-from-evil-and-good", "| Concentration | yes | yes | — |")]
    [InlineData("2014/spell/protection-from-evil-and-good", "2024/spell/protection-from-evil-and-good", "| Duration | Up to 10 minutes | Concentration up to 10 minutes | wording only |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Size/Type | Huge dragon | Huge dragon | — |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Armor Class | 19 (natural armor) | 19 | wording only |")]
    // 2014 stores "19d12+133"; unless both are spaced the same way this row would claim a change.
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Hit Points | 256 (19d12 + 133) | 256 (19d12 + 133) | — |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Speed | 40 ft., climb 40 ft., fly 80 ft. | 40 ft., climb 40 ft., fly 80 ft. | — |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Challenge | 17 (18,000 XP) | 17 (18,000 XP; 20,000 XP in lair) | yes |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| STR | 27 (+8) | 27 (+8) | — |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| CHA | 21 (+5) | 23 (+6) | yes |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Actions | 6 | 4 | yes |")]
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", "| Legendary Actions | 3 | 3 | — |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Size/Type | Small humanoid (goblinoid) | Small fey | yes |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Alignment | neutral evil | chaotic neutral | yes |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Armor Class | 15 (Leather Armor, Shield) | 15 (Leather Armor, Shield) | — |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Hit Points | 7 (2d6) | 10 (3d6) | yes |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Challenge | 1/4 (50 XP) | 1/4 (50 XP) | — |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| DEX | 14 (+2) | 15 (+2) | yes |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Traits | 1 | 0 | yes |")]
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", "| Bonus Actions | 0 | 1 | yes |")]
    // Upstream splits the 2014 azer's "17 (natural armor, shield)" in two; merged back, it is the same 17.
    [InlineData("2014/monster/azer", "2024/monster/azer-sentinel", "| Armor Class | 17 (natural armor, Shield) | 17 | wording only |")]
    // The 2024 data has no subtypes at all (SRD 5.2.1 still prints "Huge Fiend (Demon)"): a data gap, not a change.
    [InlineData("2014/monster/balor", "2024/monster/balor", "| Size/Type | Huge fiend (demon) | Huge fiend | wording only |")]
    // "24 hours" and "1 day" are the same duration.
    [InlineData("2014/spell/find-the-path", "2024/spell/find-the-path", "| Duration | Up to 24 hours | up to 1 day | wording only |")]
    [InlineData("2014/spell/forbiddance", "2024/spell/forbiddance", "| Duration | 24 hours | 1 day | wording only |")]
    public void Glance_RealCounterparts_HasExactRow(string reference2014, string reference2024, string expectedRow)
    {
        var table = Glance(reference2014, reference2024);

        Assert.True(table.Split('\n').Contains(expectedRow), $"Expected the row\n{expectedRow}\nin:\n{table}");
    }

    [Theory]
    // A cost or its consumption changed: the letters alone ("V, S, M") would say nothing did.
    [InlineData("2014/spell/bless", "2024/spell/bless", SrdComparisonMarkdown.Changed)]
    [InlineData("2014/spell/forcecage", "2024/spell/forcecage", SrdComparisonMarkdown.Changed)]
    [InlineData("2014/spell/gentle-repose", "2024/spell/gentle-repose", SrdComparisonMarkdown.Changed)]
    [InlineData("2014/spell/imprisonment", "2024/spell/imprisonment", SrdComparisonMarkdown.Changed)]
    [InlineData("2014/spell/magnificent-mansion", "2024/spell/magnificent-mansion", SrdComparisonMarkdown.Changed)]
    // Same cost and consumption in other words, upstream's stray commas ("250+, GP") and "a copper coin" included.
    [InlineData("2014/spell/plane-shift", "2024/spell/plane-shift", SrdComparisonMarkdown.WordingOnly)]
    [InlineData("2014/spell/true-seeing", "2024/spell/true-seeing", SrdComparisonMarkdown.WordingOnly)]
    [InlineData("2014/spell/detect-thoughts", "2024/spell/detect-thoughts", SrdComparisonMarkdown.WordingOnly)]
    // The two 2024 triggers that changed the rule: shining smite is cast after the hit, not before it; counterspell
    // can only stop a spell with components. The 2014 data holds no trigger to compare either with.
    [InlineData("2014/spell/branding-smite", "2024/spell/shining-smite", "unknown (no 2014 trigger in the data)", "Casting Time")]
    [InlineData("2014/spell/counterspell", "2024/spell/counterspell", "unknown (no 2014 trigger in the data)", "Casting Time")]
    public void Glance_RealCounterparts_RowHasVerdict(string reference2014, string reference2024, string verdict, string field = "Components")
    {
        var row = Glance(reference2014, reference2024).Split('\n').Single(l => l.StartsWith($"| {field} |", StringComparison.Ordinal));

        Assert.EndsWith($"| {verdict} |", row, StringComparison.Ordinal);
    }

    [Theory]
    // Both sides state a trigger: it is compared like any text.
    [InlineData("1 reaction, which you take when you fall", "Reaction, which you take when you fall", SrdComparisonMarkdown.WordingOnly)]
    [InlineData("1 reaction, which you take when you fall", "Reaction, which you take when you or a creature you can see falls", SrdComparisonMarkdown.Changed)]
    // Only one side states one: the data cannot say whether it changed.
    [InlineData("1 reaction", "Reaction, which you take when you fall", "unknown (no 2014 trigger in the data)")]
    [InlineData("1 reaction, which you take when you fall", "Reaction", "unknown (no 2024 trigger in the data)")]
    // No trigger on either side: the action type alone.
    [InlineData("1 bonus action", "Bonus Action", SrdComparisonMarkdown.WordingOnly)]
    [InlineData("1 action", "1 minute", SrdComparisonMarkdown.Changed)]
    public void Glance_CastingTimeTrigger_IsComparedOnlyWhenBothSidesHaveOne(string castingTime2014, string castingTime2024, string verdict)
    {
        var row = Glance(Spell(SrdEdition.Edition2014, $"{{\"casting_time\":\"{castingTime2014}\"}}"), Spell(SrdEdition.Edition2024, $"{{\"casting_time\":\"{castingTime2024}\"}}"))
            .Split('\n').Single(l => l.StartsWith("| Casting Time |", StringComparison.Ordinal));

        Assert.EndsWith($"| {verdict} |", row, StringComparison.Ordinal);
    }

    [Theory]
    // 2024 records carry no subtype at all, so a 2014 subtype is not compared against its absence.
    [InlineData("{\"size\":\"Huge\",\"type\":\"fiend\"}", SrdComparisonMarkdown.WordingOnly)]
    // A 2024 record that did state one is compared with it.
    [InlineData("{\"size\":\"Huge\",\"type\":\"fiend\",\"subtype\":\"devil\"}", SrdComparisonMarkdown.Changed)]
    [InlineData("{\"size\":\"Huge\",\"type\":\"fiend\",\"subtype\":\"demon\"}", "—")]
    [InlineData("{\"size\":\"Large\",\"type\":\"fiend\"}", SrdComparisonMarkdown.Changed)]
    public void Glance_MonsterSubtype_IsComparedOnlyWhenThe2024RecordHasOne(string json2024, string verdict)
    {
        var monster2014 = Monster(SrdEdition.Edition2014, "{\"size\":\"Huge\",\"type\":\"fiend\",\"subtype\":\"demon\"}");
        var row = Glance(monster2014, Monster(SrdEdition.Edition2024, json2024))
            .Split('\n').Single(l => l.StartsWith("| Size/Type |", StringComparison.Ordinal));

        Assert.EndsWith($"| {verdict} |", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2014/spell/fireball", "2024/spell/fireball", 9)]
    // 6 stat rows, 6 scores, and one count row for each entry section either edition has: traits, actions, legendary.
    [InlineData("2014/monster/adult-red-dragon", "2024/monster/adult-red-dragon", 15)]
    // Traits, actions and bonus actions; neither has reactions or legendary actions, so those rows are left out.
    [InlineData("2014/monster/goblin", "2024/monster/goblin-warrior", 15)]
    [InlineData("2014/monster/hydra", "2024/monster/hydra", 14)]
    public void Glance_RealCounterparts_IsOneHeaderAndOneRowPerField(string reference2014, string reference2024, int rows)
    {
        var lines = Glance(reference2014, reference2024).Split('\n');

        Assert.Equal(Header, lines[0]);
        Assert.Equal("|---|---|---|---|", lines[1]);
        Assert.Equal(rows, lines.Length - 2);
        Assert.DoesNotContain(lines, l => l.StartsWith("| Reactions |", StringComparison.Ordinal));
    }

    [Theory]
    // Kinds that are mostly prose get no table.
    [InlineData("2014/condition/grappled", "2024/condition/grappled")]
    [InlineData("2014/class/fighter", "2024/class/fighter")]
    [InlineData("2014/magic-item/flame-tongue", "2024/magic-item/flame-tongue")]
    // Different kinds (race ↔ species) are never compared field by field.
    [InlineData("2014/race/elf", "2024/species/elf")]
    [InlineData("2014/spell/fireball", "2024/monster/adult-red-dragon")]
    public void Glance_KindWithoutATable_IsNull(string reference2014, string reference2024)
    {
        Assert.Null(SrdComparisonMarkdown.Glance(Lookup.Require(reference2014), Lookup.Require(reference2024)));
    }

    private static string Glance(string reference2014, string reference2024) =>
        Glance(Lookup.Require(reference2014), Lookup.Require(reference2024));

    private static string Glance(SrdDocument doc2014, SrdDocument doc2024) =>
        SrdComparisonMarkdown.Glance(doc2014, doc2024)
        ?? throw new InvalidOperationException($"No glance table for {doc2014.Ref} vs {doc2024.Ref}.");

    private static SrdDocument Spell(string edition, string json) =>
        new() { Edition = edition, Kind = SrdKinds.Spell, Slug = "odd", Name = "Odd", Json = json };

    private static SrdDocument Monster(string edition, string json) =>
        new() { Edition = edition, Kind = SrdKinds.Monster, Slug = "odd", Name = "Odd", Json = json };
}
