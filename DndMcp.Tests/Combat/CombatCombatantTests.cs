using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: combatants enter, change and leave as contract §6.2 says — copies numbered and continued in a new init group,
/// default sides and hit points (D17: unknown in a player campaign, the average in a DM one), sheet-seeded combatants carry
/// the sheet's state and its snapshot, a left character re-joins its own combatant, <c>set</c> changes hit points (and the
/// maximum when there was none) with the drops and wakes that follow, <c>leave</c> ends what the leaver held; and a
/// combatant is addressed by its exact tracker name, then its entity's handle or slug, then a unique prefix, or every copy
/// with a star — never a left one by prefix or star.
/// </summary>
public sealed class CombatCombatantTests
{
    // ------------------------------------------------------------------------------------------------------------------
    // add

    [Fact]
    public void Add_Copies_NumberedOneInitGroup_ALaterAddContinuesInANewGroup()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)).Next;
        s = Add(s, Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)).Next;
        Assert.Equal(["Mummy", "Mummy 2", "Mummy 3", "Mummy 4"], s.Combatants.Select(c => c.Name));
        Assert.Equal(2, s.Combatants.Select(c => c.InitGroup).Distinct().Count());
        Assert.Equal(s.Combatants[0].InitGroup, s.Combatants[1].InitGroup);
        Assert.Equal([1d, 2d, 3d, 4d], s.Combatants.Select(c => c.OrderKey));
    }

    [Fact]
    public void Add_ASingleMonsterOfTheSameKind_ContinuesTheNumbering()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg)).Next;
        s = Add(s, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg)).Next;
        Assert.Equal(["Mummy Lord", "Mummy Lord 2"], s.Combatants.Select(c => c.Name));
        Assert.All(s.Combatants, c => Assert.Null(c.InitGroup));
    }

    [Theory]
    [InlineData(false, 58)]
    [InlineData(true, null)]
    public void Add_SrdWithoutHp_AverageInADmCampaign_UnknownInAPlayerCampaign(bool player, int? hp)
    {
        var s = Add(Encounter(E2014, player: player), Monster(Block(E2014, "mummy"))).Next;
        Assert.Equal(hp, s.Named("Mummy").Hp);
    }

    [Fact]
    public void Add_HpRoll_EachCopyRollsItsHitDice_LoggedHitPoints()
    {
        var s = Encounter(E2014);
        var op = new AddOp([Monster(Block(E2014, "mummy"), 2, HpChoice.Roll)], ["m1", "m2"]);
        var needs = CombatTracker.Needs(s, op);
        Assert.Equal([new RollNeed("hp:m1", "m1", P.HitPoints, "9d8+18"), new RollNeed("hp:m2", "m2", P.HitPoints, "9d8+18")], needs);
        var rolls = new Dictionary<string, RolledValue> { ["hp:m1"] = new(50, []), ["hp:m2"] = new(61, []) };
        var result = CombatTracker.Apply(s, op, rolls);
        Assert.Equal((50, 61), (result.Next.Find("m1")!.Hp!.Value, result.Next.Find("m2")!.Hp!.Value));
        Assert.Equal(["hp:m1", "hp:m2"], result.Changes.Select(c => c.RollKey));
        Assert.Throws<ArgumentException>(() => CombatTracker.Apply(s, op));
    }

    [Theory]
    [InlineData(null, "pc", "party")]
    [InlineData(null, "npc", "ally")]
    [InlineData("neutral", "pc", "neutral")]
    public void Add_CharacterSide_DefaultsPartyForAPcAllyOtherwise(string? side, string subtype, string expected)
    {
        var s = Add(Encounter(), new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit", EntitySubtype = subtype, Side = side }).Next;
        var kit = s.Named("Kit");
        Assert.Equal(expected, kit.Side);
        Assert.Equal(subtype == "pc", kit.MakesDeathSaves);
        Assert.False(kit.HpKnown);
        Assert.False(kit.IsSheetSeeded);
    }

    [Fact]
    public void Add_SrdWithACharacter_PlayedFromTheStatBlock_LinkedNotSheetSeeded()
    {
        var sheet = Sheet("e-old", """{ "classes": [{ "class": "wizard", "level": 18 }], "max_hp": 99 }""", E2014);
        var s = Add(Encounter(E2014), new AddEntry
        {
            Monster = Block(E2014, "lich"), Sheet = sheet, EntityId = "e-old", EntityName = "The Old King", EntityHandle = "character:old-king", EntitySubtype = "npc",
            Hp = HpChoice.Avg,
        }).Next;
        var lich = s.Combatants.Single();
        Assert.Equal(("Lich", "e-old", CampaignValues.CombatSides.Enemy), (lich.Name, lich.EntityId, lich.Side));
        Assert.False(lich.IsSheetSeeded);
        Assert.Equal(lich.StatBlock!.HitPoints, lich.Hp);
        Assert.Equal("lich", lich.Address);
    }

    [Fact]
    public void Add_TypedNameAlone_IsACustomCombatant()
    {
        var s = Add(Encounter(), new AddEntry { Name = "Wraith Blade", Count = 3, Hp = HpChoice.Of(33), Ac = 15 }).Next;
        Assert.Equal(["Wraith Blade", "Wraith Blade 2", "Wraith Blade 3"], s.Combatants.Select(c => c.Name));
        Assert.All(s.Combatants, c => Assert.Equal((33, 15, CampaignValues.CombatSides.Enemy), (c.Hp!.Value, c.Ac!.Value, c.Side)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(70)]
    [InlineData(76)]
    public void Address_OfAnyTrackerName_IsTheOneThatCombatantAndNoOther(int length)
    {
        // A tracker name may have 80 characters, a slug 60: the address a printed call carries is never cut to a prefix
        // that every copy shares ("aaaa…" for "Aaaa… 2"), which the call would be refused for.
        var name = "A" + new string('a', length - 1);
        var s = Add(Encounter(), new AddEntry { Name = name, Count = 3, Hp = HpChoice.Of(33), Ac = 15 }).Next;

        Assert.All(s.Combatants, c => Assert.Same(c, CombatAddressing.Resolve(s.Combatants, c.Address, "targets")));
        Assert.Equal(CampaignText.Key(name + " 2").Replace(' ', '-'), s.Combatants[1].Address);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Add_CountOutOfRange_IsRefused(int count)
    {
        Assert.Throws<DndInputException>(() => Add(Encounter(), new AddEntry { Name = "X", Count = count, Hp = HpChoice.Of(5) }));
    }

    [Fact]
    public void Add_NoSource_IsRefused_AvgWithoutAStatBlockIsRefused_HpOnASheetSeededOneIsRefused()
    {
        Assert.Throws<DndInputException>(() => Add(Encounter(), new AddEntry { Hp = HpChoice.Of(5) }));
        Assert.Throws<DndInputException>(() => Add(Encounter(), new AddEntry { Name = "X", Hp = HpChoice.Avg }));
        var ex = Assert.Throws<DndInputException>(() => Add(Encounter(E2014), Pc("e-torch", "Torch", "character:torch", Torch()) with { Hp = HpChoice.Of(5) }));
        Assert.Contains("seeded from its sheet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_TooFewIds_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatTracker.Apply(Encounter(), new AddOp([new AddEntry { Name = "X", Count = 2, Hp = HpChoice.Of(5) }], ["only-one"])));
    }

    [Fact]
    public void Add_TheSameCharacterTwice_IsRefused_ALeftOneReJoinsWithoutInitiative()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        s = Add(s, new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit", EntitySubtype = "pc" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("kit", Total: 15)] }).Next;
        var ex = Assert.Throws<DndInputException>(() => Add(s, new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit" }));
        Assert.Contains("already in the fight as Kit", ex.Message, StringComparison.Ordinal);

        s = Step(s, new LeaveOp(["kit"])).Next;
        var rejoined = Add(s, new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit" });
        Assert.Equal(3, rejoined.Next.Combatants.Count);
        var kit = rejoined.Next.Named("Kit");
        Assert.False(kit.Removed);
        Assert.Null(kit.Initiative);
        Assert.Contains(rejoined.Of(K.NoInitiative), r => r.CombatantId == kit.Id);
        Assert.True(JsonNode.Parse(rejoined.Changes.Single().Detail!)!["rejoined"]!.GetValue<bool>());
    }

    [Fact]
    public void FromSheet_PersistedTimedEffectsAnchorOnItself_UntilRemovedKeptWithTheirSource()
    {
        var sheet = Sheet("e-x", """{ "classes": [{ "class": "wizard", "level": 5 }], "max_hp": 30 }""", E2014) with
        {
            Conditions =
            [
                new SheetCondition("cursed (Mucus Cloud)", "Aboleth", "until_removed", null, "from The dark station (fixture), round 1"),
                new SheetCondition("Haste", null, "rounds", 98, "from The crypt, round 1", """{"ac":2}"""),
            ],
            Concentration = new SheetConcentration("Circle of Power", 5, 98, "from The crypt (fixture), round 1"),
            DeathSaves = new SheetDeathSaves(0, 1, false),
            Exhaustion = 2,
            TempHp = 4,
        };
        var c = CombatantFactory.FromSheet(sheet, "x", "X", E2014);
        var curse = c.Conditions[0];
        Assert.Equal((D.UntilRemoved, (string?)"Aboleth", (AppliedAt?)null), (curse.Duration, curse.SourceNote, curse.Applied));
        var haste = c.Conditions[1];
        Assert.Equal((D.Rounds, new ConditionExpiry(99, CombatValues.ExpiryPoints.Start, "x"), 2), (haste.Duration, haste.Expires!, haste.Effect!.Ac!.Value));
        Assert.Equal(new ConditionExpiry(99, CombatValues.ExpiryPoints.Start, "x"), c.Concentration!.Expires);
        Assert.Equal((new DeathSaveTally(0, 1, false), 2, 4), (c.DeathSaves, c.Exhaustion, c.TempHp));
        Assert.True(c.MakesDeathSaves);
        Assert.Equal(sheet.Exhaustion, (int)(long)c.SheetSnapshot!.Column(SheetColumns.Exhaustion)!);
        Assert.Equal(SheetJson.Column(sheet, SheetColumns.Conditions), c.SheetSnapshot.Column(SheetColumns.Conditions));
    }

    /// <summary>A fighter's sheet at 0 HP and dying, as campaign_character damage leaves it (Unconscious and Prone until removed).</summary>
    private static CharacterSheet DownSheet(bool storedConditions = true, bool stable = false) =>
        Sheet("e-x", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44 }""", E2024) with
        {
            Hp = 0,
            DeathSaves = new SheetDeathSaves(stable ? 0 : 1, stable ? 0 : 2, stable),
            Conditions = storedConditions
                ? [new SheetCondition("unconscious", null, "until_removed", null, null), new SheetCondition("prone", null, "until_removed", null, null),
                   new SheetCondition("poisoned", null, "until_removed", null, null)]
                : [],
        };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FromSheet_AtZeroHpNotDead_SeedsTheTrackersDownState_AStoredUnconsciousIsThatState_AStoredProneStaysAsStored(bool stored, bool stable)
    {
        // C08/CV01: one representation of "unconscious at 0 HP": the tracker's zero_hp Unconscious (healing ends it) and
        // Prone until it stands, with the stored tallies; a stored Unconscious is that same condition, never a second one.
        // UR03: a Prone the sheet holds is the sheet's own (an author set it): it stays as stored, and no second is added.
        var c = CombatantFactory.FromSheet(DownSheet(stored, stable), "x", "X", E2024);

        var unconscious = Assert.Single(c.Conditions, x => x.Name == "unconscious");
        Assert.Equal((D.ZeroHp, (AppliedAt?)null), (unconscious.Duration, unconscious.Applied));
        Assert.Equal(stored ? D.UntilRemoved : D.UntilStands, Assert.Single(c.Conditions, x => x.Name == "prone").Duration);
        Assert.Equal(stored ? [D.UntilRemoved] : Array.Empty<string>(), c.Conditions.Where(x => x.Name == "poisoned").Select(x => x.Duration));
        Assert.Equal(stable ? DeathSaveTally.Stabilized : new DeathSaveTally(1, 2, false), c.DeathSaves);
        Assert.Equal(!stable, c.Dying);
    }

    [Fact]
    public void FromSheet_AboveZeroHp_AStoredKnockOutIsTheTrackersKnockOut_AStoredProneStays_ADeadSheetGetsNoDownState()
    {
        // CR06: end writes a knock-out still out as Unconscious noted "knocked out"; seeded back it is the tracker's
        // knock-out, which a heal ends; the author's Prone stays as stored.
        var knockedOut = DownSheet() with
        {
            Hp = 20,
            DeathSaves = SheetDeathSaves.Reset,
            Conditions = [new SheetCondition("unconscious", null, "until_removed", null, CombatEnd.KnockedOutNote), new SheetCondition("prone", null, "until_removed", null, null)],
        };
        var c = CombatantFactory.FromSheet(knockedOut, "x", "X", E2024);
        var unconscious = Assert.Single(c.Conditions, x => x.Name == "unconscious");
        Assert.Equal((D.ZeroHp, true), (unconscious.Duration, unconscious.KnockOut));
        Assert.Equal(D.UntilRemoved, Assert.Single(c.Conditions, x => x.Name == "prone").Duration);

        var dead = CombatantFactory.FromSheet(DownSheet(storedConditions: false) with { DeathSaves = new SheetDeathSaves(0, 3, false) }, "x", "X", E2024);
        Assert.True(dead.Dead);
        Assert.Empty(dead.Conditions);
    }

    [Theory]
    [InlineData(E2024, "knocked out")]
    [InlineData(E2024, "Knocked out ")]
    [InlineData(E2014, "knocked out")]
    public void FromSheet_AboveZeroHp_AStoredKnockOut_IsTheTrackersKnockOut_AHealWakesIt(string edition, string note)
    {
        // CR06: end writes a 2024 knock-out as Unconscious noted "knocked out"; seeded back it is the tracker's knock-out
        // (and Prone until it stands, as the knock-out added it), so a heal in the fight wakes it, as SRD 5.2.1 says.
        var sheet = Sheet("e-x", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44 }""", edition) with
        {
            Hp = 1,
            Conditions = [new SheetCondition("unconscious", null, "until_removed", null, note)],
        };

        var c = CombatantFactory.FromSheet(sheet, "x", "X", edition);

        Assert.True(c.KnockedOut);
        Assert.Equal(["unconscious", "prone"], c.Conditions.Select(x => x.Name));
        var s = Add(Encounter(edition), Pc("e-x", "X", "character:x", sheet), new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("X", Total: 5), new("Orc", Total: 12)] }).Next;
        var healed = Step(s, new HealOp(["X"]) { Amount = 2 }).Next.Named("X");
        Assert.Equal((3, false, true), (healed.Hp!.Value, healed.Has("unconscious"), healed.Has("prone")));
    }

    /// <summary>
    /// F2R04: any other stored Unconscious on a sheet above 0 HP — a Sleep, an author's condition — is no knock-out. It is
    /// seeded until removed, as F1 seeded it, with no Prone added: a heal in the fight does not end it (by a full-HP heal,
    /// which regains nothing, or any other), only condition remove does; F2 made it the tracker's knock-out, which a heal
    /// of a 32/32 sleeper "woke" and which from_state simulated at 0 HP.
    /// </summary>
    [Theory]
    [InlineData(E2024, null, 44)]
    [InlineData(E2024, "asleep", 44)]
    [InlineData(E2024, null, 30)]
    [InlineData(E2014, null, 44)]
    [InlineData(E2014, "Sleep spell", 30)]
    public void FromSheet_AboveZeroHp_ASleepLikeUnconscious_IsUntilRemoved_AHealDoesNotEndIt_ConditionRemoveDoes(string edition, string? note, int hp)
    {
        var sheet = Sheet("e-x", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44 }""", edition) with
        {
            Hp = hp,
            Conditions = [new SheetCondition("unconscious", null, "until_removed", null, note)],
        };

        var c = CombatantFactory.FromSheet(sheet, "x", "X", edition);

        Assert.False(c.KnockedOut);
        var unconscious = Assert.Single(c.Conditions);
        Assert.Equal(("unconscious", D.UntilRemoved, false), (unconscious.Name, unconscious.Duration, unconscious.KnockOut));
        var s = Add(Encounter(edition), Pc("e-x", "X", "character:x", sheet), new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("X", Total: 5), new("Orc", Total: 12)] }).Next;
        var healed = Step(s, new HealOp(["X"]) { Amount = 2 });
        Assert.Empty(healed.Of(K.Revived));
        Assert.Equal((Math.Min(44, hp + 2), true), (healed.Next.Named("X").Hp!.Value, healed.Next.Named("X").Has("unconscious")));
        Assert.False(Step(healed.Next, new ConditionOp(["X"]) { Remove = ["unconscious"] }).Next.Named("X").Has("unconscious"));
    }

    [Fact]
    public void FromSheet_DroppedOutOfCombat_HealedInTheFight_IsConscious_CanConcentrate_NoAutoCritReminder()
    {
        // CV01: the sheet's until_removed Unconscious used to survive the heal ("regains consciousness" while still
        // incapacitated, concentration refused, every hit a reminder of an automatic critical).
        var s = Add(Encounter(E2024), Pc("e-x", "X", "character:x", DownSheet()), new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("X", Total: 5), new("Orc", Total: 12)] }).Next;
        Assert.True(Step(s, new DamageOp(["X"]) { Amount = 1 }).Says(K.UnconsciousCrit, "X is unconscious"));

        var healed = Step(s, new HealOp(["X"]) { Amount = 5 });

        Assert.True(healed.Says(K.Revived, "X regains consciousness at 5 HP (still prone)"));
        var x = healed.Next.Named("X");
        Assert.Equal(["prone", "poisoned"], x.Conditions.Select(c => c.Name));
        Assert.False(x.Incapacitated);
        Assert.NotNull(Step(healed.Next, new ConcentrationOp(["X"]) { Spell = "Bless" }).Next.Named("X").Concentration);
        Assert.Empty(Step(healed.Next, new DamageOp(["X"]) { Amount = 1 }).Of(K.UnconsciousCrit));
    }

    [Fact]
    public void FromSheet_NoMaxHp_UnknownHitPoints_InitiativeFromDex()
    {
        var sheet = Sheet("e-x", """{ "classes": [{ "class": "rogue", "level": 5 }], "abilities": { "dex": 18 } }""", E2024);
        var c = CombatantFactory.FromSheet(sheet, "x", "X", E2024);
        Assert.False(c.HpKnown);
        Assert.Equal(4, c.InitBonus);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // set

    [Theory]
    [InlineData("Goblin Warrior 3", 7, 17, 4, null, "Goblin Warrior 3: hp 10 → 7, ac 15 → 17, init_bonus +2 → +4.")]
    [InlineData("Goblin Warrior", null, null, -1, "ally", "Goblin Warrior: init_bonus +2 → −1, side enemy → ally.")]
    [InlineData("Goblin Warrior 2", 12, null, null, null, "Goblin Warrior 2: hp 10 → 12, max_hp 10 → 12.")]
    public void Set_SaysWhatChanged_FieldByField_InitBonusToo(string name, int? hp, int? ac, int? init, string? side, string line)
    {
        // U06: set was the one step with no "What changed": the author could see an init_bonus change nowhere.
        var s = Add(Encounter(), Monster(Block(E2024, "goblin-warrior"), 3, HpChoice.Avg)).Next;

        var result = Step(s, new SetOp([new SetEntry(name) { Hp = hp, Ac = ac, InitBonus = init, Side = side }]));

        Assert.Equal([line], result.Notes);
    }

    [Fact]
    public void Set_WhatChanged_UnknownHitPointsHiddenAndDeathSaves_EveryEntryItsOwnLine()
    {
        var s = Add(Encounter(), new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit", EntitySubtype = "pc" },
            new AddEntry { Name = "Ambusher", Hp = HpChoice.Of(10), Hidden = true }).Next;

        var result = Step(s, new SetOp([new SetEntry("kit") { Hp = 80 }, new SetEntry("Ambusher") { Hidden = false, DeathSaves = true, MaxHpReduction = 2 }]));

        Assert.Equal(
            [
                "Kit: hp unknown → 80, max_hp unknown → 80.",
                "Ambusher: max_hp_reduction 0 → 2, hidden true → false, death_saves false → true.",
                "Ambusher is revealed (no longer hidden).",
                "Ambusher's hit point maximum is now 8: 10 → 8 HP.",
            ],
            result.Notes);
        Assert.Equal(["Kit: nothing changed."], Step(result.Next, new SetOp([new SetEntry("kit") { Hp = 80 }])).Notes);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("—")]
    [InlineData("…")]
    [InlineData("???")]
    public void Add_ANameWithNoLetterOrDigit_IsRefused_ItWouldBeAddressedAsAnotherSuchName(string name)
    {
        // C02: every such name has the empty key: "—" was numbered a copy of "!!!", and damage to "???" landed on "!!!".
        var ex = Assert.Throws<DndInputException>(() => Add(Encounter(), new AddEntry { Name = name, Hp = HpChoice.Of(5) }));

        Assert.Equal($"combatants item 1 name \"{name}\" has no letter or digit; give combatants item 1 a name with a letter or digit.", ex.Message);
    }

    [Fact]
    public void Add_SeveralNamesWithNoLetterOrDigit_EachIsReported_ATypedNameOverAStatBlockToo()
    {
        var ex = Assert.Throws<DndInputException>(() => Add(Encounter(),
            new AddEntry { Name = "!!!", Hp = HpChoice.Of(5) }, new AddEntry { Name = "—", Hp = HpChoice.Of(6) }, Monster(Block(E2024, "ogre"), name: "???")));

        Assert.Contains("combatants item 1 name \"!!!\" has no letter or digit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("combatants item 2 name \"—\" has no letter or digit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("combatants item 3 name \"???\" has no letter or digit", ex.Message, StringComparison.Ordinal);
        Assert.Equal("O'Brien!", Add(Encounter(), new AddEntry { Name = "O'Brien!", Hp = HpChoice.Of(5) }).Next.Combatants.Single().Name);
    }

    [Fact]
    public void Set_HpOnUnknown_SetsTheMaximumToo_ZeroWithoutAMaximumIsRefused()
    {
        var s = Add(Encounter(), new AddEntry { EntityId = "e1", EntityName = "Kit", EntityHandle = "character:kit", EntitySubtype = "pc" }).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new SetOp([new SetEntry("character:kit") { Hp = 0 }])));
        Assert.Contains("give hp ≥ 1 or leave it", ex.Message, StringComparison.Ordinal);
        s = Step(s, new SetOp([new SetEntry("character:kit") { Hp = 80 }])).Next;
        Assert.Equal((80, 80), (s.Named("Kit").Hp!.Value, s.Named("Kit").MaxHp!.Value));
        s = Step(s, new SetOp([new SetEntry("kit") { Hp = 90 }])).Next;
        Assert.Equal((90, 90), (s.Named("Kit").Hp!.Value, s.Named("Kit").MaxHp!.Value));
        s = Step(s, new SetOp([new SetEntry("kit") { Hp = 40 }])).Next;
        Assert.Equal((40, 90), (s.Named("Kit").Hp!.Value, s.Named("Kit").MaxHp!.Value));
    }

    [Fact]
    public void Set_MaxHpReduction_HpDropsToTheNewMaximum_ZeroMaximumKills()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        s = Step(s, new SetOp([new SetEntry("B") { MaxHpReduction = 12 }])).Next;
        Assert.Equal((18, 18), (s.Named("B").Hp!.Value, s.Named("B").EffectiveMaxHp(E2024)!.Value));
        Assert.True(Step(s, new SetOp([new SetEntry("B") { MaxHpReduction = 30 }])).Next.Named("B").Dead);
    }

    [Fact]
    public void Set_LoggedAsAddWithUpdated_HiddenFalseReveals()
    {
        var s = Add(Encounter(), new AddEntry { Name = "Ambusher", Hp = HpChoice.Of(10), Hidden = true }).Next;
        var result = Step(s, new SetOp([new SetEntry("Ambusher") { Hidden = false, Ac = 14 }]));
        Assert.False(result.Next.Named("Ambusher").Hidden);
        var row = result.Changes.Single();
        Assert.Equal(L.Add, row.Kind);
        var updated = JsonNode.Parse(row.Detail!)!["updated"]!;
        Assert.False(updated["hidden"]!.GetValue<bool>());
        Assert.Equal(14, updated["ac"]!.GetValue<int>());
    }

    [Fact]
    public void Set_HpZero_ADeathSaveMakerFallsDying_AMonsterDies_HpUpWakes()
    {
        var s = Fight(E2024, ["P"], ("P", 20), ("M", 10));
        var down = Step(s, new SetOp([new SetEntry("P") { Hp = 0 }])).Next;
        Assert.True(down.Named("P").Dying);
        Assert.True(down.Named("P").Has("unconscious"));
        var up = Step(down, new SetOp([new SetEntry("P") { Hp = 5 }])).Next;
        Assert.False(up.Named("P").Has("unconscious"));
        Assert.Equal(DeathSaveTally.Zero, up.Named("P").DeathSaves);
        Assert.True(Step(s, new SetOp([new SetEntry("M") { Hp = 0 }])).Next.Named("M").Dead);
    }

    [Theory]
    [InlineData(E2024, 10, 30)] // 2024: exhaustion never lowers the maximum
    [InlineData(E2014, 10, 15)] // 2014 level 5 still halves it
    public void Set_HpOnTheDead_RevivesWithOneExhaustionLevelFewer_NeverStillDead(string edition, int hp, int max)
    {
        var s = Fight(edition, ["Pc"], ("Pc", 20), ("Foe", 5));
        s = Step(s, new ConditionOp(["Pc"]) { Add = ["exhaustion"], Level = 6 }).Next;
        Assert.True(s.Named("Pc").Dead);
        var result = Step(s, new SetOp([new SetEntry("Pc") { Hp = hp }]));
        var pc = result.Next.Named("Pc");
        Assert.False(pc.Dead);
        Assert.Equal((hp, 5, max), (pc.Hp!.Value, pc.Exhaustion, pc.EffectiveMaxHp(edition)!.Value));
        Assert.Equal(DeathSaveTally.Zero, pc.DeathSaves);
        Assert.Contains(result.Notes, n => n.Contains("exhaustion 6 → 5", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Reminders, r => r.Text.Contains("Exhaustion 6", StringComparison.OrdinalIgnoreCase) || r.Text.Contains(": dead", StringComparison.Ordinal));
        Assert.Equal(5, JsonNode.Parse(result.Changes.Single(c => c.Kind == L.Add).Detail!)!["updated"]!["exhaustion"]!.GetValue<int>());
    }

    [Fact]
    public void Set_HpOnTheDeadWhoseMaximumIsStill0_IsRefused_LoweringTheReductionWithItRevives()
    {
        var s = Fight(E2024, ["Pc"], ("Pc", 20), ("Foe", 5));
        s = Step(s, new SetOp([new SetEntry("Pc") { MaxHpReduction = 30 }])).Next;
        Assert.True(s.Named("Pc").Dead);
        var ex = Assert.Throws<DndInputException>(() => Step(s, new SetOp([new SetEntry("Pc") { Hp = 5 }])));
        Assert.Contains("lower max_hp_reduction", ex.Message, StringComparison.Ordinal);
        var revived = Step(s, new SetOp([new SetEntry("Pc") { Hp = 5, MaxHpReduction = 0 }])).Next.Named("Pc");
        Assert.Equal((false, 5), (revived.Dead, revived.Hp!.Value));
    }

    [Fact]
    public void Set_HpOnAnUnknownHpCombatant_ReplacesItsDamageTaken()
    {
        var s = Add(Encounter(E2024, player: true), Monster(Block(E2024, "ogre"))).Next;
        s = Step(s, new DamageOp(["ogre"]) { Amount = 20 }).Next;
        Assert.Equal(20, s.Named("Ogre").DamageTaken);
        var result = Step(s, new SetOp([new SetEntry("Ogre") { Hp = 40 }]));
        Assert.Equal((40, 0), (result.Next.Named("Ogre").Hp!.Value, result.Next.Named("Ogre").DamageTaken));
        Assert.Contains(result.Notes, n => n.Contains("20 damage taken so far is replaced", StringComparison.Ordinal));
    }

    [Fact]
    public void BadItems_EveryOneIsReported_InOneRefusal()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        var add = Assert.Throws<DndInputException>(() => Add(s, new AddEntry { Name = "X", Count = 0 }, new AddEntry { Name = "Y", Side = "sideways" })).Message;
        Assert.Contains("Invalid add (2 problems)", add, StringComparison.Ordinal);
        Assert.Contains("combatants item 1 count is 0", add, StringComparison.Ordinal);
        Assert.Contains("combatants item 2 side \"sideways\"", add, StringComparison.Ordinal);

        var set = Assert.Throws<DndInputException>(() => Step(s, new SetOp([new SetEntry("A") { Ac = 99 }, new SetEntry("B") { Hp = -1 }]))).Message;
        Assert.Contains("Invalid set (2 problems)", set, StringComparison.Ordinal);
        Assert.Contains("combatants item 1 ac is 99", set, StringComparison.Ordinal);
        Assert.Contains("combatants item 2 hp is -1", set, StringComparison.Ordinal);

        var start = Add(Encounter(), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }).Next;
        var initiative = Assert.Throws<DndInputException>(() => Step(start, new InitiativeOp { Rolls = [new("A", Face: 25), new("A", Face: 3, Total: 4)] })).Message;
        Assert.Contains("Invalid initiative (2 problems)", initiative, StringComparison.Ordinal);
        Assert.Contains("rolls item 1 face is 25", initiative, StringComparison.Ordinal);
        Assert.Contains("rolls item 2 gives both", initiative, StringComparison.Ordinal);

        var parts = Assert.Throws<DndInputException>(() => Step(s, new DamageOp(["B"]) { Parts = [new(3, "1d4", "fire"), new(2, null, "sonic")] })).Message;
        Assert.Contains("Invalid damage (2 problems)", parts, StringComparison.Ordinal);
        Assert.Contains("parts item 1 needs exactly one", parts, StringComparison.Ordinal);
        Assert.Contains("parts item 2 type \"sonic\"", parts, StringComparison.Ordinal);

        // One problem is said exactly as its check words it.
        Assert.Equal("combatants item 1 count is 0; it is 1 to 20.", Assert.Throws<DndInputException>(() => Add(s, new AddEntry { Name = "X", Count = 0 })).Message);
    }

    [Fact]
    public void Set_ALeftCombatant_IsAddressable()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        s = Step(s, new LeaveOp(["B"])).Next;
        Assert.Equal(5, Step(s, new SetOp([new SetEntry("B") { Hp = 5 }])).Next.Named("B").Hp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // leave

    [Fact]
    public void Leave_EndsItsConcentrationAndWhatItHeld_AndItsGrapples_AlreadyLeftIsANote()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        s = Step(s, new ConcentrationOp(["B"]) { Spell = "Hold Person" }).Next;
        s = Step(s, new ConditionOp(["C"]) { Add = ["paralyzed"], Duration = "concentration", Source = "B" }).Next;
        s = Step(s, new ConditionOp(["A"]) { Add = ["grappled"], Source = "B", Dc = 12 }).Next;
        var left = Step(s, new LeaveOp(["B"]));
        Assert.True(left.Next.Named("B").Removed);
        Assert.Null(left.Next.Named("B").Concentration);
        Assert.False(left.Next.Named("C").Has("paralyzed"));
        Assert.False(left.Next.Named("A").Has("grappled"));
        Assert.Contains("no effect: B left", Step(left.Next, new LeaveOp(["B"])).Notes);
    }

    [Fact]
    public void Left_EveryActionButSet_IsNoEffectForIt_NeverARefusal()
    {
        var s = Fight(E2024, ["P"], ("P", 20), ("B", 15), ("C", 10));
        s = Step(s, new LeaveOp(["P"])).Next;
        Assert.Contains("no effect: P left", Step(s, new ConcentrationOp(["P"]) { Spell = "Bless" }).Notes);
        Assert.Contains("no effect: P left", Step(s, new DeathSaveOp(["P"]) { Face = 12 }).Notes);
        Assert.Empty(CombatTracker.Needs(s, new DeathSaveOp(["P"])));
        Assert.Contains("no effect: P left", Step(s, new UseOp(["P"]) { SlotLevel = 1 }).Notes);
        Assert.Contains("no effect: P left", Step(s, new HealOp(["P"]) { Amount = 3 }).Notes);
        Assert.Contains("no effect: P left", Step(s, new ConditionOp(["P"]) { Add = ["prone"] }).Notes);
        Assert.Contains("no effect: P left", Step(s, new InitiativeOp { Rolls = [new("P", Total: 9)] }).Notes);
        Assert.Contains("no effect: P left", Step(s, new SurpriseOp(["P"])).Notes);
        var lord = Add(Encounter(E2014), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(9) }).Next;
        lord = Step(lord, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Mummy Lord", Total: 5)] }).Next;
        lord = Step(lord, new LeaveOp(["mummy-lord"])).Next;
        Assert.Contains("no effect: Mummy Lord left", Step(lord, new LegendaryOp("Mummy Lord")).Notes);
    }

    [Fact]
    public void Surprise_MarksBeforeInitiative_2024RollsWithDisadvantage()
    {
        var s = Add(Encounter(E2024), new AddEntry { Name = "A", Hp = HpChoice.Of(5), InitBonus = 1 }).Next;
        var marked = Step(s, new SurpriseOp(["A"]));
        Assert.True(marked.Next.Named("A").Surprised);
        Assert.Equal("2d20kl1+1", Assert.Single(CombatTracker.Needs(marked.Next, new InitiativeOp())).Expression);
        Assert.Contains("Disadvantage on its initiative roll", Assert.Single(marked.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Reseed_TakesTheCurrentSheet_KeepsItsPlaceInTheFight()
    {
        var s = Add(Encounter(E2014) with { Status = "planned" }, Pc("e-torch", "Lieutenant James Torch", "character:torch", Torch())).Next;
        s = Step(s, new SetOp([new SetEntry("torch") { Hidden = true, Side = "ally" }])).Next;
        var old = s.Named("Lieutenant James Torch");
        var hurt = Torch() with { Hp = 40, TempHp = 3 };
        var reseeded = CombatantFactory.Reseed(old, hurt, E2014);
        Assert.Equal((old.Id, old.Name, old.Side, old.Hidden, old.OrderKey, old.EntityHandle), (reseeded.Id, reseeded.Name, reseeded.Side, reseeded.Hidden, reseeded.OrderKey, reseeded.EntityHandle));
        Assert.Equal((40, 3), (reseeded.Hp!.Value, reseeded.TempHp));
        Assert.Equal(40L, reseeded.SheetSnapshot!.Column(SheetColumns.Hp));

        // A sheet-seeded combatant (a planned fight's) takes its conditions from the sheet as it is now, never the old ones.
        var cursed = CombatantFactory.Reseed(old, Torch() with { Conditions = [new SheetCondition("poisoned", null, "until_removed", null, null)] }, E2014);
        Assert.Empty(CombatantFactory.Reseed(cursed, Torch(), E2014).Conditions);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // addressing

    private static EncounterState Crowd()
    {
        var s = Add(Encounter(E2014), [.. PartyA()]).Next;
        return Add(s, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), Monster(Block(E2014, "mummy"), 3, HpChoice.Avg)).Next;
    }

    [Theory]
    [InlineData("Mummy 2", "Mummy 2")]
    [InlineData("mummy-2", "Mummy 2")]
    [InlineData("MUMMY  2", "Mummy 2")]
    [InlineData("mummy", "Mummy")]
    [InlineData("mummy-lord", "Mummy Lord")]
    [InlineData("character:torch", "Lieutenant James Torch")]
    [InlineData("torch", "Lieutenant James Torch")]
    [InlineData("belmakor", "Belmakor Silverwind")]
    [InlineData("Lieutenant", "Lieutenant James Torch")]
    [InlineData("vars", "Vars Nocturne")]
    public void Resolve_ExactThenHandleOrSlugThenUniquePrefix(string address, string name)
    {
        Assert.Equal(name, CombatAddressing.Resolve(Crowd().Combatants, address, "targets").Name);
    }

    [Fact]
    public void Resolve_AmbiguousPrefix_IsRefusedListingThem()
    {
        var ex = Assert.Throws<DndInputException>(() => CombatAddressing.Resolve(Crowd().Combatants, "Mum", "targets"));
        Assert.Contains("Mummy Lord, Mummy, Mummy 2, Mummy 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Star_EveryLivingCopy_NeverALeftOrDeadOne()
    {
        var s = Crowd();
        s = Step(s, new DamageOp(["mummy-3"]) { Amount = 999 }).Next;
        s = Step(s, new LeaveOp(["mummy-2"])).Next;
        Assert.Equal(["Mummy"], CombatAddressing.ResolveAll(s.Combatants, "mummy*", "targets").Select(c => c.Name));
        Assert.Throws<DndInputException>(() => CombatAddressing.ResolveAll(s.Combatants, "nobody*", "targets"));
    }

    [Fact]
    public void Resolve_ALeftCombatant_ByExactNameOrHandle_NeverByPrefix()
    {
        var s = Step(Crowd(), new LeaveOp(["torch"])).Next;
        Assert.Equal("Lieutenant James Torch", CombatAddressing.Resolve(s.Combatants, "character:torch", "targets").Name);
        Assert.Equal("Lieutenant James Torch", CombatAddressing.Resolve(s.Combatants, "Lieutenant James Torch", "targets").Name);
        Assert.Throws<DndInputException>(() => CombatAddressing.Resolve(s.Combatants, "Lieutenant", "targets"));
    }

    [Fact]
    public void Resolve_CrossCampaignHandle_IsRefused_UnknownNamesTheCombatants()
    {
        var ex = Assert.Throws<DndInputException>(() => CombatAddressing.Resolve(Crowd().Combatants, "belmakor/character:keras", "targets"));
        Assert.Contains("another campaign", ex.Message, StringComparison.Ordinal);
        var unknown = Assert.Throws<DndInputException>(() => CombatAddressing.Resolve(Crowd().Combatants, "Keras", "targets"));
        Assert.Contains("is not a combatant in this fight", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMany_TargetsListedTwiceOnce_EmptyRefused()
    {
        var s = Crowd();
        Assert.Single(CombatAddressing.ResolveMany(s.Combatants, ["torch", "character:torch"], "targets"));
        Assert.Throws<DndInputException>(() => CombatAddressing.ResolveMany(s.Combatants, [], "targets"));
    }

    [Fact]
    public void SingleTargetActions_SeveralTargets_AreRefused()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        Assert.Throws<DndInputException>(() => Step(s, new ConcentrationOp(["A", "B"]) { Spell = "Bless" }));
        Assert.Throws<DndInputException>(() => Step(s, new DeathSaveOp(["A", "B"]) { Face = 10 }));
    }
}
