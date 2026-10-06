using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign.Combat;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// The exit-criteria fights (FIX §2.2 and §4.2 with contract §16's call shapes) through the Repository's
/// <see cref="CombatService"/>, each call its own transaction, as the combat tool will send them. Fixture A gives every value
/// (no server roll); fixture B's three server rolls take the scripted faces of FIX §4.1 ([5], [4, 5, 2, 3], [3, 2]). Each
/// outcome is kept by its FIX step number. X (stage 4) asserts every step; these tests assert what the Repository adds:
/// persistence, dice, the write-back.
/// </summary>
internal static class CombatScripts
{
    public const string CryptName = "The crypt (fixture)";

    public const string StationName = "The dark station (fixture)";

    /// <summary>Fixture A, A0-A31 (session 4 live; the fight not ended), or up to and including <paramref name="until"/>.</summary>
    public static Dictionary<string, CombatOutcome> FixtureA(CombatWorld w, string? until = null)
    {
        w.StartSession(4);
        w.Reload();
        return Run(StepsA(w), until);
    }

    /// <summary>Runs steps in order, keeping each outcome, and stops after <paramref name="until"/>.</summary>
    public static Dictionary<string, CombatOutcome> Run(IEnumerable<(string Step, Func<CombatOutcome> Call)> steps, string? until)
    {
        var at = new Dictionary<string, CombatOutcome>(StringComparer.Ordinal);
        foreach (var (step, call) in steps)
        {
            at[step] = call();
            if (step == until)
            {
                break;
            }
        }

        return at;
    }

    /// <summary>
    /// Fixture A's steps A1-A31 in order, each a call not yet made (the session must already be live: <see cref="FixtureA"/>
    /// starts it). X's scenarios walk them one at a time to read the store and every view between steps.
    /// </summary>
    public static IEnumerable<(string Step, Func<CombatOutcome> Call)> StepsA(CombatWorld w)
    {
        var c = w.Campaign;
        var s = w.Combat;
        yield return ("A1", () => s.Start(c, new StartRequest { Name = CryptName }));
        yield return ("A2", () => s.Add(c, null, [CombatWorld.Monster("2014", "mummy-lord", hp: HpChoice.Avg, side: "enemy"), CombatWorld.Monster("2014", "mummy", 2, HpChoice.Avg, side: "enemy")]));
        yield return ("A3", () => s.Initiative(c, null, new InitiativeOp
        {
            Rolls =
            [
                new("belmakor", Face: 17), new("mummy-lord", Face: 18), new("mummy", Face: 10), new("vars", Total: 25),
                new("serif", Total: 16), new("ignis", Total: 14), new("torch", Total: 12), new("aiden-ironstar", Total: 7),
            ],
        }));
        yield return ("A4", () => s.Next(c, null));
        yield return ("A5", () => s.Condition(c, null, new ConditionOp(["belmakor"]) { Add = ["Bladesong"], Effect = new EffectInput(Ac: 5), Duration = "1 minute", Resource = "Bladesong" }));
        yield return ("A6", () => s.Concentration(c, null, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5, Duration = "10 minutes" }));
        yield return ("A7", () => s.Next(c, null));
        yield return ("A8", () => s.Damage(c, null, new DamageOp(["belmakor"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }));
        yield return ("A9", () => s.Concentration(c, null, new ConcentrationOp(["belmakor"]) { Total = 20 }));
        yield return ("A10", () => s.Next(c, null));
        yield return ("A11", () => s.Next(c, null));
        yield return ("A12-legendary", () => s.Legendary(c, null, new LegendaryOp("mummy-lord") { Amount = 1, Name = "Attack (Rotting Fist)" }));
        yield return ("A12", () => s.Damage(c, null, new DamageOp(["torch"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }));
        yield return ("A13", () => s.Next(c, null));
        yield return ("A14", () => s.Damage(c, null, new DamageOp(["mummy-lord", "mummy", "mummy-2"]) { Parts = [new(28, null, "fire")] }));
        yield return ("A15", () => s.Next(c, null));
        yield return ("A16", () => s.Condition(c, null, new ConditionOp(["torch"]) { Add = ["frightened"], Source = "mummy", Duration = "until_end_of_source_turn" }));
        yield return ("A17", () => s.Damage(c, null, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy" }));
        yield return ("A18-next", () => s.Next(c, null));
        yield return ("A18", () => s.Damage(c, null, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy-2" }));
        yield return ("A19-aiden", () => s.Next(c, null));
        yield return ("A19", () => s.Next(c, null));
        yield return ("A20", () => s.Next(c, null));
        yield return ("A21", () => s.Damage(c, null, new DamageOp(["mummy-2"]) { Parts = [new(8, null, "slashing")], Magical = true }));
        yield return ("A22", () => s.Next(c, null));
        yield return ("A23", () => s.Damage(c, null, new DamageOp(["torch"]) { Parts = [new(25, null, "bludgeoning"), new(42, null, "necrotic")], Critical = true, Source = "mummy-lord" }));
        yield return ("A24-serif", () => s.Next(c, null));
        yield return ("A24-ignis", () => s.Next(c, null));
        yield return ("A24", () => s.Next(c, null));
        yield return ("A25", () => s.DeathSave(c, null, new DeathSaveOp(["torch"]) { Face = 20 }));
        yield return ("A26", () => s.Damage(c, null, new DamageOp(["mummy-lord"]) { Parts = [new(16, null, "fire")] }));
        yield return ("A27", () => s.Next(c, null));
        yield return ("A28", () => s.Next(c, null));
        yield return ("A28a-prev", () => s.Prev(c, null));
        yield return ("A28a-next", () => s.Next(c, null));
        yield return ("A29", () => s.Damage(c, null, new DamageOp(["mummy-lord"]) { Parts = [new(12, null, "radiant")] }));
        yield return ("A30", () => s.Next(c, null));
        yield return ("A31", () => s.Damage(c, null, new DamageOp(["mummy"]) { Parts = [new(10, null, "piercing")], Magical = true }));
    }

    /// <summary>Fixture A's end (A33).</summary>
    public static CombatEndOutcome EndA(CombatWorld w, bool dryRun = false) =>
        w.Combat.End(w.Campaign, null, new EndRequest { Outcome = "The crypt is cleared.", DryRun = dryRun });

    /// <summary>
    /// Fixture B, B0-B26 (session 13 live, lair; the fight not ended), or up to and including <paramref name="until"/>.
    /// Pushes FIX §4.1's faces (all of them: a test that stops early leaves the rest queued).
    /// </summary>
    public static Dictionary<string, CombatOutcome> FixtureB(CombatWorld w, string? until = null)
    {
        w.StartSession(13);
        w.Reload();
        w.Roller.Push(5, 4, 5, 2, 3, 3, 2);
        return Run(StepsB(w), until);
    }

    /// <summary>
    /// Fixture B's steps B1-B26 in order, each a call not yet made (the session must already be live and FIX §4.1's faces
    /// queued: <see cref="FixtureB"/> does both). X's scenarios walk them one at a time to read the store and every view
    /// between steps.
    /// </summary>
    public static IEnumerable<(string Step, Func<CombatOutcome> Call)> StepsB(CombatWorld w)
    {
        var c = w.Campaign;
        var s = w.Combat;
        yield return ("B1", () => s.Start(c, new StartRequest { Name = StationName, Lair = true }));
        yield return ("B2", () => s.Add(c, null, [CombatWorld.Monster("2024", "aboleth", hp: HpChoice.Avg, side: "enemy", character: "character:the-nester")]));
        yield return ("B3", () => s.Condition(c, null, new ConditionOp(["bjorn-mountainfell"]) { Add = ["exhaustion"], Level = 1 }));
        yield return ("B4", () => s.Initiative(c, null, new InitiativeOp
        {
            Rolls = [new("fishman-monk", Face: 16), new("dragon-slayer", Face: 9), new("bjorn-mountainfell", Face: 8), new("aboleth", Total: 13)],
        }));
        yield return ("B5-focus", () => s.Use(c, null, new UseOp(["fishman-monk"]) { Resource = "Focus", Amount = 2 }));
        yield return ("B5", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Amount = 18, DamageType = "bludgeoning" }));
        yield return ("B5-resistance", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Resistance = true }));
        yield return ("B6-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
        yield return ("B6", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B7", () => s.Next(c, null));
        yield return ("B8-monk", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B8-grapple-monk", () => s.Condition(c, null, new ConditionOp(["fishman-monk"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }));
        yield return ("B8-bjorn", () => s.Damage(c, null, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B8-grapple-bjorn", () => s.Condition(c, null, new ConditionOp(["bjorn-mountainfell"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }));
        yield return ("B8", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
        yield return ("B9", () => s.Condition(c, null, new ConditionOp(["fishman-monk"]) { Add = ["cursed (Mucus Cloud)"], Source = "aboleth", Duration = "until removed" }));
        yield return ("B10-next", () => s.Next(c, null));
        yield return ("B10", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Parts = [new(22, null, "bludgeoning"), new(4, null, "psychic")] }));
        yield return ("B11-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
        yield return ("B11", () => s.Damage(c, null, new DamageOp(["dragon-slayer"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B12-next", () => s.Next(c, null));
        yield return ("B12-rage", () => s.Condition(c, null, new ConditionOp(["bjorn-mountainfell"]) { Add = ["Rage"], Effect = new EffectInput(Resist: ["all"], Except: ["psychic"]), Resource = "Rage" }));
        yield return ("B12", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Amount = 24, DamageType = "slashing" }));
        yield return ("B13-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
        yield return ("B13", () => s.Damage(c, null, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B14", () => s.Next(c, null));
        yield return ("B15-focus", () => s.Use(c, null, new UseOp(["fishman-monk"]) { Resource = "Focus", Amount = 2 }));
        yield return ("B15", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Amount = 20, DamageType = "bludgeoning" }));
        yield return ("B15-resistance", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Resistance = true }));
        yield return ("B16-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Psychic Drain" }));
        yield return ("B16-damage", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
        yield return ("B16", () => s.Heal(c, null, new HealOp(["aboleth"]) { Dice = "1d10" }));
        yield return ("B17", () => s.Next(c, null));
        yield return ("B18-first", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B18-second", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B18", () => s.Damage(c, null, new DamageOp(["bjorn-mountainfell"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
        yield return ("B19-next", () => s.Next(c, null));
        yield return ("B19", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Parts = [new(24, null, "bludgeoning"), new(4, null, "psychic")] }));
        yield return ("B20-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
        yield return ("B20", () => s.Damage(c, null, new DamageOp(["fishman-monk"]) { Dice = "2d6+5", DamageType = "bludgeoning", Critical = true, Source = "aboleth" }));
        yield return ("B21", () => s.Next(c, null));
        yield return ("B22", () => s.Heal(c, null, new HealOp(["fishman-monk"]) { Dice = "2d4+2", Source = "bjorn-mountainfell", Item = "Potion of Healing" }));
        yield return ("B23", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Amount = 26, DamageType = "slashing" }));
        yield return ("B24-legendary", () => s.Legendary(c, null, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
        yield return ("B24", () => s.Damage(c, null, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
        yield return ("B25", () => s.Next(c, null));
        yield return ("B26", () => s.Damage(c, null, new DamageOp(["aboleth"]) { Amount = 18, DamageType = "bludgeoning" }));
    }

    /// <summary>Fixture B's end (B28, contract §16): the ledger to the party, the potion to Björn, 120 gp to the party.</summary>
    public static CombatEndOutcome EndB(CombatWorld w, bool dryRun = false, bool force = false) =>
        w.Combat.End(w.Campaign, null, new EndRequest
        {
            Outcome = "The sixth station is clear.",
            Loot =
            [
                new LootRequest("The sixth station's ledger") { To = "faction:the-party" },
                new LootRequest("Potion of Water Breathing") { Srd = "2024/magic-item/potion-of-water-breathing", To = "character:bjorn-mountainfell" },
            ],
            Currency = [new CurrencyRequest { To = "faction:the-party", Gp = 120 }],
            DryRun = dryRun,
            Force = force,
        });
}
