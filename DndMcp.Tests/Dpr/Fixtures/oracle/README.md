# DPR oracle: independent brute-force cases for the Phase 4 engine

`oracle.py` computes the damage-per-round semantics of the Phase 4 build contract (§4) by a method deliberately
unlike the C# engine's, and writes `cases.json`: 292 builds with their exact expected results, 33 derived deltas and a
reference block of non-DPR goldens. The 31 `fix-*` cases were added in Phase 5 for the Phase 4 review's rules fixes (see
"Readings changed by the Phase 4 review" at the end); every earlier case kept its numbers exactly. The C# tests (agent E) run each case through `DprEngine` and must agree.

The oracle was written from the contract and the research notes only. It does not read the C# engine or agent F's
resolver, so a shared misreading is the only way the two can agree while both are wrong. Every place the contract left
a reading open is listed below. Where the other reading changes a number, the case carries it under `alternatives`.

## Rerun

```bash
python3 DndMcp.Tests/Dpr/Fixtures/oracle/oracle.py           # rewrites cases.json (about 1 s)
python3 DndMcp.Tests/Dpr/Fixtures/oracle/oracle.py --check   # exit 1 if cases.json is stale or a golden fails
```

The script needs Python 3.8 or later and uses only the standard library (`fractions`, `itertools`, `json`, `math`,
`re`, `argparse`). The output is deterministic: the same bytes on every run. Commit `oracle.py` and `cases.json`
together. A failed §8 golden is printed as `GOLDEN MISMATCHES:` and makes the script exit 1, but the file is still
written.

## Method

The C# engine is a forward dynamic program over a compact per-turn state. The oracle does not use one.

- **d20:** every face of every die is enumerated: 20^k combinations for advantage, disadvantage and Elven
  Accuracy, and with Lucky every reroll of one die that shows a natural 1. Each kept face is then crossed with every
  bonus-die value. Nothing uses a closed form such as `1-(1-p)^2`; the crit floor, Lucky with disadvantage and Bless
  come straight out of the counts.
- **Damage:** each die is remapped before summing. GWF 2014 uses (1/M)[f≥3] + (2/M)(1/M), GWF 2024 maps 1–2 to 3,
  and Elemental Adept maps 1 to 2. The dice are then convolved directly. Types are kept apart until each has been
  halved, resisted, doubled or zeroed and floored at 0. Savage Attacker uses P(max = x) = F(x)² − F(x−1)².
- **Turn:** each turn is an explicit outcome tree. It branches on miss, normal hit or crit for every attack roll, on
  fail or success for every Topple and condition save, and on whether a Cleave partner is present. A Vex hit also
  branches on whether it dealt damage. Every decision is an exhaustive expected-value search over the rest of the
  tree:
  - optional riders under each policy, including `optimal` with `use_value`;
  - the power-attack on/off combination;
  - the Bonus Action option.

  There is no memoised state table. The forward pass under the chosen decisions gives the exact turn distribution.
- **Fight:** only the carried state (Vex pending and resource uses left) passes from one turn to the next, so a
  fight chains the per-turn trees as a Markov chain over that state.
- **Day:** the §4.4 formula is applied to fight-horizon runs.
- **Arithmetic:** exact rationals (`fractions.Fraction`) throughout. Each JSON number is given both as the exact
  fraction and as its nearest double.

## `cases.json`

```jsonc
{
  "schema": "dnd-mcp/dpr-oracle-cases", "schema_version": 1, "generated_by": "...", "notes": "...",
  "cases": [ {
    "id": "gwm14-pa-auto-crit-ba-ac15",            // unique, stable
    "description": "...", "contract_refs": ["§4.2", "§8.2"],
    "build":   { ... },                             // BuildSpec DSL JSON, contract §3 field names exactly
    "target":  { ... } | null,                      // TargetSpec; null = the DMG CR = level row
    "rulings": { ... } | null,                      // RulingsSpec; null = all false
    "horizon": { "kind": "round1" | "fight" | "day", "rounds": R | null,
                 "encounters_per_day": E | null, "short_rests": S | null, "rest_preset"?: "dmg2014" | "light" },
    "expected": {
      "dpr": { "exact": "num/den", "value": 19.611625 },      // always
      "p_zero"?:         { exact, value },   // round1: P(turn damage = 0)
      "p_at_least_hp"?:  { exact, value },   // round1 with target.hp, attack builds: P(turn damage >= hp)
      "pmf"?: [[damage, "num/den"], ...],    // round1, support <= 48 values (the whole turn, reaction included)
      "riders"?: [ { "name", "expected_damage", "uses_per_round", "damage_per_use" } ],  // optional riders + Savage Attacker
      "power_attack"?: [ { "probability": "num/den", "on": bool } ],  // one row per advantage-source sample
      "save"?: { "targets", "dc", "per_target_fail", "expected_damage_per_target", "raw",
                 "effective"?, "p_each_dies"?, "p_all_die"?, "expected_kills"?, "kill_distribution"?: [[k, "num/den"]],
                 "expected_casts_to_land"? },
      "day"?: { "fight_dpr_without_limited_features", "features": [ { "name", "uses_per_day",
                "uses_per_round_unlimited", "marginal_damage_per_use", "contribution" } ] },
      "alternatives"?: [ { "reading": "...", "dpr": { exact, value } } ],
      "notes"?: "..."
    } } ],
  "deltas":    [ { "id", "plus", "minus", "kind": "difference" | "relative", "exact", "value", "golden" } ],
  "reference": { "d20_at_plus7_vs_ac15", "save_fail", "legendary_resistance_casts_f06_l3", "gwf_expected_per_die",
                 "savage_attacker_table", "vex_steady_state", "rpgbot_target_by_level",
                 "fireball_independent_rolls_wrong_model", "typical_save_bonus_by_cr",
                 "power_attack_toggle_rule_2014_l5" }
}
```

Each `dpr` means the following for its horizon:

| Horizon | `dpr` |
|---|---|
| round1 | E[the turn's damage], including the round's reaction |
| fight | E[total over R rounds] ÷ R |
| day | the §4.4 formula |

For a save effect, `dpr` is the total across all of the effect's targets. `rest_preset` appears when E and S are a
preset's numbers. The light preset uses E = 3.5; a custom integer E cannot express it.

### How the C# tests should consume it

- Copy the file to the test output directory. Agent O may not edit the csproj, so the entry has to be added by
  agent E or H:
  `<None Include="Dpr\Fixtures\oracle\cases.json" CopyToOutputDirectory="PreserveNewest" />`.
- Bind `build`, `target` and `rulings` with `DslJson.Options` into `BuildSpec`, `TargetSpec` and `RulingsSpec`, and
  evaluate at `build.level` with the horizon given.
- Compare `dpr`, `riders`, `save` and `day` to `value` within 1e-9, since the engine computes in doubles.
  Compare each `pmf` weight within 1e-12.
- One tie case is exact: `policy-first-hit-optimal-lambda-9`, where E[2d8] − 9 = 0 against a hold value of 0.
  The contract says ties do not spend.
- `alternatives` is not a second right answer. It shows what the other reading gives. If the engine matches an
  alternative, that is a semantic decision to record in PLAN's Phase 4 status and to settle on purpose. Do not
  switch the test to the alternative quietly.
- `deltas` are differences, or ratios minus 1 for `relative`, between two cases' `dpr`. Several are §8 goldens.

## §8 goldens: all reproduce

| § | Golden | Where | Result |
|---|---|---|---|
| 8.1 | crit floor 1.2 | `d20-crit-floor-golden` | 6/5 exactly |
| 8.2 | 19.611625; 253/15; 15.00; 18.70; 19.50; Δ +2.745, +0.1116; PA on at AC ≤ 16, off at ≥ 17; both AC 13–19 columns | `gwm14-*`, deltas `gwm14-feat-*`, `reference.power_attack_toggle_rule_2014_l5` | all match. The turn-level `auto` choice agrees with the per-attack rule at every AC |
| 8.3 | 24.036; 24.226125; 19.20; 22.00; Δ +4.836 / +2.036; GWF 2024 +1.40, GWF 2014 +1.8667, Graze +2.80; AC 13–19 column; GWM − ASI column | `gwm24-*`, deltas `gwm24-*` | all match (components: see note A) |
| 8.4 | Savage Attacker +0.798958 (ruling off) / +0.825250 (on); ruling-off AC column 0.8536…0.6351; ruling-on column 0.88…0.67; SA table | `gwm24-savage-attacker-*`, deltas `savage-attacker-gain-*`, `reference.savage_attacker_table` | all match |
| 8.5 | GWF per die, plain / 2014 / 2024 | `reference.gwf_expected_per_die` | exact fractions match |
| 8.6 | Vex steady state 0.841424 | `reference.vex_steady_state` (closed form plus 200 iterations of the chain) | 260/309 = 0.8414239… |
| 8.7 | smite 8.505 / 6.885 / 1.755 / 12.6; uses 0.8775 / 0.6675 / 0.0975 / 1.3; per use 9.692 / 10.315 / 18.0; optimal boundaries at 54/7 and 9 (then 18) | `policy-*` | all match on both sides of each boundary |
| 8.8 | Fireball: F 0.6, raw 89.2, effective 27.998608, P(each dies) 0.9996935, P(all 4 die) 0.999333059, E[kills] 3.998774, wrong independent-roll model 0.998775; 2024 goblin warriors 0.9844489 / 0.9661672 / 39.908441 | `save-fireball-goblins-2014`, `save-fireball-goblin-warriors-2024`, `reference.fireball_independent_rolls_wrong_model` | all match (P(all) = 3642569/3645000) |
| 8.9 | Legendary Resistance casts 6.6667 | `save-legendary-resistance-hold-person`, reference | 20/3 |
| 8.10 | advantage +52.7% / +37.8% / +22.9%; +1 to hit +9.4% / +7.3% / +6.0% | `calibration-*`, deltas `calibration-*-gain` | all match |
| 8.11 | warlock baseline, levels 1–20; RPGBOT target | `warlock-baseline-l01..l20`, `reference.rpgbot_target_by_level` | all 20 match exactly |
| 8.12 | Action Surge +3.254 (E 6), +2.440 (E 8); 2024 +3.991 | `horizon-action-surge-day-*` | +3.25379 / +2.44034 / +3.99079. The marginal per use is 62472773/3200000 = 19.5227 |

§2's d20 and save goldens are in `reference.d20_at_plus7_vs_ac15` and `reference.save_fail`, and all match.

**Note A:** the GWF and Graze components (+1.40, +1.8667, +2.80) hold for the two Attack-action swings of build 3
**without Hew**, so the cases are `gwm24-no-hew-*`. On the full build 3 each component is 0.0975 × (its per-swing
value) larger: GWF 2024 +1.46825, Graze +2.9365.

## Semantic readings the contract left open

Each item gives the reading `cases.json` uses, the cases it affects, and the alternative's value where one was
computed. Items 1–3 change numbers in committed cases. The rest are either pinned by a case or avoided by every case.

1. **Decision scope in a fight.** An `optimal` rider and the Bonus Action choice maximise
   **the current turn's** E[damage] − λ·uses. `use_value` prices the resource and later rounds are ignored, which
   matches "backward induction over the full state" read as the turn's state. Power attack `auto` maximises the turn's
   E[damage] as §4.2 says.
   - The other reading is full-fight backward induction, where uses left and a pending Vex have a value in later
     rounds.
   - `horizon-smite-every-hit-fight-r3-optimal` gives 20.7348826 per turn and 20.7582679 as the alternative.
   - No other fight case changes.
2. **Cleave's `second_target_rate`.** The second creature's presence is sampled **once per turn**, at the first melee
   hit with the Cleave weapon. The Cleave is then used whether or not a creature was there. This is research A5's
   `p_adj · P(≥1 eligible hit) · E[attack]`.
   - The other reading draws again on every melee hit with the weapon until a Cleave happens.
   - Every `mastery-cleave-*` case with a rate strictly between 0 and 1 carries it, for example rate 0.5:
     16.2963125 against 16.7769063.
3. **The reaction attack in the round1 horizon.** "Each round" is read as including round 1's reaction.
   - Alternative: `econ-reaction-opportunity-attack` 18.34 → 13.10, and `econ-reaction-no-resources`
     26.975 → 24.10.
   - `p_zero` and `pmf` include the reaction.
4. **What the reaction attack sees.** It gets fresh once-per-turn riders. It spends no resources: riders with a
   `resource` are skipped. It uses no advantage-rate sources and none of the turn's Vex or applied conditions, but the
   target's initial `condition` applies. Only `always` power attacks are on. It cannot Cleave, Topple or apply
   conditions. "Best single" means the highest `trigger_probability × E[damage]`.
5. **`crit_or_last`: which attack is "last".** It is the last attack in the current phase's queue that the rider could
   apply to. For the Action that queue holds the Action's attacks plus any Action Surge attacks; for a Bonus Action
   option it holds that option's attacks. It does not look ahead to Bonus Action attacks, Hew or a possible Cleave.
   Cases use `crit_or_last` only on pure Action sequences.
6. **`condition_on_hit` is not attempted, and spends no use, while the target already has that condition.** This
   changes only the use counts. No case reports uses for condition riders.
7. **An `extra_damage` rider with no `type` deals the attack's damage type**, like Sneak Attack. Treating it as
   typeless would matter only against resistances. Every case that has adjustments gives its riders an explicit type.
8. **`on_crit` riders are added once even with `crit_doubles: true`**, which is the default. §4.1 says so; it is listed
   because the default invites the opposite. Pinned by `dmg-on-crit-rider-added-once`.
9. **"A Vex hit that deals damage"** means the hit's total damage after adjustments is above 0. With 1d4−1 a normal
   hit rolling 1 deals 0 and grants nothing. Pinned by `mastery-vex-needs-damage` (2.43).
10. **The Cleave attack and turn state.** It neither consumes nor grants Vex. Neither the target's initial condition
    nor the turn's applied conditions apply to the second creature. Advantage-rate sources present that turn still
    apply to it, following their `attacks` filter. A power attack switched on applies. Its crit counts as a
    melee-weapon crit for a `crit` trigger (Hew). Its hits take every-hit riders and still-unspent once-per-turn riders
    per their policy, as §4.1 says (pinned). Cases avoid the other interactions.
11. **Graze and `on_miss` riders on one miss** are one damage instance: summed per type, then adjusted.
12. **Power attack `auto`.** It is decided per advantage sample and carried state, before any roll. It covers every
    filtered attack of the turn, including the bonus-action and Hew attacks, which golden 8.2 requires. A tie leaves it
    off. The objective is E[damage] without λ; no case mixes `auto` with a `use_value`.
13. **Several optional riders on one hit.** `optimal` riders are decided jointly by searching every subset of them,
    and on a tie the smallest subset wins. At most one Bonus-Action-costing rider is spent per turn; among
    fixed-policy ones, the first listed wins.
14. **Bonus Action choice.** The option with the highest turn objective wins. A tie goes to the first option in this
    order:
    1. all `bonus_action` attacks together, as one option;
    2. each available `extra_attack` with `action: bonus_action`, in modifier order;
    3. each `bonus_action` save effect;
    4. nothing.
15. **"Hew" for `hew_gets_pb`** is any `extra_attack` with `action: bonus_action`, whatever its trigger. It gains
    `bonus_damage` marked `attack_action_only`, but never `extra_damage` marked `attack_action_only`, as §4.2 says.
    Pinned by `gwm24-hew-gets-pb-not-riders` (29.126125).
16. **Action Surge.** One use per turn from round 1 while uses last, so with unlimited uses it happens every turn;
    that unlimited case is what the day formula's u = 1 needs. It is also used in the round1 horizon.
    - Its attacks follow the Action's and count as part of the Attack action.
    - It would still be granted if a `setup: action` took round 1's Action; no case has that.
17. **Setup.** A modifier with `setup` is active for the attacks of the round in which it is set up, because setup
    happens first. Pinned by `horizon-hex-setup-round1` (7.5) and `-fight-r3` (10.7667).
18. **A save effect's `condition`** has no in-turn effect on later attacks. A damaging effect's damage ignores
    Legendary Resistance: saves are taken as rolled, pinned by `save-damage-effect-ignores-legendary-resistance`.
    `expected_casts_to_land` is reported only when there is a condition and LR > 0.
19. **Elemental Adept** applies to every die of its type everywhere: weapon dice, rider dice whatever
    `gwf_on_riders` says, and save-effect dice. It ignores its `attacks` filter. If GWF and EA hit the same die, GWF
    is applied first and EA to the final face; no case does this.
20. **A `damage_die_remap` modifier (GWF) with no `attacks`** applies to every attack. The cases always filter it or
    use the `fighting_style` sugar.
21. **Day horizon.** Each limited feature is evaluated alone on the base, with every other limited feature removed
    and its own uses unlimited.
    - u = E[uses over the fight] ÷ R.
    - d = (fight DPR with the feature − fight DPR of the base) ÷ u.
    - A feature with u = 0 contributes 0.
    - U = uses for `long_rest`, or uses × (S + 1) for `short_rest`.
    - E may be fractional (the light preset's 3.5).
22. **Unconscious is not also treated as prone** (the contract lists them separately), so a ranged attack against an
    unconscious target has advantage. (The C# engine reads Unconscious as Prone too, per both editions' condition text;
    no case has a ranged attack against an unconscious target, so no number depends on this reading.)
23. **Topple is always attempted** on a hit against a target that is not prone, even though prone gives ranged
    attacks disadvantage. No case mixes Topple with ranged attacks. Its DC uses the to-hit ability even with `total`.
    The target's `save_dice` apply, and the save is not magical.
24. **Cover.** It adds +2 / +5 to Dex saves against save effects and against `condition_on_hit`. `ignore_cover`
    affects only attack rolls.
25. **An `ac`-only target** takes its save bonus from the CR = level row's typical value, as §3.5 says. All
    save-dependent cases give `saves` explicitly.
26. **Graze uses the attack's ability modifier** even when `ability_to_damage` is false.
27. **A rider's `expected_damage`** is its marginal damage on the hits where it is spent, which equals E[rider damage]
    when its type is not adjusted. Savage Attacker's is E[max] − E[one roll] on those hits.
28. **Multi-type save effects** cannot be expressed: `save_effect` has one `type`. The per-type code is exercised by
    attack hits (`dmg-multitype-*`).
29. **Lucky** is not actually open. Rerolling one natural 1 whenever any die shows one gives the same kept-face
    distribution as rerolling only when it can help, because rerolling a 1 never lowers the max, or the min when the
    other die is higher. §2's closed forms (1.1·p² and so on) come out of the enumeration.

## Readings changed by the Phase 4 review (Phase 5)

The Phase 4 review (rules lens, each finding adversarially verified) found five places where the contract's first
reading was not the rules. The oracle now follows the fixed reading; no case that existed before changed its numbers
(`cases.json` was regenerated and compared), and each new reading is pinned by `fix-*` cases with hand-worked goldens.

30. **The Attack action is weapon attacks only.** A spell attack made with the Action (Fire Bolt, Eldritch Blast) is
    the spell's casting action, not the Attack action (2024 "Attack [Action]: an attack roll with a weapon or an
    Unarmed Strike"): `attack_action_only` flat bonuses and riders skip it, and it does not enable the offhand attack.
    Pinned by `fix-attack-action-only-skips-spell-attacks` (7.70).
31. **Dodge is lost** while the target is Incapacitated (stunned, paralyzed, unconscious) or its Speed is 0
    (restrained), in both editions ("You lose this benefit if you are incapacitated … or if your speed drops to 0").
    A blinded dodger cannot see its attacker, so attack rolls lose Dodge's Disadvantage, but its Dex saves keep the
    Advantage. Prone keeps Dodge. Pinned by `fix-dodge-lost-*` and `fix-dodge-dex-save-*` (9.0196375, 10.5026125,
    8.11231875, 9.2305, 7.2025).
32. **2024 Evasion does not work while the creature is Incapacitated** ("You don't benefit from this feature if you
    have the Incapacitated condition"); 2014 Evasion has no such clause. The conditions read are the creature's own:
    the target spec's, plus for the main target those imposed on it this turn. Pinned by `fix-evasion-*`.
33. **A save effect's targets.** The first target is the main target (the one the turn's attacks hit): a condition
    imposed on it this turn changes its save (and its Evasion) only; the other creatures in the area have the target
    spec's condition alone. Given the shared roll x, the main target fails with its own chance and the other n − 1
    fail binomially with theirs. Pinned by `fix-area-save-main-target-*` and `fix-evasion-stunned-this-turn-*`.
34. **The offhand attack needs the Attack action** that turn (2014 Two-Weapon Fighting "When you take the Attack
    action …", 2024 Light "When you take the Attack action on your turn …"): taken when the Action makes at least one
    weapon attack, or when an Action Surge makes weapon attacks. Not after a `setup: action`, not beside an Action save
    effect. Other `bonus_action` attacks (Spiritual Weapon) are unaffected. Pinned by `fix-offhand-*` (0, 14.0375,
    9.358333…, 3.7125, 22.3).
35. **Archery and Dueling read the weapon's category**: an attack with `ranged` and `thrown` is a melee weapon thrown
    (javelin, dagger, handaxe), so Archery does not apply and Dueling does. Pinned by `fix-archery-skips-thrown-*`
    (10.10) and `fix-dueling-takes-thrown-*` (12.70), both editions.
36. **`trigger: crit_or_kill`** (a DSL addition for the simulator) is `crit` here: the closed form has no hit points to
    reduce to 0. Pinned by `fix-crit-or-kill-is-crit-in-closed-form` (19.611625).
37. **Reading 7 is now the engine's too**: the C# engine used to treat an untyped rider as typeless; it now deals the
    attack's type, as this oracle always did. Pinned by `fix-untyped-rider-takes-attack-type` (6.3875; 10.06 was the
    typeless figure).

