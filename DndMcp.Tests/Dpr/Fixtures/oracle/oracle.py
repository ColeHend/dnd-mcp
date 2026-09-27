#!/usr/bin/env python3
"""Independent brute-force oracle for the dnd-mcp Phase 4 DPR engine (build contract section 4).

Why this exists
---------------
The C# engine (DndMcp.Domain/Dpr) computes a turn as a forward dynamic program over a compact state. A bug in that
state (a once-per-turn rider that is never reset, a Vex flag that is not consumed, a crit that is not a hit, an
Action Surge that is spent twice) gives plausible numbers that a handful of hand-written goldens will not catch.
This oracle computes the same quantities by a deliberately different method, so the two can only agree when both
are right:

* d20 odds by enumerating every die face: all 20^k combinations of advantage / Elven Accuracy dice, every Lucky
  reroll, every bonus-die value. No closed forms.
* damage by direct convolution of per-die face distributions, remaps applied to each die before summing.
* every turn as an explicit outcome tree: miss / normal hit / crit per attack roll, fail / success per saving throw,
  Cleave partner present or not, a Vex hit that deals damage or not. Every decision (rider policies, `optimal` with
  `use_value`, power attack `auto`, the Bonus Action choice) is made by exhaustive expected-value search over the
  remaining tree; there is no memoised state and no backward-induction table.
* exact rational arithmetic throughout (fractions.Fraction); floats appear only in the JSON's convenience `value`.

Between turns of a fight only the carried state (Vex pending, resource uses left) survives, so a fight composes the
per-turn trees as a Markov chain over that carried state.

Standard library only. `python3 oracle.py` rewrites cases.json beside this file (deterministic: same bytes every
run). `python3 oracle.py --check` recomputes and exits 1 if cases.json is stale or any golden fails. README.md lists
every semantic decision the contract left open.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from collections import namedtuple
from fractions import Fraction as Fr
from functools import lru_cache
from itertools import combinations, product

F0 = Fr(0)
F1 = Fr(1)

HERE = os.path.dirname(os.path.abspath(__file__))
CASES_PATH = os.path.join(HERE, "cases.json")

# ----------------------------------------------------------------------------------------------------------------------
# PMFs: dict value -> Fraction. The support is exact (no zero weights are ever created or dropped).
# ----------------------------------------------------------------------------------------------------------------------


def conv(a, b):
    out = {}
    for x, px in a.items():
        for y, py in b.items():
            k = x + y
            out[k] = out.get(k, F0) + px * py
    return out


def conv_many(pmfs):
    r = {0: F1}
    for p in pmfs:
        r = conv(r, p)
    return r


def pmap(a, f):
    out = {}
    for x, p in a.items():
        y = f(x)
        out[y] = out.get(y, F0) + p
    return out


def mean(a):
    return sum((x * p for x, p in a.items()), F0)


def mix(parts):
    out = {}
    for w, a in parts:
        if w == 0:
            continue
        for x, p in a.items():
            out[x] = out.get(x, F0) + w * p
    return out


def max_of_two(a):
    """P(max(X1, X2) = x) = F(x)^2 - F(x-1)^2 for two independent copies of X."""
    out = {}
    cum = F0
    for x in sorted(a):
        prev = cum
        cum += a[x]
        out[x] = cum * cum - prev * prev
    return out


def freeze(pmf):
    return tuple(sorted(pmf.items()))


# ----------------------------------------------------------------------------------------------------------------------
# Dice text: plain dice terms and whole numbers only ("2d6", "1d8+1", "1d10+1d6", "-1d4", "1d4+1d6", "-1d4+1d4").
# ----------------------------------------------------------------------------------------------------------------------

_TERM = re.compile(r"^(\d+)d(\d+)$")


def parse_dice(text):
    """Returns ([(sign, count, sides)], flat). Signs are kept on dice terms (dndMath.ts bug 5)."""
    s = str(text).replace(" ", "")
    terms = re.findall(r"[+-]?[^+-]+", s)
    if "".join(terms) != s:
        raise ValueError(f"bad dice text {text!r}")
    dice, flat = [], 0
    for t in terms:
        sign = -1 if t[0] == "-" else 1
        body = t.lstrip("+-")
        m = _TERM.match(body)
        if m:
            dice.append((sign, int(m.group(1)), int(m.group(2))))
        else:
            flat += sign * int(body)
    return dice, flat


@lru_cache(maxsize=None)
def die_pmf(sides, remaps=()):
    """One die's face distribution after the remaps, applied in order (gwf first, then elemental adept)."""
    p = {f: Fr(1, sides) for f in range(1, sides + 1)}
    for rm in remaps:
        if rm == "gwf2014":
            # Reroll a 1 or 2 once and keep the new roll: P(f) = [f >= 3]/M + (#{1,2} / M) * (1/M).
            low = sum(1 for f in range(1, sides + 1) if f <= 2)
            p = {f: (Fr(1, sides) if f > 2 else F0) + Fr(low, sides) * Fr(1, sides) for f in range(1, sides + 1)}
            p = {f: w for f, w in p.items() if w != 0}
        elif rm == "gwf2024":
            p = pmap(p, lambda f: 3 if f <= 2 else f)
        elif rm == "elemental_adept":
            p = pmap(p, lambda f: 2 if f == 1 else f)
        else:
            raise ValueError(rm)
    return p


@lru_cache(maxsize=None)
def dice_set(dice, remaps=(), times=1):
    """Sum of a tuple of (sign, count, sides) terms, each die remapped; `times` = 2 rolls every die twice (a crit)."""
    parts = []
    for sign, n, sides in dice:
        d = die_pmf(sides, remaps)
        if sign < 0:
            d = {-x: w for x, w in d.items()}
        parts.extend([d] * (n * times))
    return freeze(conv_many(parts))


def bonus_dice_pmf(texts):
    """Bless / Bane style added dice, convolved (several sources add)."""
    pmf = {0: F1}
    for t in texts:
        dice, flat = parse_dice(t)
        pmf = conv(pmf, dict(dice_set(tuple(dice))))
        pmf = {x + flat: w for x, w in pmf.items()}
    return pmf


# ----------------------------------------------------------------------------------------------------------------------
# d20 by enumeration.
# ----------------------------------------------------------------------------------------------------------------------


@lru_cache(maxsize=None)
def face_pmf(mode, lucky, elven):
    """P(kept face = f), f = 1..20 (index f). Enumerates every die of the roll and, with Lucky, every reroll of ONE die
    showing a natural 1 (the new roll must be used). Elven Accuracy (third die) only with advantage."""
    n = 1 if mode == "normal" else (3 if (mode == "advantage" and elven) else 2)
    agg = min if mode == "disadvantage" else max
    counts = [0] * 21
    for faces in product(range(1, 21), repeat=n):
        if lucky and 1 in faces:
            i = faces.index(1)
            for r in range(1, 21):
                counts[agg(faces[:i] + (r,) + faces[i + 1:])] += 1
        else:
            counts[agg(faces)] += 20
    total = 20 ** n * 20
    return tuple(Fr(c, total) for c in counts)


@lru_cache(maxsize=None)
def attack_odds(bonus, ac, crit_min, mode, lucky, elven, bdice, autocrit):
    """(P(miss), P(normal hit), P(crit)) over every kept face and every bonus-die value.
    Hit: face >= crit_min (always), or face != 1 and face + bonus + b >= AC. Crit: face >= crit_min, or any hit when
    autocrit. The crit floor falls out of the enumeration: a crit face is a hit whatever the AC."""
    faces = face_pmf(mode, lucky, elven)
    bd = dict(bdice) if bdice else {0: F1}
    hit = crit = F0
    for f in range(1, 21):
        pf = faces[f]
        if pf == 0:
            continue
        is_crit = f >= crit_min
        for b, pb in bd.items():
            if is_crit or (f != 1 and f + bonus + b >= ac):
                hit += pf * pb
                if is_crit or autocrit:
                    crit += pf * pb
    return (F1 - hit, hit - crit, crit)


@lru_cache(maxsize=None)
def save_fail_odds(bonus, dc, mode, sdice, autofail):
    """P(d20 + bonus + dice < DC). No natural 1 / 20 rule on saves (both editions)."""
    if autofail:
        return F1
    faces = face_pmf(mode, False, False)
    bd = dict(sdice) if sdice else {0: F1}
    fail = F0
    for f in range(1, 21):
        for b, pb in bd.items():
            if f + bonus + b < dc:
                fail += faces[f] * pb
    return fail


def combine_modes(adv, dis):
    if adv and not dis:
        return "advantage"
    if dis and not adv:
        return "disadvantage"
    return "normal"


# ----------------------------------------------------------------------------------------------------------------------
# Tables.
# ----------------------------------------------------------------------------------------------------------------------

# DMG 2014 pp. 274-275 Monster Statistics by Challenge Rating: CR -> (AC, HP min, HP max, attack bonus, save DC).
CR_ROWS = {
    "0": (13, 1, 6, 3, 13), "1/8": (13, 7, 35, 3, 13), "1/4": (13, 36, 49, 3, 13), "1/2": (13, 50, 70, 3, 13),
    "1": (13, 71, 85, 3, 13), "2": (13, 86, 100, 3, 13), "3": (13, 101, 115, 4, 13), "4": (14, 116, 130, 5, 14),
    "5": (15, 131, 145, 6, 15), "6": (15, 146, 160, 6, 15), "7": (15, 161, 175, 6, 15), "8": (16, 176, 190, 7, 16),
    "9": (16, 191, 205, 7, 16), "10": (17, 206, 220, 7, 16), "11": (17, 221, 235, 8, 17),
    "12": (17, 236, 250, 8, 17), "13": (18, 251, 265, 8, 18), "14": (18, 266, 280, 8, 18),
    "15": (18, 281, 295, 8, 18), "16": (18, 296, 310, 9, 18), "17": (19, 311, 325, 10, 19),
    "18": (19, 326, 340, 10, 19), "19": (19, 341, 355, 10, 19), "20": (19, 356, 400, 10, 19),
    "21": (19, 401, 445, 11, 20), "22": (19, 446, 490, 11, 20), "23": (19, 491, 535, 11, 20),
    "24": (19, 536, 580, 12, 21), "25": (19, 581, 625, 12, 21), "26": (19, 626, 670, 12, 21),
    "27": (19, 671, 715, 13, 22), "28": (19, 716, 760, 13, 22), "29": (19, 761, 805, 13, 22),
    "30": (19, 806, 850, 14, 23),
}


def cr_key(cr):
    """'1/2', '5', 0.5, 5 -> the table key."""
    if isinstance(cr, str):
        return cr.strip()
    v = Fr(cr).limit_denominator(8)
    return str(v.numerator) if v.denominator == 1 else f"{v.numerator}/{v.denominator}"


def typical_save_bonus(cr):
    """The Finished Book (tomedunn) average monster save bonus by CR (not DMG)."""
    v = Fr(cr_key(cr))
    if v <= 1:
        return 0
    for hi, bonus in ((3, 1), (5, 2), (7, 3), (9, 4), (11, 5), (13, 6), (16, 7), (18, 8), (20, 9), (22, 10),
                      (24, 11), (26, 12), (28, 13), (30, 14)):
        if v <= hi:
            return bonus
    raise ValueError(cr)


ABILITIES = ("str", "dex", "con", "int", "wis", "cha")
DAMAGE_TYPES = ("acid", "bludgeoning", "cold", "fire", "force", "lightning", "necrotic", "piercing", "poison",
                "psychic", "radiant", "slashing", "thunder")
ADV_CONDITIONS = {"restrained", "blinded", "stunned", "paralyzed", "unconscious"}
AUTOCRIT_CONDITIONS = {"paralyzed", "unconscious"}
AUTOFAIL_CONDITIONS = {"stunned", "paralyzed", "unconscious"}
DEFAULT_RULINGS = {"hew_gets_pb": False, "cleave_part_of_attack_action": False, "gwf_on_riders": False,
                   "savage_attacker_on_crit_dice": False}


def ability_mod(score):
    return (score - 10) // 2


def tier_multiplier(level):
    return 1 if level < 5 else 2 if level < 11 else 3 if level < 17 else 4


def level_value(value, level, what):
    """A scalar, or a step map {"1": a, "5": b}: the value of the highest key <= level."""
    if isinstance(value, dict):
        best = None
        for k in value:
            ki = int(k)
            if ki <= level and (best is None or ki > best):
                best = ki
        if best is None:
            raise ValueError(f"{what}: no step at or below level {level}")
        return value[str(best)]
    return value


# ----------------------------------------------------------------------------------------------------------------------
# Resolution: the DSL JSON (contract section 3) -> plain records at one level. Written from the contract, not from
# agent F's code.
# ----------------------------------------------------------------------------------------------------------------------


class Obj:
    def __init__(self, **kw):
        self.__dict__.update(kw)

    def __repr__(self):
        return f"Obj({self.__dict__})"


def expand_preset(spec):
    """warlock_baseline (research B3): Eldritch Blast (cha spell attack, 1d10 force, beams 1/2/3/4 at 1/5/11/17),
    Agonizing Blast (+cha to EB damage from level 2), Hex (1d6 necrotic every hit, NO setup cost: the published
    baseline assumes Hex is already up), Cha 16 at 1-3, 18 at 4-7, 20 at 8+."""
    if spec.get("preset") != "warlock_baseline":
        raise ValueError(spec.get("preset"))
    return {
        "name": spec["name"], "edition": spec.get("edition", "2024"), "level": spec["level"],
        "abilities": {"cha": {"1": 16, "4": 18, "8": 20}},
        "attacks": [{"name": "Eldritch Blast", "damage": "1d10", "damage_type": "force",
                     "properties": ["ranged", "spell"], "to_hit": {"ability": "cha", "proficient": True},
                     "ability_to_damage": False, "cantrip": "beams"}],
        "modifiers": [
            {"kind": "bonus_damage", "name": "Agonizing Blast", "amount": "cha", "from_level": 2,
             "attacks": ["Eldritch Blast"]},
            {"kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "when": "every_hit",
             "concentration": True, "attacks": ["Eldritch Blast"]},
        ],
    }


def resolve_build(spec, rulings):
    if spec.get("preset"):
        spec = expand_preset(spec)
    level = spec["level"]
    edition = spec.get("edition", "2024")
    pb = spec.get("proficiency_bonus") or (2 + (level - 1) // 4)
    abil = spec.get("abilities") or {}
    mods = {a: ability_mod(level_value(abil.get(a, 10), level, a)) for a in ABILITIES}
    mods["none"] = 0
    style = spec.get("fighting_style")

    def active(x):
        return (x.get("from_level") or 1) <= level <= (x.get("until_level") or 20)

    def amount(v, what="amount"):
        v = level_value(v, level, what)
        if isinstance(v, bool):
            raise ValueError(what)
        if isinstance(v, int):
            return v
        if v == "pb":
            return pb
        if v in ABILITIES:
            return mods[v]
        raise ValueError(f"{what}: {v!r}")

    def filt(m):
        if "attacks" not in m or m["attacks"] is None:
            return None
        return frozenset(n.lower() for n in m["attacks"])

    rb = Obj(level=level, pb=pb, edition=edition, mods=mods, style=style, rulings=rulings)

    # --- attacks (active only), in list order -------------------------------------------------------------------
    attacks = []
    for a in spec.get("attacks", []):
        if not active(a):
            continue
        props = set(a.get("properties", []))
        ranged = "ranged" in props
        melee = not ranged
        spell = "spell" in props
        th = a.get("to_hit") or {}
        ability = th.get("ability", "str")
        count = level_value(a.get("count", 1), level, "count")
        cantrip = a.get("cantrip")
        if cantrip == "beams":
            count *= tier_multiplier(level)
        dice, flat = parse_dice(level_value(a["damage"], level, "damage"))
        if cantrip == "dice":
            dice = [(s, n * tier_multiplier(level), m) for s, n, m in dice]
        offhand = bool(a.get("offhand", False))
        atd = a.get("ability_to_damage")
        if atd is None:
            atd = not offhand
        mod = mods[ability]
        if offhand:
            ability_part = mod if style == "twf" else min(mod, 0)
        else:
            ability_part = mod if atd else 0
        attacks.append(Obj(
            idx=len(attacks), name=a["name"], lname=a["name"].lower(), count=count,
            action=a.get("action", "action"), ability=ability, mod=mod, proficient=th.get("proficient", True),
            base_bonus_extra=th.get("bonus", 0), total=th.get("total"), dice=tuple(dice), flat=flat,
            dtype=a.get("damage_type"), ability_to_damage=atd, ability_part=ability_part, offhand=offhand,
            melee=melee, ranged=ranged, spell=spell, weapon=not spell, props=props, mastery=a.get("mastery"),
            remap=(), ignore_cover=False, lucky=False, elven=False, crit_min=20, bdice=None, bonus=0,
            dueling=2 if (style == "dueling" and melee and not spell and "two-handed" not in props) else 0,
        ))
    rb.attacks = attacks
    by_name = {a.lname: a for a in attacks}

    def applies(f, atk):
        return f is None or atk.lname in f

    rb.applies = applies

    # --- modifiers ------------------------------------------------------------------------------------------------
    rb.riders, rb.bonuses, rb.advs, rb.extras, rb.pas = [], [], [], [], []
    rb.saves, rb.conds, rb.sas = [], [], []
    rb.ea_types = set()
    rb.setups = []
    rb.slots = {}  # mid -> (uses at this level or None for unlimited, per)
    rb.names = {}
    to_hit_amounts = []  # (filter, amount)
    to_hit_dice = []  # (filter, text)
    crit_ranges = []
    luckies, elvens, remaps, ignores = [], [], [], []
    for mid, m in enumerate(spec.get("modifiers", [])):
        if not active(m):
            continue
        kind = m["kind"]
        rb.names[mid] = m.get("name") or kind
        f = filt(m)
        if m.get("resource"):
            if m.get("_unlimited"):
                rb.slots[mid] = (None, m["resource"]["per"])
            else:
                rb.slots[mid] = (level_value(m["resource"]["uses"], level, "uses"), m["resource"]["per"])
        if m.get("setup"):
            rb.setups.append((mid, m["setup"]))
        lam = Fr(str(m["use_value"])) if m.get("use_value") is not None else F0
        if kind == "to_hit":
            if m.get("amount") is not None:
                to_hit_amounts.append((f, amount(m["amount"])))
            if m.get("dice") is not None:
                to_hit_dice.append((f, m["dice"]))
        elif kind == "extra_damage":
            dtext = m.get("dice")
            d, fl = parse_dice(level_value(dtext, level, "dice")) if dtext is not None else ([], 0)
            rb.riders.append(Obj(
                mid=mid, name=rb.names[mid], filt=f, dice=tuple(d),
                amount=fl + (amount(m["amount"]) if m.get("amount") is not None else 0),
                dtype=m.get("type"), when=m.get("when", "every_hit"), policy=m.get("policy", "any_hit"), lam=lam,
                crit_doubles=m.get("crit_doubles", True), aao=bool(m.get("attack_action_only", False)),
                ba_cost=m.get("action_cost") == "bonus_action", slot=mid in rb.slots))
        elif kind == "bonus_damage":
            rb.bonuses.append(Obj(mid=mid, filt=f, amount=amount(m["amount"]),
                                  aao=bool(m.get("attack_action_only", False))))
        elif kind == "crit_range":
            crit_ranges.append((f, level_value(m["min"], level, "min")))
        elif kind == "advantage":
            rb.advs.append(Obj(mid=mid, filt=f, mode=m.get("mode", "advantage"), rate=Fr(str(m.get("rate", 1)))))
        elif kind == "lucky":
            luckies.append(f)
        elif kind == "elven_accuracy":
            elvens.append(f)
        elif kind == "damage_die_remap":
            if m["remap"] == "elemental_adept":
                rb.ea_types.add(m["type"])
            else:
                remaps.append((f, m["remap"]))
        elif kind == "reroll_damage_take_best":
            rb.sas.append(Obj(mid=mid, filt=f, policy=m.get("policy", "any_hit"), lam=lam))
        elif kind == "extra_attack":
            target = by_name[m["attack"].lower()]
            rb.extras.append(Obj(
                mid=mid, attack=target.idx, count=level_value(m.get("count", 1), level, "count"),
                action=m["action"], trigger=m.get("trigger", "always"),
                tp=Fr(str(m["trigger_probability"])) if m.get("trigger_probability") is not None else F1,
                slot=mid in rb.slots))
        elif kind == "power_attack":
            rb.pas.append(Obj(mid=mid, filt=f, penalty=m.get("penalty", 5), bonus=m.get("bonus", 10),
                              policy=m.get("policy", "auto")))
        elif kind == "save_effect":
            if m.get("dc") is not None:
                dc = m["dc"]
            else:
                dc = 8 + pb + mods[m["dc_ability"]]
            dc += m.get("dc_bonus", 0)
            dtext = m.get("dice")
            d, fl = parse_dice(level_value(dtext, level, "dice")) if dtext is not None else ([], 0)
            if m.get("cantrip"):
                d = [(s, n * tier_multiplier(level), sides) for s, n, sides in d]
            if m.get("targets") is not None:
                n_targets = m["targets"]
            elif m.get("shape"):
                size = m["size"]
                div = {"cone": 10, "cube": 5, "cylinder": 5, "line": 30, "sphere": 5}[m["shape"]]
                n_targets = max(1, -(-size // div))
            else:
                n_targets = 1
            rb.saves.append(Obj(
                mid=mid, name=rb.names[mid], ability=m["ability"], dc=dc, dice=tuple(d),
                amount=fl + (amount(m["amount"]) if m.get("amount") is not None else 0), dtype=m.get("type"),
                on_success=m.get("on_success", "half"), targets=n_targets, magical=m.get("magical", True),
                condition=m.get("condition"), action_cost=m.get("action_cost", "action")))
        elif kind == "condition_on_hit":
            rb.conds.append(Obj(mid=mid, filt=f, cond=m["condition"], ability=m["ability"],
                                dc=m.get("dc"), dc_ability=m.get("dc_ability"), when=m.get("when", "every_hit"),
                                policy=m.get("policy", "any_hit"), lam=F0, magical=m.get("magical", False),
                                slot=mid in rb.slots))
        elif kind == "ignore_cover":
            ignores.append(f)
        elif kind in ("ac", "resistance", "temp_hp"):
            pass  # defensive: accepted, no effect on damage dealt
        else:
            raise ValueError(kind)

    # --- per-attack roll parameters ---------------------------------------------------------------------------------
    for atk in attacks:
        if atk.total is not None:
            bonus = atk.total
        else:
            bonus = atk.mod + (pb if atk.proficient else 0) + atk.base_bonus_extra
            if style == "archery" and atk.ranged and atk.weapon:
                bonus += 2
        bonus += sum(v for f, v in to_hit_amounts if applies(f, atk))
        atk.bonus = bonus
        texts = [t for f, t in to_hit_dice if applies(f, atk)]
        atk.bdice = freeze(bonus_dice_pmf(texts)) if texts else None
        mins = [v for f, v in crit_ranges if applies(f, atk)]
        atk.crit_min = min(mins) if mins else 20
        atk.lucky = any(applies(f, atk) for f in luckies)
        atk.elven = atk.ability in ("dex", "int", "wis", "cha") and any(applies(f, atk) for f in elvens)
        rm = [r for f, r in remaps if applies(f, atk)]
        if style == "gwf" and atk.melee and atk.weapon and ({"two-handed", "versatile"} & atk.props):
            rm.append("gwf2014" if edition == "2014" else "gwf2024")
        if len(set(rm)) > 1:
            raise ValueError(f"{atk.name}: two different GWF remaps")
        atk.remap = tuple(sorted(set(rm)))
        atk.ignore_cover = any(applies(f, atk) for f in ignores)
    rb.slot_mids = tuple(sorted(rb.slots))
    rb.action_save = next((s for s in rb.saves if s.action_cost == "action"), None)
    return rb


def resolve_target(t, level):
    t = t or {}
    if t.get("ac") is not None:
        ac = t["ac"]
        cr = str(level)
    elif t.get("cr") is not None:
        cr = cr_key(t["cr"])
        ac = CR_ROWS[cr][0]
    else:
        cr = str(level)
        ac = CR_ROWS[cr][0]
    save_bonus = t.get("save_bonus")
    if save_bonus is None:
        save_bonus = typical_save_bonus(cr)
    saves = {a: save_bonus for a in ABILITIES}
    for a, v in (t.get("saves") or {}).items():
        saves[a] = v
    cover = t.get("cover")
    cover_bonus = {None: 0, "half": 2, "three_quarters": 5}[cover]
    cond = t.get("condition")
    sd = t.get("save_dice")
    return Obj(
        ac=ac, cr=cr, saves=saves, hp=t.get("hp"), res=set(t.get("resistances") or []),
        vul=set(t.get("vulnerabilities") or []), imm=set(t.get("immunities") or []),
        magic_resistance=bool(t.get("magic_resistance", False)), evasion=bool(t.get("evasion", False)),
        conds_initial=frozenset([cond]) if cond else frozenset(), cover_bonus=cover_bonus,
        lr=t.get("legendary_resistance", 0) or 0, save_dice=freeze(bonus_dice_pmf([sd])) if sd else None,
        second_rate=Fr(str(t.get("second_target_rate", 0))))


# ----------------------------------------------------------------------------------------------------------------------
# Damage.
# ----------------------------------------------------------------------------------------------------------------------


def adjust(x, dtype, tg, half=False):
    """Per damage type: halve (a successful save, floor), then resistance floor(x/2), then vulnerability x2;
    immunity 0; floored at 0. Typeless damage (None) is never adjusted."""
    if half:
        x = x // 2
    if dtype is not None:
        if dtype in tg.imm:
            return 0
        if dtype in tg.res:
            x = x // 2
        if dtype in tg.vul:
            x = x * 2
    return max(x, 0)


def total_after_adjust(by_type, tg):
    """by_type: {type: pmf of the raw per-type sum}. Types are independent dice, so the total is the convolution of
    the adjusted per-type PMFs."""
    out = {0: F1}
    for t in sorted(by_type, key=lambda k: (k is None, k or "")):
        out = conv(out, pmap(by_type[t], lambda x, t=t: adjust(x, t, tg)))
    return out


def is_attack_action(rb, kind, for_flat_bonus):
    if kind in ("action", "surge"):
        return True
    if kind == "cleave":
        return rb.rulings["cleave_part_of_attack_action"]
    if kind == "hew":
        return for_flat_bonus and rb.rulings["hew_gets_pb"]
    return False


def rider_applies(rb, r, atk, kind):
    if not rb.applies(r.filt, atk):
        return False
    if r.aao and not is_attack_action(rb, kind, False):
        return False
    return True


def die_remaps(rb, dtype, gwf):
    rm = list(gwf)
    if dtype is not None and dtype in rb.ea_types:
        rm.append("elemental_adept")
    return tuple(rm)


def pa_active(rb, pa_on, atk):
    return [pa for pa in rb.pas if pa.mid in pa_on and rb.applies(pa.filt, atk)]


def hit_pmf(cc, ai, kind, crit, riders_spent, sa, pa_on_atk):
    """The adjusted damage PMF of one hit. riders_spent: optional riders spent on it; sa: Savage Attacker used."""
    key = ("hit", ai, kind, crit, riders_spent, sa, pa_on_atk)
    if key in cc.memo:
        return cc.memo[key]
    rb, tg = cc.rb, cc.tg
    atk = rb.attacks[ai]
    t = atk.dtype
    by_type = {}
    flats = {}

    def add(dt, pmf):
        by_type[dt] = conv(by_type.get(dt, {0: F1}), pmf)

    def add_flat(dt, v):
        flats[dt] = flats.get(dt, 0) + v

    # The weapon's own dice (remapped per die), doubled on a crit; Savage Attacker takes the better of two rolls.
    wremap = die_remaps(rb, t, atk.remap)
    one = dict(dice_set(atk.dice, wremap, 1))
    if sa:
        if crit and rb.rulings["savage_attacker_on_crit_dice"]:
            base = max_of_two(conv(one, one))
        elif crit:
            base = conv(max_of_two(one), one)
        else:
            base = max_of_two(one)
    else:
        base = conv(one, one) if crit else one
    add(t, base)
    add_flat(t, atk.flat)
    # Flat parts: ability modifier (none on a Cleave attack unless negative), bonus_damage, Dueling, power attack.
    if kind == "cleave":
        add_flat(t, min(atk.mod, 0) if atk.ability_to_damage else 0)
    else:
        add_flat(t, atk.ability_part)
    for b in rb.bonuses:
        if rb.applies(b.filt, atk) and (not b.aao or is_attack_action(rb, kind, True)):
            add_flat(t, b.amount)
    add_flat(t, atk.dueling)
    if pa_on_atk:
        for pa in pa_on_atk:
            add_flat(t, pa.bonus)
    # Riders.
    for r in rb.riders:
        if not rider_applies(rb, r, atk, kind):
            continue
        mandatory = r.when == "every_hit" and not r.slot
        if r.when == "on_crit":
            if not crit:
                continue
            times = 1  # crit-only dice are added once
        elif r.when == "on_miss":
            continue
        elif mandatory or r.mid in riders_spent:
            times = 2 if (crit and r.crit_doubles) else 1
        else:
            continue
        rt = r.dtype if r.dtype is not None else t
        gwf = atk.remap if rb.rulings["gwf_on_riders"] else ()
        if r.dice:
            add(rt, dict(dice_set(r.dice, die_remaps(rb, rt, gwf), times)))
        add_flat(rt, r.amount)
    for dt, v in flats.items():
        if dt not in by_type:
            by_type[dt] = {0: F1}
        by_type[dt] = {x + v: w for x, w in by_type[dt].items()}
    out = total_after_adjust(by_type, tg)
    cc.memo[key] = out
    return out


def miss_pmf(cc, ai, kind):
    """Graze (the attack ability's modifier if positive, weapon's type) plus on_miss riders, as one instance."""
    key = ("miss", ai, kind)
    if key in cc.memo:
        return cc.memo[key]
    rb, tg = cc.rb, cc.tg
    atk = rb.attacks[ai]
    by_type = {}
    if atk.mastery == "graze" and atk.mod > 0 and kind != "cleave":
        by_type[atk.dtype] = {atk.mod: F1}
    for r in rb.riders:
        if r.when != "on_miss" or not rider_applies(rb, r, atk, kind):
            continue
        rt = r.dtype if r.dtype is not None else atk.dtype
        p = dict(dice_set(r.dice, die_remaps(rb, rt, ()), 1)) if r.dice else {0: F1}
        p = {x + r.amount: w for x, w in p.items()}
        by_type[rt] = conv(by_type.get(rt, {0: F1}), p)
    out = total_after_adjust(by_type, tg) if by_type else {0: F1}
    cc.memo[key] = out
    return out


# ----------------------------------------------------------------------------------------------------------------------
# The turn as an explicit outcome tree.
# ----------------------------------------------------------------------------------------------------------------------

# spent: once-per-turn things used this turn (rider mids, SA mids, condition mids, "cleave")
# uses: tuple aligned with rb.slot_mids (None = unlimited); vex: Vex advantage pending on the main target
# conds: conditions applied to the target this turn; ba: Bonus Action available; trig: {"hit", "crit"} seen this turn
St = namedtuple("St", "spent uses vex conds ba trig")
Opt = namedtuple("Opt", "kind mid policy lam slot ba_cost once")


class Res:
    """Aggregate over a subtree, conditional on reaching its root. ev: E[damage]; obj: E[damage] - sum(lambda * uses)
    plus the continuation value at the leaves; uses / rdmg: per modifier; carry: end-of-turn carried state -> P;
    pmf: the subtree's damage distribution (only when asked for)."""

    __slots__ = ("ev", "obj", "uses", "rdmg", "carry", "pmf")

    def __init__(self, ev, obj, uses, rdmg, carry, pmf):
        self.ev, self.obj, self.uses, self.rdmg, self.carry, self.pmf = ev, obj, uses, rdmg, carry, pmf


def r_shift(r, pmf, duses, drdmg, lamcost, want):
    ev = mean(pmf)
    uses = dict(r.uses)
    for k, v in duses.items():
        uses[k] = uses.get(k, F0) + v
    rdmg = dict(r.rdmg)
    for k, v in drdmg.items():
        rdmg[k] = rdmg.get(k, F0) + v
    return Res(r.ev + ev, r.obj + ev - lamcost, uses, rdmg, r.carry, conv(pmf, r.pmf) if want else None)


def r_mix(parts, want):
    ev = obj = F0
    uses, rdmg, carry = {}, {}, {}
    for p, r in parts:
        ev += p * r.ev
        obj += p * r.obj
        for k, v in r.uses.items():
            uses[k] = uses.get(k, F0) + p * v
        for k, v in r.rdmg.items():
            rdmg[k] = rdmg.get(k, F0) + p * v
        for k, v in r.carry.items():
            carry[k] = carry.get(k, F0) + p * v
    pmf = mix([(p, r.pmf) for p, r in parts]) if want else None
    return Res(ev, obj, uses, rdmg, carry, pmf)


class TC:
    """Per-turn context: the advantage sources present this turn, the power attacks switched on, whether PMFs are
    wanted, and the continuation value of an end-of-turn carried state (0 for per-turn decisions)."""

    def __init__(self, cc, present, pa_on, want, cont):
        self.cc, self.rb, self.tg = cc, cc.rb, cc.tg
        self.present, self.pa_on, self.want, self.cont = present, pa_on, want, cont
        self._ev = None

    def ev_mode(self):
        if not self.want:
            return self
        if self._ev is None:
            self._ev = TC(self.cc, self.present, self.pa_on, False, self.cont)
        return self._ev


def uses_left(rb, st, mid):
    return st.uses[rb.slot_mids.index(mid)]


def uses_dec(rb, uses, mid):
    i = rb.slot_mids.index(mid)
    left = uses[i]
    if left is None:
        return uses
    return uses[:i] + (left - 1,) + uses[i + 1:]


def target_conditions(tc, st, kind):
    conds = set()
    if kind != "cleave":
        conds |= tc.tg.conds_initial
    if kind not in ("cleave", "reaction"):
        conds |= st.conds
    return conds


def roll(tc, st, atk, kind):
    rb, tg = tc.rb, tc.tg
    adv = dis = False
    if kind != "reaction":
        for a in rb.advs:
            if a.mid in tc.present and rb.applies(a.filt, atk):
                if a.mode == "advantage":
                    adv = True
                else:
                    dis = True
    if kind not in ("cleave", "reaction") and st.vex:
        adv = True
    conds = target_conditions(tc, st, kind)
    if "prone" in conds:
        if atk.melee:
            adv = True
        else:
            dis = True
    if conds & ADV_CONDITIONS:
        adv = True
    if "dodging" in conds:
        dis = True
    mode = combine_modes(adv, dis)
    autocrit = atk.melee and bool(conds & AUTOCRIT_CONDITIONS)
    elven = mode == "advantage" and atk.elven
    ac = tg.ac + (0 if atk.ignore_cover else tg.cover_bonus)
    pas = pa_active(rb, tc.pa_on, atk)
    bonus = atk.bonus - sum(pa.penalty for pa in pas)
    return attack_odds(bonus, ac, atk.crit_min, mode, atk.lucky, elven, atk.bdice, autocrit), tuple(pas)


def option_applies(tc, o, ai, kind):
    rb = tc.rb
    atk = rb.attacks[ai]
    if o.kind == "rider":
        r = next(x for x in rb.riders if x.mid == o.mid)
        return rider_applies(rb, r, atk, kind)
    if o.kind == "sa":
        s = next(x for x in rb.sas if x.mid == o.mid)
        return atk.weapon and rb.applies(s.filt, atk)
    c = next(x for x in rb.conds if x.mid == o.mid)
    return rb.applies(c.filt, atk) and kind not in ("cleave", "reaction")


def option_available(tc, st, o, kind):
    if o.once and o.mid in st.spent:
        return False
    if o.slot:
        if kind == "reaction":
            return False  # a reaction attack spends no resources
        left = uses_left(tc.rb, st, o.mid)
        if left is not None and left <= 0:
            return False
    if o.ba_cost and not st.ba:
        return False
    return True


def eligible_options(tc, st, ai, kind):
    rb = tc.rb
    out = []
    for r in rb.riders:
        if r.when == "first_hit_per_turn" or (r.when == "every_hit" and r.slot):
            out.append(Opt("rider", r.mid, r.policy, r.lam, r.slot, r.ba_cost, r.when == "first_hit_per_turn"))
    for s in rb.sas:
        out.append(Opt("sa", s.mid, s.policy, s.lam, s.mid in rb.slots, False, True))
    conds_now = tc.tg.conds_initial | st.conds
    for c in rb.conds:
        if c.cond in conds_now:
            continue  # never attempted when the target already has the condition
        out.append(Opt("cond", c.mid, c.policy, c.lam, c.slot, False, c.when == "first_hit_per_turn"))
    return [o for o in out if option_applies(tc, o, ai, kind) and option_available(tc, st, o, kind)]


def is_last(tc, o, rest):
    return not any(option_applies(tc, o, ai2, k2) for ai2, k2 in rest)


def save_fail_for(tc, ability, dc, magical, conds):
    tg = tc.tg
    autofail = ability in ("str", "dex") and bool(conds & AUTOFAIL_CONDITIONS)
    bonus = tg.saves[ability] + (tg.cover_bonus if ability == "dex" else 0)
    adv = magical and tg.magic_resistance
    dis = False
    if ability == "dex" and "restrained" in conds:
        dis = True
    if ability == "dex" and "dodging" in conds:
        adv = True
    return save_fail_odds(bonus, dc, combine_modes(adv, dis), tg.save_dice, autofail)


def go(tc, st, queue, phase):
    if queue:
        (ai, kind), rest = queue[0], queue[1:]
        return attack_node(tc, st, ai, kind, rest, phase)
    if phase == "action":
        return ba_phase(tc, st)
    return leaf(tc, st)


def leaf(tc, st):
    key = (st.vex, st.uses)
    return Res(F0, tc.cont(key), {}, {}, {key: F1}, {0: F1} if tc.want else None)


def attack_node(tc, st, ai, kind, rest, phase):
    atk = tc.rb.attacks[ai]
    (pm, pn, pc), pas = roll(tc, st, atk, kind)
    # The roll consumes a pending Vex advantage on the main target (a Cleave attack is against another creature).
    st_r = st if kind in ("cleave", "reaction") else st._replace(vex=False)
    parts = []
    if pm:
        mp = miss_pmf(tc.cc, ai, kind)
        parts.append((pm, r_shift(go(tc, st_r, rest, phase), mp, {}, {}, F0, tc.want)))
    for crit, p in ((False, pn), (True, pc)):
        if p:
            parts.append((p, hit_node(tc, st_r, ai, kind, crit, rest, phase, pas)))
    return r_mix(parts, tc.want)


def hit_node(tc, st, ai, kind, crit, rest, phase, pas):
    opts = eligible_options(tc, st, ai, kind)
    fixed, optimal = [], []
    for o in opts:
        if o.policy == "optimal":
            optimal.append(o)
        elif (o.policy == "any_hit" or (o.policy == "crits_only" and crit)
              or (o.policy == "crit_or_last" and (crit or is_last(tc, o, rest)))):
            fixed.append(o)
    # Only one Bonus-Action-costing rider can be spent (the first listed wins among fixed policies).
    kept, ba_taken = [], False
    for o in fixed:
        if o.ba_cost:
            if ba_taken:
                continue
            ba_taken = True
        kept.append(o)
    fixed = tuple(kept)
    if not optimal:
        return post_hit(tc, st, ai, kind, crit, fixed, rest, phase, pas)
    cands = []
    for k in range(len(optimal) + 1):
        for sub in combinations(optimal, k):
            if sum(1 for o in sub if o.ba_cost) + ba_taken > 1:
                continue
            cands.append(fixed + sub)
    tce = tc.ev_mode()
    best, best_s = None, None
    for s in cands:  # smallest spend sets first, so a tie never spends
        r = post_hit(tce, st, ai, kind, crit, s, rest, phase, pas)
        if best is None or r.obj > best.obj:
            best, best_s = r, s
    if tc.want:
        best = post_hit(tc, st, ai, kind, crit, best_s, rest, phase, pas)
    return best


def post_hit(tc, st, ai, kind, crit, spend, rest, phase, pas):
    rb, tg, cc = tc.rb, tc.tg, tc.cc
    atk = rb.attacks[ai]
    spent = set(st.spent)
    uses = st.uses
    ba = st.ba
    for o in spend:
        if o.once:
            spent.add(o.mid)
        if o.slot:
            uses = uses_dec(rb, uses, o.mid)
        if o.ba_cost:
            ba = False
    riders_spent = frozenset(o.mid for o in spend if o.kind == "rider")
    sa = any(o.kind == "sa" for o in spend)
    dmg = hit_pmf(cc, ai, kind, crit, riders_spent, sa, pas)
    e_hit = mean(dmg)
    duses = {o.mid: F1 for o in spend}
    lamcost = sum((o.lam for o in spend), F0)
    drdmg = {}
    for o in spend:
        if o.kind == "rider":
            drdmg[o.mid] = e_hit - mean(hit_pmf(cc, ai, kind, crit, riders_spent - {o.mid}, sa, pas))
        elif o.kind == "sa":
            drdmg[o.mid] = e_hit - mean(hit_pmf(cc, ai, kind, crit, riders_spent, False, pas))
    trig = set(st.trig)
    trig.add("hit")
    if crit and atk.melee and atk.weapon:
        trig.add("crit")
    base = st._replace(spent=frozenset(spent), uses=uses, ba=ba, trig=frozenset(trig))
    # Outcomes after the hit: (probability, conditional damage PMF of this hit, state, queue).
    outs = [(F1, dmg, base, rest)]
    if kind not in ("cleave", "reaction"):
        if atk.mastery == "vex":
            nxt = []
            for p, pmf, s, q in outs:
                p0 = pmf.get(0, F0)
                if p0 != 1:
                    nz = {x: w / (1 - p0) for x, w in pmf.items() if x != 0}
                    nxt.append((p * (1 - p0), nz, s._replace(vex=True), q))
                if p0 != 0:
                    nxt.append((p * p0, {0: F1}, s._replace(vex=False), q))
            outs = nxt
        for o in spend:
            if o.kind != "cond":
                continue
            c = next(x for x in rb.conds if x.mid == o.mid)
            dc = c.dc if c.dc is not None else 8 + rb.pb + (rb.mods[c.dc_ability] if c.dc_ability else atk.mod)
            nxt = []
            for p, pmf, s, q in outs:
                conds = tg.conds_initial | s.conds
                if c.cond in conds:
                    nxt.append((p, pmf, s, q))
                    continue
                fail = save_fail_for(tc, c.ability, dc, c.magical, conds)
                if fail:
                    nxt.append((p * fail, pmf, s._replace(conds=s.conds | {c.cond}), q))
                if fail != 1:
                    nxt.append((p * (1 - fail), pmf, s, q))
            outs = nxt
        if atk.mastery == "topple":
            dc = 8 + atk.mod + rb.pb
            nxt = []
            for p, pmf, s, q in outs:
                conds = tg.conds_initial | s.conds
                if "prone" in conds:
                    nxt.append((p, pmf, s, q))
                    continue
                fail = save_fail_for(tc, "con", dc, False, conds)
                if fail:
                    nxt.append((p * fail, pmf, s._replace(conds=s.conds | {"prone"}), q))
                if fail != 1:
                    nxt.append((p * (1 - fail), pmf, s, q))
            outs = nxt
        if atk.mastery == "cleave" and atk.melee and "cleave" not in base.spent and tg.second_rate > 0:
            # The second creature's presence is sampled once per turn, at the first melee hit with the Cleave weapon
            # (research A5: p_adj * P(>= 1 eligible hit) * E[attack]); cc.cleave_per_hit is the alternative reading
            # (every such hit draws again until a Cleave happens).
            r2 = tg.second_rate
            nxt = []
            for p, pmf, s, q in outs:
                s2 = s._replace(spent=s.spent | {"cleave"})
                nxt.append((p * r2, pmf, s2, ((ai, "cleave"),) + q))
                if r2 != 1:
                    nxt.append((p * (1 - r2), pmf, s if cc.cleave_per_hit else s2, q))
            outs = nxt
    parts = []
    for p, pmf, s, q in outs:
        child = go(tc, s, q, phase)
        parts.append((p, r_shift(child, pmf, duses, drdmg, lamcost, tc.want)))
    return r_mix(parts, tc.want)


def ba_phase(tc, st):
    rb = tc.rb
    options = []
    if st.ba:
        ba_q = tuple((a.idx, "ba") for a in rb.attacks if a.action == "bonus_action" for _ in range(a.count))
        if ba_q:
            options.append(("ba_attacks", ba_q, None))
        for ex in rb.extras:
            if ex.action != "bonus_action":
                continue
            if ex.trigger == "crit" and "crit" not in st.trig:
                continue
            if ex.trigger == "hit" and "hit" not in st.trig:
                continue
            if ex.slot:
                left = uses_left(rb, st, ex.mid)
                if left is not None and left <= 0:
                    continue
            options.append(("extra", tuple((ex.attack, "hew") for _ in range(ex.count)), ex))
        for se in rb.saves:
            if se.action_cost == "bonus_action":
                options.append(("save", None, se))
    options.append(("none", None, None))
    if len(options) == 1:
        return leaf(tc, st)

    def run(opt, t):
        kind, q, x = opt
        if kind == "none":
            return leaf(t, st)
        s = st._replace(ba=False)
        if kind == "save":
            child = leaf(t, s)
            return r_shift(child, save_turn_pmf(t, x, t.tg.conds_initial | st.conds), {}, {}, F0, t.want)
        duses = {}
        if x is not None and x.slot:
            s = s._replace(uses=uses_dec(rb, s.uses, x.mid))
        if x is not None:
            duses = {x.mid: F1}
        return r_shift(go(t, s, q, "ba"), {0: F1}, duses, {}, F0, t.want)

    tce = tc.ev_mode()
    best, best_o = None, None
    for o in options:
        r = run(o, tce)
        if best is None or r.obj > best.obj:
            best, best_o = r, o
    if tc.want:
        best = run(best_o, tc)
    return best


def run_turn(tc, carry, first_round):
    rb = tc.rb
    vex, uses = carry
    st = St(frozenset(), uses, vex, frozenset(), True, frozenset())
    action_free = True
    if first_round:
        for _, setup in rb.setups:
            if setup == "bonus_action":
                st = st._replace(ba=False)
            elif setup == "action":
                action_free = False
    queue = ()
    duses = {}
    if action_free and rb.action_save is not None:
        se = rb.action_save
        child = go(tc, st, (), "action")
        return r_shift(child, save_turn_pmf(tc, se, tc.tg.conds_initial), {}, {}, F0, tc.want)
    if action_free:
        queue = tuple((a.idx, "action") for a in rb.attacks if a.action == "action" for _ in range(a.count))
    for ex in rb.extras:
        if ex.action != "action":
            continue
        left = uses_left(rb, st, ex.mid) if ex.slot else None
        if ex.slot and left is not None and left <= 0:
            continue
        queue += tuple((ex.attack, "surge") for _ in range(ex.count))
        if ex.slot:
            st = st._replace(uses=uses_dec(rb, st.uses, ex.mid))
        duses[ex.mid] = F1
    return r_shift(go(tc, st, queue, "action"), {0: F1}, duses, {}, F0, tc.want)


def adv_samples(rb):
    always = frozenset(a.mid for a in rb.advs if a.rate >= 1)
    var = [a for a in rb.advs if 0 < a.rate < 1]
    out = []
    for bits in product((0, 1), repeat=len(var)):
        p = F1
        pres = set(always)
        for a, b in zip(var, bits):
            p *= a.rate if b else 1 - a.rate
            if b:
                pres.add(a.mid)
        out.append((frozenset(pres), p))
    return out


def reaction_res(cc, want):
    """The best single reaction extra attack (by trigger_probability x E[damage]) as a standalone attack: fresh
    once-per-turn riders, no resources, no Vex or conditions from the turn."""
    rb = cc.rb
    best = None
    for ex in rb.extras:
        if ex.action != "reaction":
            continue
        tc = TC(cc, frozenset(), frozenset(pa.mid for pa in rb.pas if pa.policy == "always"), want, lambda k: F0)
        st = St(frozenset(), tuple(0 if m in rb.slots else None for m in rb.slot_mids), False, frozenset(), False,
                frozenset())
        r = go(tc, st, tuple((ex.attack, "reaction") for _ in range(ex.count)), "ba")
        if best is None or ex.tp * r.ev > best[0] * best[1].ev:
            best = (ex.tp, r)
    return best


def eval_turn(cc, carry, first_round, want, cont):
    """One turn from a carried state: advantage-source samples x power-attack choice x the outcome tree, plus the
    round's reaction."""
    rb = cc.rb
    parts, pa_choices = [], []
    for present, p in adv_samples(rb):
        auto = [pa for pa in rb.pas if pa.policy == "auto"]
        fixed_on = frozenset(pa.mid for pa in rb.pas if pa.policy == "always")
        if not auto:
            r = run_turn(TC(cc, present, fixed_on, want, cont), carry, first_round)
            on = fixed_on
        else:
            best = None
            for k in range(len(auto) + 1):
                for sub in combinations(auto, k):
                    on = fixed_on | {pa.mid for pa in sub}
                    r = run_turn(TC(cc, present, on, False, cont), carry, first_round)
                    if best is None or r.ev > best[0].ev:  # the turn's expected damage; ties keep it off
                        best = (r, on)
            r, on = best
            if want:
                r = run_turn(TC(cc, present, on, True, cont), carry, first_round)
        parts.append((p, r))
        pa_choices.append((p, on))
    res = r_mix(parts, want)
    react = reaction_res(cc, want)
    if react is not None:
        tp, rr = react
        res = Res(res.ev + tp * rr.ev, res.obj + tp * rr.ev,
                  {k: res.uses.get(k, F0) + tp * rr.uses.get(k, F0) for k in set(res.uses) | set(rr.uses)},
                  {k: res.rdmg.get(k, F0) + tp * rr.rdmg.get(k, F0) for k in set(res.rdmg) | set(rr.rdmg)},
                  res.carry, conv(res.pmf, mix([(tp, rr.pmf), (1 - tp, {0: F1})])) if want else None)
    return res, pa_choices


# ----------------------------------------------------------------------------------------------------------------------
# Save effects.
# ----------------------------------------------------------------------------------------------------------------------


def save_raw_pmf(cc, se):
    rb = cc.rb
    rm = die_remaps(rb, se.dtype, ())
    pmf = dict(dice_set(se.dice, rm, 1)) if se.dice else {0: F1}
    return {x + se.amount: w for x, w in pmf.items()}


def save_outcome_damage(cc, se, x):
    """(damage on a failed save, damage on a success) for raw roll x, after halving / evasion / adjustments."""
    tg = cc.tg
    evasion = tg.evasion and se.ability == "dex" and se.on_success == "half"
    d_fail = adjust(x, se.dtype, tg, half=evasion)
    if se.on_success == "half" and not evasion:
        d_succ = adjust(x, se.dtype, tg, half=True)
    else:
        d_succ = 0
    return d_fail, d_succ


def save_fail_effect(tc, se, conds):
    return save_fail_for(tc, se.ability, se.dc, se.magical, set(conds))


def save_turn_pmf(tc, se, conds):
    """The TOTAL damage across the effect's targets: one shared roll x, independent saves, k failures ->
    k * dmgF(x) + (n - k) * dmgS(x)."""
    cc = tc.cc
    key = ("save", se.mid, frozenset(conds))
    if key in cc.memo:
        return cc.memo[key]
    F = save_fail_effect(tc, se, conds)
    n = se.targets
    out = {}
    for x, px in save_raw_pmf(cc, se).items():
        dF, dS = save_outcome_damage(cc, se, x)
        for k in range(n + 1):
            pk = math.comb(n, k) * F ** k * (1 - F) ** (n - k)
            if pk == 0:
                continue
            v = k * dF + (n - k) * dS
            out[v] = out.get(v, F0) + px * pk
    cc.memo[key] = out
    return out


def save_stats(cc):
    rb, tg = cc.rb, cc.tg
    se = rb.saves[0]
    tc = TC(cc, frozenset(), frozenset(), False, lambda k: F0)
    F = save_fail_effect(tc, se, tg.conds_initial)
    n = se.targets
    raw = F0
    eff = F0
    p_each = F0
    p_all = F0
    kd = [F0] * (n + 1)
    hp = tg.hp
    for x, px in save_raw_pmf(cc, se).items():
        dF, dS = save_outcome_damage(cc, se, x)
        raw += px * (F * dF + (1 - F) * dS)
        if hp is not None:
            eff += px * (F * min(dF, hp) + (1 - F) * min(dS, hp))
            q = F * (1 if dF >= hp else 0) + (1 - F) * (1 if dS >= hp else 0)
            p_each += px * q
            p_all += px * q ** n
            for k in range(n + 1):
                kd[k] += px * math.comb(n, k) * q ** k * (1 - q) ** (n - k)
    out = {"targets": n, "dc": se.dc, "per_target_fail": num(F), "expected_damage_per_target": num(raw),
           "raw": num(raw * n)}
    if hp is not None:
        out.update({"effective": num(eff * n), "p_each_dies": num(p_each), "p_all_die": num(p_all),
                    "expected_kills": num(p_each * n),
                    "kill_distribution": [[k, frac_str(kd[k])] for k in range(n + 1)]})
    if se.condition and tg.lr:
        out["expected_casts_to_land"] = num((tg.lr + 1) / F) if F else {"exact": "inf", "value": None}
    return out


# ----------------------------------------------------------------------------------------------------------------------
# Horizons.
# ----------------------------------------------------------------------------------------------------------------------


class CaseCtx:
    def __init__(self, build, target, rulings):
        self.rulings = dict(DEFAULT_RULINGS)
        self.rulings.update(rulings or {})
        self.rb = resolve_build(build, self.rulings)
        self.tg = resolve_target(target, build["level"])
        self.memo = {}
        self.cleave_per_hit = False

    def initial_carry(self):
        return (False, tuple(self.rb.slots[m][0] for m in self.rb.slot_mids))


def zero_cont(_key):
    return F0


def round1(cc, want=True):
    res, pa = eval_turn(cc, cc.initial_carry(), True, want, zero_cont)
    return res, pa


def fight(cc, rounds, scope="turn"):
    """E[total damage over R rounds] / R, and per-modifier uses / rider damage per round. scope "turn": every
    decision maximises the current turn (E - lambda * uses); scope "fight": decisions also count the value of the
    carried state (uses left, Vex pending) to the end of the fight (full backward induction)."""
    memo_v = {}
    memo_turn = {}  # (round index, carried state) -> turn result
    memo_first = {}  # (is first round, carried state) -> turn result, for per-turn decisions

    def cont_for(r):
        if scope == "turn" or r >= rounds:
            return zero_cont
        return lambda key: value(r, key)

    def turn(r, key):
        k = (r, key)
        if k not in memo_turn:
            first = r == 0
            if scope == "turn":
                # A separate dict: (1, key) == (True, key) in Python, so one shared dict would hand round 2 the
                # first round's turn (setup costs and all).
                if (first, key) not in memo_first:
                    memo_first[(first, key)] = eval_turn(cc, key, first, False, zero_cont)[0]
                memo_turn[k] = memo_first[(first, key)]
            else:
                memo_turn[k] = eval_turn(cc, key, first, False, cont_for(r + 1))[0]
        return memo_turn[k]

    def value(r, key):
        k = (r, key)
        if k not in memo_v:
            memo_v[k] = turn(r, key).obj
        return memo_v[k]

    dist = {cc.initial_carry(): F1}
    total = F0
    uses, rdmg = {}, {}
    for r in range(rounds):
        nxt = {}
        for key, p in dist.items():
            t = turn(r, key)
            total += p * t.ev
            for m, v in t.uses.items():
                uses[m] = uses.get(m, F0) + p * v
            for m, v in t.rdmg.items():
                rdmg[m] = rdmg.get(m, F0) + p * v
            for k2, p2 in t.carry.items():
                nxt[k2] = nxt.get(k2, F0) + p * p2
        dist = nxt
    return total / rounds, {m: v / rounds for m, v in uses.items()}, {m: v / rounds for m, v in rdmg.items()}


def day(build, target, rulings, rounds, encounters, short_rests):
    """Day DPR = fight DPR with every resource-limited feature removed + sum over those features of
    min(U, u * E * R) * d / (E * R); U = uses(long_rest) + uses(short_rest) * (S + 1); u = uses per round and
    d = marginal damage per use, from the fight horizon with that feature alone restored and unlimited."""
    E = Fr(str(encounters))
    S = short_rests
    mods = build.get("modifiers", [])
    limited = [i for i, m in enumerate(mods) if m.get("resource")]
    base = dict(build)
    base["modifiers"] = [m for m in mods if not m.get("resource")]
    d0, _, _ = fight(CaseCtx(base, target, rulings), rounds)
    total = d0
    detail = []
    for i in limited:
        m = mods[i]
        variant = dict(build)
        kept = []
        feature_mid = None
        for j, mm in enumerate(mods):
            if j == i:
                feature_mid = len(kept)
                mm = dict(mm)
                mm["_unlimited"] = True
                kept.append(mm)
            elif not mm.get("resource"):
                kept.append(mm)
        variant["modifiers"] = kept
        cc = CaseCtx(variant, target, rulings)
        dm, uses, _ = fight(cc, rounds)
        u = uses.get(feature_mid, F0)
        d = (dm - d0) / u if u else F0
        lvl = build["level"]
        per_use = level_value(m["resource"]["uses"], lvl, "uses")
        U = per_use if m["resource"]["per"] == "long_rest" else per_use * (S + 1)
        er = E * rounds
        contrib = min(Fr(U), u * er) * d / er
        total += contrib
        detail.append({"name": m.get("name") or m["kind"], "uses_per_day": U, "uses_per_round_unlimited": num(u),
                       "marginal_damage_per_use": num(d), "contribution": num(contrib)})
    return total, d0, detail


# ----------------------------------------------------------------------------------------------------------------------
# Output helpers.
# ----------------------------------------------------------------------------------------------------------------------


def frac_str(x):
    x = Fr(x)
    return f"{x.numerator}/{x.denominator}"


def num(x):
    x = Fr(x)
    return {"exact": frac_str(x), "value": float(x)}


# ----------------------------------------------------------------------------------------------------------------------
# Case catalogue. Every build / target / rulings object is DSL JSON exactly as contract section 3 names its fields.
# ----------------------------------------------------------------------------------------------------------------------

CASES = []
DELTAS = []
PMF_MAX_SUPPORT = 48


def weapon(name, damage, dtype, props, count=1, ability="str", **extra):
    a = {"name": name}
    if count != 1:
        a["count"] = count
    a["to_hit"] = {"ability": ability, "proficient": True}
    for k in ("bonus", "total"):
        if k in extra:
            a["to_hit"][k] = extra.pop(k)
    a["damage"] = damage
    if dtype is not None:
        a["damage_type"] = dtype
    a["properties"] = list(props)
    a.update(extra)
    return a


def build(name, level, attacks=(), modifiers=(), edition="2024", abilities=None, style=None, pb=None, preset=None):
    b = {"name": name}
    if preset:
        b["preset"] = preset
    b["edition"] = edition
    b["level"] = level
    if abilities:
        b["abilities"] = abilities
    if pb is not None:
        b["proficiency_bonus"] = pb
    if style:
        b["fighting_style"] = style
    if not preset:
        b["attacks"] = list(attacks)
        b["modifiers"] = list(modifiers)
    return b


def md(kind, **kw):
    m = {"kind": kind}
    m.update(kw)
    return m


def fight_h(rounds):
    return {"kind": "fight", "rounds": rounds, "encounters_per_day": None, "short_rests": None}


def day_h(rounds, encounters, short_rests, preset=None):
    h = {"kind": "day", "rounds": rounds, "encounters_per_day": encounters, "short_rests": short_rests}
    if preset:
        h["rest_preset"] = preset
    return h


ROUND1 = {"kind": "round1", "rounds": None, "encounters_per_day": None, "short_rests": None}


def case(cid, description, refs, bld, target=None, rulings=None, horizon=None, golden=(), riders=False,
         notes=None, pmf=True, alt=None):
    if any(c["id"] == cid for c in CASES):
        raise ValueError(f"duplicate case id {cid}")
    CASES.append({"id": cid, "description": description, "contract_refs": list(refs), "build": bld,
                  "target": target, "rulings": rulings, "horizon": horizon or ROUND1, "_golden": list(golden),
                  "_riders": riders, "_notes": notes, "_pmf": pmf, "_alt": alt})


def delta(did, description, plus, minus, golden=None, tol=None, refs=()):
    DELTAS.append({"id": did, "description": description, "plus": plus, "minus": minus, "golden": golden,
                   "tol": tol, "refs": list(refs)})


# --- shared pieces ----------------------------------------------------------------------------------------------------

STR18 = {"str": 18}
LONGSWORD = weapon("Longsword", "1d8", "slashing", ["melee", "versatile"])
LONGSWORD2 = weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=2)
GREATSWORD2 = weapon("Greatsword", "2d6", "slashing", ["melee", "heavy", "two-handed"], count=2)
GREATSWORD2_GRAZE = weapon("Greatsword", "2d6", "slashing", ["melee", "heavy", "two-handed"], count=2,
                           mastery="graze")
PA_AUTO = md("power_attack", name="Great Weapon Master (-5/+10)", policy="auto")
GWM14_BA = md("extra_attack", name="GWM crit bonus attack", attack="Greatsword", action="bonus_action",
              trigger="crit")
HWM = md("bonus_damage", name="Heavy Weapon Mastery", amount="pb", attack_action_only=True)
HEW = md("extra_attack", name="Hew", attack="Greatsword", action="bonus_action", trigger="crit")
SAVAGE = md("reroll_damage_take_best", name="Savage Attacker")
ACTION_SURGE = md("extra_attack", name="Action Surge", attack="Greatsword", count=2, action="action",
                  resource={"uses": 1, "per": "short_rest"})


def fighter14(ac_mods, str_score=18, style="gwf"):
    return build("2014 L5 Fighter", 5, [GREATSWORD2], ac_mods, edition="2014", abilities={"str": str_score},
                 style=style)


def fighter24(mods, str_score=19, style="gwf", attack=GREATSWORD2_GRAZE):
    return build("2024 L5 Fighter", 5, [attack], mods, edition="2024", abilities={"str": str_score}, style=style)


def plain5(attacks, mods=(), edition="2024", abilities=None, style=None, name="L5 test build"):
    return build(name, 5, attacks, mods, edition=edition, abilities=abilities or STR18, style=style)


def define_cases():
    # ============================== d20: accuracy, crit range, advantage family, bonus dice ==========================
    ls1 = plain5([LONGSWORD])
    case("d20-plain-ac15", "Longsword 1d8+4 at +7 vs AC 15: P(hit) 0.65, P(crit) 0.05.", ["§2", "§4.1"], ls1,
         {"ac": 15}, golden=[("dpr", 5.75, 1e-12)])
    case("d20-plain-ac10", "+7 vs AC 10: need 3, P(hit) 0.90.", ["§2"], ls1, {"ac": 10})
    case("d20-plain-ac5-natural-1-misses", "+7 vs AC 5: every face but a natural 1 hits (0.95).", ["§2"], ls1,
         {"ac": 5})
    case("d20-plain-ac27-natural-20-only", "+7 vs AC 27: only a natural 20 hits, and it is a crit.", ["§2"],
         ls1, {"ac": 27}, golden=[("dpr", 0.65, 1e-12)])
    case("d20-crit-floor-golden", "Crit floor regression: +5 (to_hit total) vs AC 30, crit 19, 1d8+3 -> 1.2 "
         "(dndMath.ts gives 0.825).", ["§2", "§8.1"],
         build("Crit floor", 5, [weapon("Longsword", "1d8", "slashing", ["melee"], total=5)],
               [md("crit_range", name="Improved Critical", min=19)], edition="2014", abilities={"str": 16}),
         {"ac": 30}, golden=[("dpr", 1.2, 1e-12)])
    case("d20-crit17-ac30-floor", "Crit 17 vs an unreachable AC: hit = crit = 0.20.", ["§2"],
         plain5([LONGSWORD], [md("crit_range", min=17)]), {"ac": 30})
    case("d20-crit18", "Crit 18 (Superior Critical) at +7 vs AC 15: P(crit) 0.15.", ["§2", "§4.1"],
         plain5([LONGSWORD], [md("crit_range", name="Superior Critical", min=18)]), {"ac": 15})
    case("d20-crit-range-lowest-wins", "Two crit_range modifiers (19 and 18): the lowest min applies.", ["§3.4"],
         plain5([LONGSWORD], [md("crit_range", name="A", min=19), md("crit_range", name="B", min=18)]), {"ac": 15})
    case("d20-to-hit-total-override", "to_hit total +9 overrides the computed bonus; Str still adds +4 damage.",
         ["§3.3"], plain5([weapon("Longsword", "1d8", "slashing", ["melee"], total=9)]), {"ac": 15})
    case("d20-to-hit-amount", "to_hit modifier amount +1 (e.g. a +1 weapon as a modifier).", ["§3.4"],
         plain5([LONGSWORD], [md("to_hit", name="+1", amount=1)]), {"ac": 15})
    case("d20-advantage", "Advantage (rate 1): P(hit) 0.8775, P(crit) 0.0975.", ["§2", "§4.1"],
         plain5([LONGSWORD], [md("advantage", name="Reckless Attack")]), {"ac": 15})
    case("d20-disadvantage", "Disadvantage: P(hit) 0.4225, P(crit) 0.0025.", ["§2"],
         plain5([LONGSWORD], [md("advantage", name="Long range", mode="disadvantage")]), {"ac": 15})
    case("d20-advantage-and-disadvantage-cancel", "One advantage and one disadvantage source: a normal roll.",
         ["§4.1"], plain5([LONGSWORD], [md("advantage", name="A"), md("advantage", name="D", mode="disadvantage")]),
         {"ac": 15}, golden=[("dpr", 5.75, 1e-12)])
    case("d20-advantage-rate-half-shared", "Advantage rate 0.5 per turn: both attacks of a turn share the sample "
         "(a mixture of whole turns).", ["§3.4", "§4.2"],
         plain5([LONGSWORD2], [md("advantage", name="Ally's Help", rate=0.5)]), {"ac": 15})
    case("d20-advantage-and-disadvantage-rates", "Advantage rate 0.5 and disadvantage rate 0.4: four samples, "
         "both-present cancels.", ["§4.2"],
         plain5([LONGSWORD2], [md("advantage", name="Help", rate=0.5),
                               md("advantage", name="Darkness", mode="disadvantage", rate=0.4)]), {"ac": 15})
    case("d20-advantage-filtered", "Advantage applied to one of two attacks by the modifier's attacks filter.",
         ["§3.4"], plain5([LONGSWORD, weapon("Dagger", "1d4", "piercing", ["melee", "light", "finesse"])],
                          [md("advantage", name="Marked", attacks=["Dagger"])]), {"ac": 15})
    rapier = weapon("Rapier", "1d8", "piercing", ["melee", "finesse"], ability="dex")
    dex18 = {"dex": 18}
    case("d20-elven-accuracy-dex", "Elven Accuracy with advantage on a Dex attack: highest of three d20s "
         "(P(hit) 0.957125, P(crit) 0.142625).", ["§2", "§3.4"],
         plain5([rapier], [md("advantage", name="Adv"), md("elven_accuracy")], abilities=dex18), {"ac": 15})
    case("d20-elven-accuracy-str-ignored", "Elven Accuracy does nothing for a Str attack.", ["§3.4"],
         plain5([LONGSWORD], [md("advantage", name="Adv"), md("elven_accuracy")]), {"ac": 15})
    case("d20-elven-accuracy-without-advantage", "Elven Accuracy without advantage: a normal roll.", ["§2"],
         plain5([rapier], [md("elven_accuracy")], abilities=dex18), {"ac": 15})
    case("d20-lucky-normal", "Lucky: reroll a natural 1 (P(hit) 0.6825).", ["§2"],
         plain5([LONGSWORD], [md("lucky")]), {"ac": 15})
    case("d20-lucky-advantage", "Lucky + advantage: reroll one die showing 1 (P(hit) 0.898625).", ["§2"],
         plain5([LONGSWORD], [md("lucky"), md("advantage", name="Adv")]), {"ac": 15})
    case("d20-lucky-disadvantage", "Lucky + disadvantage: 1.1 p^2 = 0.46475 (dndMath's 1.0975 p^2 is bug 2).",
         ["§2"], plain5([LONGSWORD], [md("lucky"), md("advantage", name="Dis", mode="disadvantage")]), {"ac": 15})
    case("d20-lucky-elven-accuracy", "Lucky + Elven Accuracy + advantage: three dice, one 1 rerolled.", ["§2"],
         plain5([rapier], [md("lucky"), md("elven_accuracy"), md("advantage", name="Adv")], abilities=dex18),
         {"ac": 15})
    case("d20-bless-normal", "Bless (+1d4) at +7 vs AC 15: P(hit) 0.775; crit unchanged.", ["§2"],
         plain5([LONGSWORD], [md("to_hit", name="Bless", dice="1d4", concentration=True)]), {"ac": 15})
    case("d20-bless-advantage", "Bless + advantage: P(hit) 0.94625.", ["§2"],
         plain5([LONGSWORD], [md("to_hit", name="Bless", dice="1d4"), md("advantage", name="Adv")]), {"ac": 15})
    case("d20-bless-disadvantage", "Bless + disadvantage: P(hit) 0.60375.", ["§2"],
         plain5([LONGSWORD], [md("to_hit", name="Bless", dice="1d4"),
                              md("advantage", name="Dis", mode="disadvantage")]), {"ac": 15})
    case("d20-bane", "Bane (-1d4 on attack rolls).", ["§2"],
         plain5([LONGSWORD], [md("to_hit", name="Bane", dice="-1d4")]), {"ac": 15})
    case("d20-bless-and-bane", "Bless and Bane together: the two d4s convolve.", ["§2"],
         plain5([LONGSWORD], [md("to_hit", name="Bless", dice="1d4"), md("to_hit", name="Bane", dice="-1d4")]),
         {"ac": 15})
    case("d20-bless-near-crit-floor", "Bless at +5 vs AC 26 with crit 19: bonus dice only matter below the crit "
         "faces.", ["§2"],
         build("Bless floor", 5, [weapon("Longsword", "1d8", "slashing", ["melee"], total=5)],
               [md("crit_range", min=19), md("to_hit", name="Bless", dice="1d4")], abilities={"str": 16}),
         {"ac": 26})
    gs_plain = weapon("Greatsword", "2d6", "slashing", ["melee", "heavy", "two-handed"])
    for base_p, ac in (("050", 18), ("065", 15), ("080", 12)):
        case(f"calibration-p{base_p}-normal", f"Advantage calibration: greatsword 2d6+4 at +7 vs AC {ac}.",
             ["§8.10"], plain5([gs_plain]), {"ac": ac})
        case(f"calibration-p{base_p}-advantage", f"Advantage calibration: with advantage vs AC {ac}.", ["§8.10"],
             plain5([gs_plain], [md("advantage", name="Adv")]), {"ac": ac})
        case(f"calibration-p{base_p}-plus1", f"Advantage calibration: +1 to hit vs AC {ac}.", ["§8.10"],
             plain5([gs_plain], [md("to_hit", name="+1", amount=1)]), {"ac": ac})
        for tag, gold in (("advantage", {"050": 0.527, "065": 0.378, "080": 0.229}),
                          ("plus1", {"050": 0.094, "065": 0.073, "080": 0.060})):
            delta(f"calibration-p{base_p}-{tag}-gain", f"Relative damage gain of {tag} at base P 0.{base_p[1:]}",
                  f"calibration-p{base_p}-{tag}", f"calibration-p{base_p}-normal", golden=gold[base_p], tol=5e-4,
                  refs=["§8.10"])
    # cover
    case("cover-half", "Half cover: +2 AC.", ["§3.5"], ls1, {"ac": 15, "cover": "half"})
    case("cover-three-quarters", "Three-quarters cover: +5 AC.", ["§3.5"], ls1, {"ac": 15, "cover": "three_quarters"})
    longbow = weapon("Longbow", "1d8", "piercing", ["ranged", "heavy", "two-handed"], ability="dex")
    case("cover-ignored-by-ignore-cover", "ignore_cover (2024 Sharpshooter) cancels three-quarters cover.",
         ["§3.4"], plain5([longbow], [md("ignore_cover", name="Sharpshooter")], abilities=dex18),
         {"ac": 15, "cover": "three_quarters"})
    case("style-archery", "Archery fighting style: +2 to hit with a ranged weapon.", ["§3.6"],
         plain5([longbow], abilities=dex18, style="archery"), {"ac": 15})

    # ============================== damage: remaps, riders, types ====================================================
    gs1 = weapon("Greatsword", "2d6", "slashing", ["melee", "heavy", "two-handed"])
    case("dmg-gwf2014-greatsword", "GWF 2014 (reroll 1-2 once) on a 2d6 greatsword, 2014 build.", ["§4.1", "§3.6"],
         build("GWF 2014", 5, [gs1], [], edition="2014", abilities=STR18, style="gwf"), {"ac": 15})
    case("dmg-gwf2024-greatsword", "GWF 2024 (1-2 count as 3) on a 2d6 greatsword.", ["§4.1", "§3.6"],
         build("GWF 2024", 5, [gs1], [], edition="2024", abilities=STR18, style="gwf"), {"ac": 15})
    case("dmg-gwf2014-remap-modifier-glaive", "damage_die_remap gwf2014 as an explicit modifier on a 1d10 glaive.",
         ["§3.4", "§4.1"], plain5([weapon("Glaive", "1d10", "slashing", ["melee", "heavy", "two-handed", "reach"])],
                                  [md("damage_die_remap", remap="gwf2014", attacks=["Glaive"])]), {"ac": 15})
    case("dmg-gwf-sugar-on-versatile", "fighting_style gwf applies to a versatile melee weapon (2024 -> gwf2024).",
         ["§3.6"], plain5([LONGSWORD], style="gwf"), {"ac": 15})
    case("dmg-gwf-sugar-skips-one-handed", "fighting_style gwf does nothing for a one-handed, non-versatile weapon.",
         ["§3.6"], plain5([weapon("Shortsword", "1d6", "piercing", ["melee", "finesse", "light"])], style="gwf"),
         {"ac": 15})
    ids_rider = md("extra_damage", name="Improved Divine Smite", dice="1d8", type="radiant", when="every_hit")
    case("dmg-gwf-not-on-riders-default", "GWF 2014 remaps the weapon's dice only; the 1d8 radiant rider is plain.",
         ["§4.1", "§3.5"], build("GWF rider", 11, [gs1], [ids_rider], edition="2014", abilities=STR18, style="gwf"),
         {"ac": 15}, rulings={"gwf_on_riders": False})
    case("dmg-gwf-on-riders-ruling", "Ruling gwf_on_riders: the rider's dice are remapped too.", ["§4.1", "§3.5"],
         build("GWF rider", 11, [gs1], [ids_rider], edition="2014", abilities=STR18, style="gwf"),
         {"ac": 15}, rulings={"gwf_on_riders": True})
    firebolt = weapon("Fire Bolt", "1d10", "fire", ["ranged", "spell"], ability="int", ability_to_damage=False,
                      cantrip="dice")
    case("dmg-elemental-adept-fire-bolt", "Fire Bolt at level 5 (cantrip dice x2 -> 2d10 fire) with Elemental Adept "
         "(fire): 1 counts as 2 on each die.", ["§4.1", "§3.3"],
         plain5([firebolt], [md("damage_die_remap", name="Elemental Adept", remap="elemental_adept", type="fire")],
                abilities={"int": 18}), {"ac": 15})
    case("dmg-elemental-adept-rider-dice-only", "Elemental Adept (fire) remaps a fire rider's dice, not the "
         "slashing weapon dice.", ["§4.1"],
         plain5([LONGSWORD], [md("extra_damage", name="Flame Tongue", dice="2d6", type="fire"),
                              md("damage_die_remap", remap="elemental_adept", type="fire")]), {"ac": 15})
    hex_rider = md("extra_damage", name="Hex", dice="1d6", type="necrotic", when="every_hit", concentration=True)
    flame = md("extra_damage", name="Flame Tongue", dice="2d6", type="fire", when="every_hit")
    case("dmg-multitype-resist-vuln-immune", "Slashing weapon + necrotic + fire riders vs a target resistant to "
         "slashing, vulnerable to fire, immune to necrotic: each type adjusted separately per hit.", ["§4.1"],
         plain5([LONGSWORD], [hex_rider, flame]),
         {"ac": 15, "resistances": ["slashing"], "vulnerabilities": ["fire"], "immunities": ["necrotic"]})
    case("dmg-multitype-no-adjustments", "The same build vs an ordinary target (control for the case above).",
         ["§4.1"], plain5([LONGSWORD], [hex_rider, flame]), {"ac": 15})
    case("dmg-typeless-never-resisted", "An attack with no damage_type is never resisted.", ["§3.3"],
         plain5([weapon("Mystery", "1d8", None, ["melee"])]),
         {"ac": 15, "resistances": list(DAMAGE_TYPES)})
    case("dmg-resistance-floors-per-hit", "Resistance floors each hit: 1d4+1 piercing vs resistance.", ["§4.1"],
         build("Dagger", 1, [weapon("Dagger", "1d4", "piercing", ["melee", "finesse", "light"], ability="dex")], [],
               abilities={"dex": 12}), {"ac": 12, "resistances": ["piercing"]})
    case("dmg-negative-modifier-floors-at-zero", "Str 6 (-2) with a 1d4 club: faces 1-2 deal 0, never negative.",
         ["§4.1"], build("Weakling", 1, [weapon("Club", "1d4", "bludgeoning", ["melee", "light"])], [],
                         abilities={"str": 6}), {"ac": 10})
    case("dmg-on-crit-rider-added-once", "on_crit rider 1d12 (Brutal Critical style): added once on a crit, not "
         "doubled.", ["§4.1"],
         plain5([gs1], [md("extra_damage", name="Brutal Critical", dice="1d12", when="on_crit")]), {"ac": 15})
    case("dmg-on-miss-rider", "on_miss rider: 2 flat on a miss.", ["§4.2"],
         plain5([LONGSWORD2], [md("extra_damage", name="Miss rider", amount=2, when="on_miss")]), {"ac": 15})
    case("dmg-every-hit-rider-crit-doubles-false", "An every-hit rider with crit_doubles false keeps one set of dice "
         "on a crit.", ["§3.4", "§4.1"],
         plain5([LONGSWORD], [md("extra_damage", name="Flat-ish rider", dice="1d6", type="fire",
                                 crit_doubles=False)]), {"ac": 15})
    rapier2 = weapon("Rapier", "1d8", "piercing", ["melee", "finesse"], count=2, ability="dex")
    sneak = md("extra_damage", name="Sneak Attack", dice="3d6", when="first_hit_per_turn")
    case("dmg-first-hit-per-turn-sneak-attack", "Sneak Attack 3d6 first_hit_per_turn (any_hit) on two rapier "
         "attacks.", ["§4.2"], plain5([rapier2], [sneak], abilities=dex18), {"ac": 15}, riders=True)
    case("dmg-dueling", "Dueling: +2 damage with a one-handed melee weapon.", ["§3.6"],
         plain5([LONGSWORD], style="dueling"), {"ac": 15})
    shortsword = weapon("Shortsword", "1d6", "piercing", ["melee", "finesse", "light"], ability="dex")
    offhand = weapon("Offhand Shortsword", "1d6", "piercing", ["melee", "finesse", "light"], ability="dex",
                     action="bonus_action", offhand=True)
    case("dmg-offhand-no-twf", "Offhand (bonus action) attack adds no positive ability modifier.", ["§3.3"],
         plain5([shortsword, offhand], abilities=dex18), {"ac": 15})
    case("dmg-offhand-twf-style", "Two-Weapon Fighting style: the offhand attack adds the modifier.", ["§3.6"],
         plain5([shortsword, offhand], abilities=dex18, style="twf"), {"ac": 15})
    case("dmg-offhand-negative-modifier", "A negative modifier still applies to the offhand attack.", ["§3.3"],
         plain5([shortsword, offhand], abilities={"dex": 8}), {"ac": 12})
    eb = weapon("Eldritch Blast", "1d10", "force", ["ranged", "spell"], ability="cha", ability_to_damage=False,
                cantrip="beams")
    case("dmg-cantrip-beams-level-11", "Eldritch Blast at level 11: three beams (count x3), each 1d10.", ["§3.3"],
         build("EB 11", 11, [eb], [], abilities={"cha": 20}), {"ac": 17})
    case("dmg-from-level-inactive-attack", "An attack with from_level 6 is inactive at level 5.", ["§3.3"],
         plain5([LONGSWORD, weapon("Extra Swing", "1d8", "slashing", ["melee"], from_level=6)]), {"ac": 15},
         golden=[("dpr", 5.75, 1e-12)])
    case("dmg-bonus-damage-not-doubled", "bonus_damage +3 is flat: added once on a crit (crit 18).", ["§3.4"],
         plain5([LONGSWORD], [md("bonus_damage", name="Flat +3", amount=3), md("crit_range", min=18)]), {"ac": 15})

    # ============================== policies (golden 7) =============================================================
    def smite_build(rider, edition="2014", level=5, attack=LONGSWORD2, extra=()):
        return build("Paladin", level, [attack] + list(extra), [rider], edition=edition, abilities=STR18)

    def smite(policy="any_hit", when="first_hit_per_turn", **kw):
        return md("extra_damage", name="Divine Smite", dice="2d8", type="radiant", when=when, policy=policy, **kw)

    case("policy-first-hit-any-hit", "2d8 radiant once-per-turn rider, any_hit: smite damage 8.505/round, "
         "0.8775 uses, 9.692 per use.", ["§4.2", "§8.7"], smite_build(smite("any_hit")), {"ac": 15}, riders=True,
         golden=[("riders.0.expected_damage", 8.505, 1e-12), ("riders.0.uses_per_round", 0.8775, 1e-12)])
    case("policy-first-hit-crit-or-last", "crit_or_last: 6.885, 0.6675 uses, 10.315 per use.", ["§4.2", "§8.7"],
         smite_build(smite("crit_or_last")), {"ac": 15}, riders=True,
         golden=[("riders.0.expected_damage", 6.885, 1e-12), ("riders.0.uses_per_round", 0.6675, 1e-12)])
    case("policy-first-hit-crits-only", "crits_only: 1.755, 0.0975 uses, 18.0 per use.", ["§4.2", "§8.7"],
         smite_build(smite("crits_only")), {"ac": 15}, riders=True,
         golden=[("riders.0.expected_damage", 1.755, 1e-12), ("riders.0.uses_per_round", 0.0975, 1e-12)])
    case("policy-every-hit-2014-resource-any-hit", "2014 Divine Smite on every hit (resource 4/long rest): 12.6, "
         "1.3 uses.", ["§4.2", "§8.7"],
         smite_build(smite("any_hit", when="every_hit", resource={"uses": 4, "per": "long_rest"})), {"ac": 15},
         riders=True, golden=[("riders.0.expected_damage", 12.6, 1e-12), ("riders.0.uses_per_round", 1.3, 1e-12)])
    for lam, same_as in (("0", "any_hit"), ("7.7", "any_hit"), ("7.73", "crit_or_last"), ("8.99", "crit_or_last"),
                         ("9", "crits_only (exact tie at the last attack: ties do not spend)"),
                         ("9.01", "crits_only"), ("17.99", "crits_only"), ("18.01", "never")):
        gold = {"any_hit": 8.505, "crit_or_last": 6.885, "crits_only": 1.755, "never": 0.0}[same_as.split(" ")[0]]
        case(f"policy-first-hit-optimal-lambda-{lam}",
             f"optimal with use_value {lam}: equals {same_as} (boundaries 2.7/0.35 = 7.714 and 9, then 18).",
             ["§4.2", "§8.7"], smite_build(smite("optimal", use_value=float(lam))), {"ac": 15}, riders=True,
             golden=[("riders.0.expected_damage", gold, 1e-12)],
             notes="Exact tie: E[2d8] - 9 = 0 against a hold value of 0; the contract says ties do not spend."
             if lam == "9" else None)
    case("policy-every-hit-resource-one-use", "Every-hit rider with 1 use: any_hit spends it on the first hit.",
         ["§4.2"], smite_build(smite("any_hit", when="every_hit", resource={"uses": 1, "per": "long_rest"})),
         {"ac": 15}, riders=True)
    for lam in ("8.99", "9.01", "17.99"):
        case(f"policy-every-hit-optimal-lambda-{lam}", f"2014 every-hit smite, ample uses, optimal use_value {lam}: "
             "spend on a hit iff its value exceeds lambda.", ["§4.2"],
             smite_build(smite("optimal", when="every_hit", use_value=float(lam),
                               resource={"uses": 4, "per": "long_rest"})), {"ac": 15}, riders=True)
    case("policy-every-hit-optimal-one-use-lambda-0", "Every-hit smite with 1 use left, optimal lambda 0: backward "
         "induction (spend on a first normal hit: 9 > 6.3).", ["§4.2"],
         smite_build(smite("optimal", when="every_hit", use_value=0,
                           resource={"uses": 1, "per": "long_rest"})), {"ac": 15}, riders=True)
    ls3 = weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=3)
    case("policy-crit-or-last-three-attacks", "crit_or_last with three attacks (level 11): hold normal hits until "
         "the third attack.", ["§4.2"], build("Paladin 11", 11, [ls3], [smite("crit_or_last")], edition="2014",
                                             abilities={"str": 20}), {"ac": 17}, riders=True)
    case("policy-optimal-three-attacks-lambda-0", "optimal (lambda 0) with three attacks: backward induction.",
         ["§4.2"], build("Paladin 11", 11, [ls3], [smite("optimal", use_value=0)], edition="2014",
                         abilities={"str": 20}), {"ac": 17}, riders=True)
    smite24 = md("extra_damage", name="Divine Smite (2024)", dice="2d8", type="radiant", when="first_hit_per_turn",
                 action_cost="bonus_action", resource={"uses": 2, "per": "long_rest"})
    off_ls = weapon("Offhand Dagger", "1d4", "piercing", ["melee", "light", "finesse"], action="bonus_action",
                    offhand=True)
    case("policy-2024-smite-costs-bonus-action-any-hit", "2024 Divine Smite costs the Bonus Action; any_hit spends "
         "it on the first hit and forgoes the offhand attack.", ["§4.2"],
         build("Paladin 2024", 5, [LONGSWORD2, off_ls], [smite24], abilities=STR18), {"ac": 15}, riders=True)
    case("policy-2024-smite-costs-bonus-action-optimal", "Same, optimal (lambda 0): weighs the offhand attack it "
         "gives up.", ["§4.2"],
         build("Paladin 2024", 5, [LONGSWORD2, off_ls], [dict(smite24, policy="optimal", use_value=0)],
               abilities=STR18), {"ac": 15}, riders=True)
    spiritual = weapon("Spiritual Weapon", "1d8", "force", ["melee", "spell"], action="bonus_action")
    for pol, lam, what in (("any_hit", None, "any_hit spends on the first hit and loses the Spiritual Weapon attack"),
                           ("optimal", 0, "optimal lambda 0: a smite (9) still beats the 5.75 bonus-action attack"),
                           ("optimal", 3, "optimal lambda 3: a normal-hit smite nets 6, so it waits for a crit or "
                                          "the last attack (hold value 6.3625 > 6)")):
        rider = dict(smite24, policy=pol)
        if lam is not None:
            rider["use_value"] = lam
        case(f"policy-2024-smite-vs-spiritual-weapon-{pol}{'' if lam is None else '-lambda-' + str(lam)}",
             f"2024 Divine Smite (Bonus Action) vs a Spiritual Weapon bonus-action attack (1d8+4): {what}.",
             ["§4.2"], build("Paladin 2024", 5, [LONGSWORD2, spiritual], [rider], abilities=STR18), {"ac": 15},
             riders=True, pmf=False)

    # ============================== 2014 GWM (golden 2) ==============================================================
    col_pa = {13: 24.30, 14: 21.95, 15: 19.61, 16: 17.27, 17: 15.10, 18: 13.81, 19: 12.52}
    col_no = {13: 19.33, 14: 18.10, 15: 16.87, 16: 15.63, 17: 14.40, 18: 13.17, 19: 11.93}
    for ac in range(13, 20):
        g = [("dpr", col_pa[ac], 0.005)]
        if ac == 15:
            g = [("dpr", 19.611625, 1e-9)]
        case(f"gwm14-pa-auto-crit-ba-ac{ac}", f"2014 L5 fighter, GWF 2014, GWM power attack auto + crit bonus attack, "
             f"vs AC {ac} (PA on at AC <= 16, off at >= 17).", ["§4.2", "§8.2"], fighter14([PA_AUTO, GWM14_BA]),
             {"ac": ac}, golden=g + [("power_attack.0.on", ac <= 16, 0)], pmf=False)
        g = [("dpr", col_no[ac], 0.005)]
        if ac == 15:
            g = [("dpr", 253 / 15, 1e-9)]
        case(f"gwm14-gwf-only-ac{ac}", f"2014 L5 fighter, GWF 2014 only (no feat) vs AC {ac}.", ["§8.2"],
             fighter14([]), {"ac": ac}, golden=g, pmf=False)
    case("gwm14-no-gwf-no-feat-ac15", "2014 L5 fighter without GWF or GWM: 15.00.", ["§8.2"],
         fighter14([], style=None), {"ac": 15}, golden=[("dpr", 15.0, 1e-9)])
    case("gwm14-pa-always-no-ba-ac15", "Power attack always, no crit bonus attack: 18.70.", ["§8.2"],
         fighter14([dict(PA_AUTO, policy="always")]), {"ac": 15}, golden=[("dpr", 18.7, 1e-9)])
    case("gwm14-pa-never-crit-ba-ac15", "Power attack never, crit bonus attack kept.", ["§4.2"],
         fighter14([dict(PA_AUTO, policy="never"), GWM14_BA]), {"ac": 15}, pmf=False)
    case("gwm14-pa-always-crit-ba-ac19", "Power attack forced on at AC 19 (auto would switch it off).", ["§4.2"],
         fighter14([dict(PA_AUTO, policy="always"), GWM14_BA]), {"ac": 19}, pmf=False)
    for ac in (13, 17):
        case(f"gwm14-pa-always-crit-ba-ac{ac}", f"Power attack always + crit bonus attack at AC {ac}.", ["§4.2"],
             fighter14([dict(PA_AUTO, policy="always"), GWM14_BA]), {"ac": ac}, pmf=False)
        case(f"gwm14-pa-never-crit-ba-ac{ac}", f"Power attack never + crit bonus attack at AC {ac}.", ["§4.2"],
             fighter14([dict(PA_AUTO, policy="never"), GWM14_BA]), {"ac": ac}, pmf=False)
    case("gwm14-asi-str20-ac15", "The ASI alternative (Str 20, no feat): 19.50.", ["§8.2"],
         fighter14([], str_score=20), {"ac": 15}, golden=[("dpr", 19.5, 1e-9)])
    case("gwm14-pa-auto-advantage-rate-ac17", "PA auto decided after the advantage sample: at AC 17 on with "
         "advantage, off without (rate 0.5).", ["§4.2"],
         fighter14([PA_AUTO, GWM14_BA, md("advantage", name="Reckless (sometimes)", rate=0.5)]), {"ac": 17},
         pmf=False)
    delta("gwm14-feat-vs-no-feat-ac15", "2014 GWM (PA + crit BA) minus GWF-only at AC 15: +2.745",
          "gwm14-pa-auto-crit-ba-ac15", "gwm14-gwf-only-ac15", golden=2.745, tol=5e-4, refs=["§8.2"])
    delta("gwm14-feat-vs-asi-ac15", "2014 GWM minus the Str 20 ASI at AC 15: +0.1116",
          "gwm14-pa-auto-crit-ba-ac15", "gwm14-asi-str20-ac15", golden=0.1116, tol=5e-5, refs=["§8.2"])

    # ============================== 2024 GWM (golden 3) and Savage Attacker (golden 4) ==============================
    col24 = {13: 26.31, 14: 25.18, 15: 24.04, 16: 22.90, 17: 21.76, 18: 20.62, 19: 19.48}
    col_asi = {13: 2.71, 14: 2.38, 15: 2.04, 16: 1.70, 17: 1.36, 18: 1.02, 19: 0.68}
    sa_off = {13: 0.8536, 14: 0.8285, 15: 0.7990, 16: 0.7648, 17: 0.7261, 18: 0.6829, 19: 0.6351}
    sa_on = {13: 0.88, 14: 0.85, 15: 0.83, 16: 0.79, 17: 0.75, 18: 0.71, 19: 0.67}
    for ac in range(13, 20):
        g = [("dpr", 24.036, 1e-9)] if ac == 15 else [("dpr", col24[ac], 0.005)]
        case(f"gwm24-hew-ac{ac}", f"2024 L5 fighter (Str 19, GWF 2024, Graze, GWM +PB, Hew) vs AC {ac}.",
             ["§8.3"], fighter24([HWM, HEW]), {"ac": ac}, golden=g, pmf=False)
        case(f"gwm24-asi-str20-ac{ac}", f"2024 L5 fighter with the Str 20 ASI instead of GWM vs AC {ac}.", ["§8.3"],
             fighter24([], str_score=20), {"ac": ac}, golden=[("dpr", 22.0, 1e-9)] if ac == 15 else [], pmf=False)
        delta(f"gwm24-feat-vs-asi-ac{ac}", f"2024 GWM + Hew minus the ASI at AC {ac}", f"gwm24-hew-ac{ac}",
              f"gwm24-asi-str20-ac{ac}", golden=2.036 if ac == 15 else col_asi[ac], tol=1e-9 if ac == 15 else 0.005,
              refs=["§8.3"])
        case(f"gwm24-savage-attacker-ac{ac}", f"Build 3 + Savage Attacker (any_hit), default ruling (crit: one set "
             f"rerolled), vs AC {ac}.", ["§4.1", "§8.4"], fighter24([HWM, HEW, SAVAGE]), {"ac": ac}, riders=True,
             pmf=False)
        case(f"gwm24-savage-attacker-crit-dice-ac{ac}", f"Build 3 + Savage Attacker with savage_attacker_on_crit_dice "
             f"vs AC {ac}.", ["§4.1", "§8.4"], fighter24([HWM, HEW, SAVAGE]), {"ac": ac},
             rulings={"savage_attacker_on_crit_dice": True}, riders=True, pmf=False)
        delta(f"savage-attacker-gain-ac{ac}", f"Savage Attacker gain (flag off) at AC {ac}",
              f"gwm24-savage-attacker-ac{ac}", f"gwm24-hew-ac{ac}",
              golden=0.798958333 if ac == 15 else sa_off[ac], tol=1e-9 if ac == 15 else 5e-5, refs=["§8.4"])
        delta(f"savage-attacker-gain-crit-dice-ac{ac}", f"Savage Attacker gain (flag on) at AC {ac}",
              f"gwm24-savage-attacker-crit-dice-ac{ac}", f"gwm24-hew-ac{ac}",
              golden=0.825250 if ac == 15 else sa_on[ac], tol=5e-7 if ac == 15 else 0.005, refs=["§8.4"])
    case("gwm24-hew-gets-pb", "Build 3 with ruling hew_gets_pb: 24.226125.", ["§4.2", "§8.3"],
         fighter24([HWM, HEW]), {"ac": 15}, rulings={"hew_gets_pb": True}, golden=[("dpr", 24.226125, 1e-9)],
         pmf=False)
    case("gwm24-hew-gets-pb-not-riders", "hew_gets_pb adds only attack_action_only FLAT bonuses to Hew: an "
         "attack_action_only 1d6 fire rider still skips Hew.", ["§4.2"],
         fighter24([HWM, HEW, md("extra_damage", name="Action-only fire", dice="1d6", type="fire",
                                  attack_action_only=True)]), {"ac": 15}, rulings={"hew_gets_pb": True}, pmf=False)
    case("gwm24-no-feat-str18", "2024 L5 fighter without GWM (Str 18): 19.20.", ["§8.3"],
         fighter24([], str_score=18), {"ac": 15}, golden=[("dpr", 19.2, 1e-9)], pmf=False)
    delta("gwm24-feat-vs-no-feat-ac15", "2024 GWM + Hew minus no feat: +4.836", "gwm24-hew-ac15",
          "gwm24-no-feat-str18", golden=4.836, tol=1e-9, refs=["§8.3"])
    case("gwm24-no-hew-gwf2024", "Build 3 without Hew (the component baseline): 23.10.", ["§8.3"],
         fighter24([HWM]), {"ac": 15}, golden=[("dpr", 23.1, 1e-9)], pmf=False)
    case("gwm24-no-hew-no-gwf", "Build 3 without Hew and without GWF.", ["§8.3"], fighter24([HWM], style=None),
         {"ac": 15}, pmf=False)
    case("gwm24-no-hew-gwf2014-remap", "Build 3 without Hew, GWF 2014 remap instead of 2024.", ["§8.3"],
         fighter24([HWM, md("damage_die_remap", name="GWF 2014", remap="gwf2014", attacks=["Greatsword"])],
                   style=None), {"ac": 15}, pmf=False)
    case("gwm24-no-hew-no-graze", "Build 3 without Hew and without Graze.", ["§8.3"],
         fighter24([HWM], attack=GREATSWORD2), {"ac": 15}, golden=[("dpr", 20.3, 1e-9)], pmf=False)
    delta("gwm24-component-gwf2024", "GWF 2024 component (two Attack-action swings, no Hew): +1.40",
          "gwm24-no-hew-gwf2024", "gwm24-no-hew-no-gwf", golden=1.40, tol=1e-9, refs=["§8.3"])
    delta("gwm24-component-gwf2014", "GWF 2014 component (two swings, no Hew): +1.8667", "gwm24-no-hew-gwf2014-remap",
          "gwm24-no-hew-no-gwf", golden=1.8667, tol=5e-5, refs=["§8.3"])
    delta("gwm24-component-graze", "Graze component (two swings, no Hew): +2.80", "gwm24-no-hew-gwf2024",
          "gwm24-no-hew-no-graze", golden=2.80, tol=1e-9, refs=["§8.3"])
    ls24 = weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=2)
    case("savage-attacker-plain-longsword", "Savage Attacker (any_hit) on two plain 1d8+4 longsword attacks.",
         ["§4.1"], plain5([ls24], [SAVAGE]), {"ac": 15}, riders=True)
    case("savage-attacker-crits-only", "Savage Attacker with policy crits_only (flag off: a crit rerolls one set).",
         ["§4.1", "§4.2"], plain5([ls24], [dict(SAVAGE, policy="crits_only")]), {"ac": 15}, riders=True)
    case("savage-attacker-gwf2014", "Savage Attacker over GWF 2014 remapped dice (2d6 greatsword, 1 attack).",
         ["§4.1"], plain5([gs1], [SAVAGE, md("damage_die_remap", remap="gwf2014", attacks=["Greatsword"])]),
         {"ac": 15}, riders=True)
    case("savage-attacker-not-on-spell-attacks", "Savage Attacker needs a WEAPON hit: no effect on a spell attack.",
         ["§3.4"], plain5([weapon("Fire Bolt", "1d10", "fire", ["ranged", "spell"], ability="int",
                                  ability_to_damage=False)], [SAVAGE], abilities={"int": 18}), {"ac": 15})
    for lam in ("0.5", "1.0", "1.4"):
        case(f"savage-attacker-optimal-crit-dice-lambda-{lam}", f"Savage Attacker optimal (use_value {lam}) with the "
             "crit-dice ruling on build 3: per-hit gain 0.9105 on a normal hit, 1.3 on a crit.", ["§4.2"],
             fighter24([HWM, HEW, dict(SAVAGE, policy="optimal", use_value=float(lam))]), {"ac": 15},
             rulings={"savage_attacker_on_crit_dice": True}, riders=True, pmf=False)

    # ============================== masteries =======================================================================
    case("mastery-graze-negative-modifier", "Graze with Str 8: a non-positive modifier deals nothing on a miss.",
         ["§4.1"], build("Weak graze", 5, [weapon("Greatsword", "2d6", "slashing", ["melee", "heavy", "two-handed"],
                                                  mastery="graze")], [], abilities={"str": 8}), {"ac": 12})
    case("mastery-graze-resisted", "Graze damage is the weapon's type, so resistance halves it.", ["§4.1"],
         fighter24([], str_score=18), {"ac": 15, "resistances": ["slashing"]}, pmf=False)
    rapier1 = weapon("Rapier", "1d8", "piercing", ["melee", "finesse"], ability="dex", mastery="vex")
    vex1 = build("Vex duelist", 4, [rapier1], [], abilities=dex18)
    for rounds in (1, 2, 3, 4):
        case(f"mastery-vex-one-attack-fight-r{rounds}", f"Vex, one attack per turn, fight of {rounds} round(s): "
             "a damaging hit gives the next attack (next turn) advantage.", ["§4.1", "§4.4", "§8.6"], vex1,
             {"ac": 14}, horizon=fight_h(rounds))
    rapier2v = weapon("Rapier", "1d8", "piercing", ["melee", "finesse"], count=2, ability="dex", mastery="vex")
    vex2 = build("Vex duelist", 5, [rapier2v], [], abilities=dex18)
    for rounds in (1, 2, 3, 4):
        case(f"mastery-vex-two-attacks-fight-r{rounds}", f"Vex, two attacks per turn, fight of {rounds} round(s).",
             ["§4.1", "§4.4"], vex2, {"ac": 15}, horizon=fight_h(rounds))
    case("mastery-vex-round1", "Vex within one turn (round1 horizon): the second attack may have advantage.",
         ["§4.1"], vex2, {"ac": 15})
    case("mastery-vex-feeds-next-weapon", "Vex on a shortsword gives advantage to the following dagger attack.",
         ["§4.1"], plain5([weapon("Shortsword", "1d6", "piercing", ["melee", "finesse", "light"], ability="dex",
                                  mastery="vex"),
                           weapon("Dagger", "1d4", "piercing", ["melee", "finesse", "light"], ability="dex")],
                          abilities=dex18), {"ac": 15})
    case("mastery-vex-needs-damage", "Vex with 1d4-1 (Dex 8): a normal hit rolling 1 deals 0 and grants no "
         "advantage.", ["§4.1"],
         build("Feeble vex", 5, [weapon("Dagger", "1d4", "piercing", ["melee", "finesse", "light"], count=2,
                                         ability="dex", mastery="vex")], [], abilities={"dex": 8}), {"ac": 10})
    case("mastery-vex-redundant-with-advantage", "Vex adds nothing when every attack already has advantage.",
         ["§4.1"], build("Vex + adv", 5, [rapier2v], [md("advantage", name="Adv")], abilities=dex18), {"ac": 15})
    maul3 = weapon("Maul", "2d6", "bludgeoning", ["melee", "heavy", "two-handed"], count=3, mastery="topple")
    case("mastery-topple-three-attacks", "Topple (Con save vs 8 + 4 + 4 = 16): a failed save knocks the target "
         "prone for the rest of the turn, giving later melee attacks advantage.", ["§4.1"],
         build("Topple fighter", 11, [maul3], [], abilities=STR18), {"ac": 17, "saves": {"con": 3}})
    case("mastery-topple-with-bonus-action-attack", "Topple then a bonus-action melee attack that benefits from "
         "prone.", ["§4.1", "§4.2"],
         build("Topple + BA", 5, [weapon("Maul", "2d6", "bludgeoning", ["melee", "heavy", "two-handed"], count=2,
                                         mastery="topple"),
                                  weapon("Pommel Strike", "1d4", "bludgeoning", ["melee"], action="bonus_action")],
               [], abilities=STR18), {"ac": 15, "saves": {"con": 2}})
    axe2 = weapon("Greataxe", "1d12", "slashing", ["melee", "heavy", "two-handed"], count=2, mastery="cleave")
    case("mastery-cleave-rate-half", "Cleave: once per turn on the first melee hit, with probability 0.5 an attack "
         "on a second creature, no ability modifier on its damage.", ["§4.1"],
         build("Cleaver", 5, [axe2], [], abilities=STR18), {"ac": 15, "second_target_rate": 0.5})
    case("mastery-cleave-rate-one", "Cleave with a second creature always adjacent.", ["§4.1"],
         build("Cleaver", 5, [axe2], [], abilities=STR18), {"ac": 15, "second_target_rate": 1})
    case("mastery-cleave-rate-zero", "Cleave with no second creature: plain attacks.", ["§4.1"],
         build("Cleaver", 5, [axe2], [], abilities=STR18), {"ac": 15})
    cleave_gwm = build("Cleaver GWM", 5, [axe2], [HWM], abilities={"str": 19})
    case("mastery-cleave-gwm-not-attack-action", "Cleave + 2024 GWM +PB: the Cleave attack is not part of the "
         "Attack action (default), so no +PB on it.", ["§4.1", "§4.2", "§3.5"], cleave_gwm,
         {"ac": 15, "second_target_rate": 0.5}, rulings={"cleave_part_of_attack_action": False})
    case("mastery-cleave-gwm-ruling-attack-action", "Ruling cleave_part_of_attack_action: the Cleave attack gets "
         "+PB.", ["§4.1", "§3.5"], cleave_gwm, {"ac": 15, "second_target_rate": 0.5},
         rulings={"cleave_part_of_attack_action": True})
    case("mastery-cleave-takes-every-hit-rider", "The Cleave attack takes every-hit riders (1d6 fire).", ["§4.1"],
         build("Cleaver", 5, [axe2], [md("extra_damage", name="Flame", dice="1d6", type="fire")],
               abilities=STR18), {"ac": 15, "second_target_rate": 0.5})
    case("mastery-cleave-unspent-crits-only-rider", "A crits_only once-per-turn rider still unspent can land on the "
         "Cleave attack's crit.", ["§4.1", "§4.2"],
         build("Cleaver", 5, [axe2], [md("extra_damage", name="Crit rider", dice="2d8", type="radiant",
                                         when="first_hit_per_turn", policy="crits_only")], abilities=STR18),
         {"ac": 15, "second_target_rate": 0.5}, riders=True, pmf=False)
    case("mastery-sap-no-dpr", "Sap has no DPR effect (defensive note only).", ["§4.1"],
         plain5([weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], mastery="sap")]), {"ac": 15},
         golden=[("dpr", 5.75, 1e-12)])
    case("mastery-nick-push-slow-no-dpr", "Nick, Push and Slow have no DPR effect.", ["§4.1"],
         plain5([weapon("Dagger", "1d4", "piercing", ["melee", "light"], mastery="nick"),
                 weapon("Warhammer", "1d8", "bludgeoning", ["melee", "versatile"], mastery="push"),
                 weapon("Club", "1d4", "bludgeoning", ["melee", "light"], mastery="slow")]), {"ac": 15})

    # ============================== conditions ======================================================================
    case("cond-target-prone-melee", "Prone target: melee attacks have advantage.", ["§4.1"], ls1,
         {"ac": 15, "condition": "prone"})
    case("cond-target-prone-ranged", "Prone target: ranged attacks have disadvantage.", ["§4.1"],
         plain5([longbow], abilities=dex18), {"ac": 15, "condition": "prone"})
    case("cond-target-paralyzed-melee-autocrit", "Paralyzed target: advantage, and every melee hit is a crit.",
         ["§4.1"], ls1, {"ac": 15, "condition": "paralyzed"})
    case("cond-target-paralyzed-ranged-no-autocrit", "Paralyzed target: a ranged attack has advantage but no "
         "automatic crit.", ["§4.1"], plain5([longbow], abilities=dex18), {"ac": 15, "condition": "paralyzed"})
    case("cond-target-unconscious", "Unconscious target: advantage and melee autocrit.", ["§4.1"], ls1,
         {"ac": 15, "condition": "unconscious"})
    case("cond-target-restrained", "Restrained target: attacks have advantage.", ["§4.1"], ls1,
         {"ac": 15, "condition": "restrained"})
    case("cond-target-blinded", "Blinded target: attacks have advantage.", ["§4.1"], ls1,
         {"ac": 15, "condition": "blinded"})
    case("cond-target-dodging", "Dodging target: attacks have disadvantage.", ["§4.1"], ls1,
         {"ac": 15, "condition": "dodging"})
    case("cond-dodging-cancels-advantage", "Dodging target + an advantage source: normal roll.", ["§4.1"],
         plain5([LONGSWORD], [md("advantage", name="Adv")]), {"ac": 15, "condition": "dodging"},
         golden=[("dpr", 5.75, 1e-12)])
    monk_mods = [md("condition_on_hit", name="Stunning Strike", condition="stunned", ability="con", dc=14)]
    monk = build("2014 Monk 5", 5,
                 [weapon("Unarmed Strike", "1d6", "bludgeoning", ["melee"], count=2, ability="dex"),
                  weapon("Flurry of Blows", "1d6", "bludgeoning", ["melee"], count=2, ability="dex",
                         action="bonus_action")], monk_mods, edition="2014", abilities={"dex": 18, "wis": 16})
    case("cond-stunning-strike-every-hit", "Stunning Strike on every hit until the target is stunned (Con DC 14 vs "
         "+2: fail 0.55); stunned grants advantage for the rest of the turn.", ["§4.1", "§4.2"], monk,
         {"ac": 15, "saves": {"con": 2}}, pmf=False)
    case("cond-stunning-strike-first-hit", "Stunning Strike once per turn (first_hit_per_turn).", ["§4.2"],
         build("2014 Monk 5", 5, monk["attacks"], [dict(monk_mods[0], when="first_hit_per_turn")], edition="2014",
               abilities={"dex": 18, "wis": 16}), {"ac": 15, "saves": {"con": 2}}, pmf=False)
    case("cond-stunning-strike-default-dc", "condition_on_hit with no dc: 8 + PB + the attack's ability modifier "
         "(8 + 3 + 4 = 15).", ["§3.4"],
         build("2014 Monk 5", 5, monk["attacks"],
               [md("condition_on_hit", name="Stun", condition="stunned", ability="con")],
               edition="2014", abilities={"dex": 18, "wis": 16}), {"ac": 15, "saves": {"con": 2}}, pmf=False)
    case("cond-paralyze-on-first-hit-autocrit", "A paralyzing first hit (Con DC 15 vs +1): later melee hits this "
         "turn are crits.", ["§4.1"],
         plain5([weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=3)],
                [md("condition_on_hit", name="Paralyzing Touch", condition="paralyzed", ability="con", dc=15,
                    when="first_hit_per_turn")]), {"ac": 15, "saves": {"con": 1}}, pmf=False)
    case("cond-magic-resistance-on-magical-condition", "A magical condition_on_hit vs Magic Resistance: the save "
         "has advantage.", ["§4.1", "§4.3"],
         plain5([weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=3)],
                [md("condition_on_hit", name="Paralyzing Touch", condition="paralyzed", ability="con", dc=15,
                    when="first_hit_per_turn", magical=True)]),
         {"ac": 15, "saves": {"con": 1}, "magic_resistance": True}, pmf=False)

    # ============================== save effects ====================================================================
    fireball = md("save_effect", name="Fireball", ability="dex", dc=15, dice="8d6", type="fire", shape="sphere",
                  size=20)
    wiz14 = build("2014 Wizard 5", 5, [], [fireball], edition="2014", abilities={"int": 16})
    goblins = {"saves": {"dex": 2}, "hp": 7}
    case("save-fireball-goblins-2014", "Fireball (DC 15, 8d6, sphere 20 -> 4 targets) vs 2014 goblins (Dex +2, "
         "HP 7): shared damage roll, independent saves.", ["§4.3", "§8.8"], wiz14, goblins, pmf=False,
         golden=[("dpr", 89.2, 1e-9), ("save.per_target_fail", 0.6, 1e-12), ("save.raw", 89.2, 1e-9),
                 ("save.effective", 27.998608, 5e-7), ("save.p_each_dies", 0.9996935, 5e-8),
                 ("save.p_all_die", 0.999333059, 5e-10), ("save.expected_kills", 3.998774, 5e-7)])
    case("save-fireball-goblin-warriors-2024", "Fireball vs 2024 goblin warriors (HP 10).", ["§4.3", "§8.8"],
         build("2024 Wizard 5", 5, [], [fireball], abilities={"int": 16}), {"saves": {"dex": 2}, "hp": 10},
         pmf=False, golden=[("save.p_each_dies", 0.9844489, 5e-8), ("save.p_all_die", 0.9661672, 5e-8),
                            ("save.effective", 39.908441, 5e-7)])
    case("save-fireball-magic-resistance", "Magic Resistance: advantage on the save (F 0.36).", ["§4.3"], wiz14,
         dict(goblins, magic_resistance=True), pmf=False, golden=[("save.per_target_fail", 0.36, 1e-12)])
    case("save-fireball-not-magical", "magical false: Magic Resistance does not apply.", ["§4.3"],
         build("Alchemist", 5, [], [dict(fireball, magical=False, name="Alchemist's Fire Bomb")], edition="2014"),
         dict(goblins, magic_resistance=True), pmf=False, golden=[("save.per_target_fail", 0.6, 1e-12)])
    case("save-fireball-evasion", "Evasion: success -> 0, failure -> half.", ["§4.3"], wiz14,
         dict(goblins, evasion=True), pmf=False)
    case("save-fireball-stunned-autofail", "Stunned targets fail Dex saves automatically.", ["§4.3", "§4.1"], wiz14,
         dict(goblins, condition="stunned"), pmf=False, golden=[("save.per_target_fail", 1.0, 0)])
    case("save-fireball-restrained-disadvantage", "Restrained: disadvantage on Dex saves.", ["§4.1"], wiz14,
         dict(goblins, condition="restrained"), pmf=False)
    case("save-fireball-dodging-advantage", "Dodging: advantage on Dex saves.", ["§4.1"], wiz14,
         dict(goblins, condition="dodging"), pmf=False)
    case("save-fireball-half-cover", "Half cover: +2 to Dex saves (F 0.5).", ["§4.3"], wiz14,
         dict(goblins, cover="half"), pmf=False, golden=[("save.per_target_fail", 0.5, 1e-12)])
    case("save-fireball-resistant", "Fire resistance after halving on a save: floor(floor(x/2)/2).", ["§4.1"],
         wiz14, dict(goblins, resistances=["fire"], hp=20), pmf=False)
    case("save-fireball-elemental-adept", "Elemental Adept (fire): each d6 treats 1 as 2.", ["§4.1"],
         build("2014 Wizard 5", 5, [], [fireball, md("damage_die_remap", remap="elemental_adept", type="fire")],
               edition="2014", abilities={"int": 16}), goblins, pmf=False)
    case("save-fireball-bane-on-saves", "save_dice -1d4 (Bane on the targets' saves).", ["§3.5"], wiz14,
         dict(goblins, save_dice="-1d4"), pmf=False)
    case("save-damage-effect-ignores-legendary-resistance", "A damage-only effect vs Legendary Resistance 3: LR is "
         "assumed not spent (same as without LR).", ["§4.3"], wiz14, dict(goblins, legendary_resistance=3), pmf=False,
         golden=[("dpr", 89.2, 1e-9)])
    case("save-stunned-con-save-not-autofail", "Stunned auto-fails only Str and Dex saves: a Con save is rolled.",
         ["§4.1", "§4.3"],
         build("Wizard 5", 5, [], [md("save_effect", name="Thunder Burst", ability="con", dc=15, dice="3d8",
                                      type="thunder", targets=2)], abilities={"int": 16}),
         {"saves": {"con": 2}, "hp": 12, "condition": "stunned"}, pmf=False,
         golden=[("save.per_target_fail", 0.6, 1e-12)])
    case("save-paralyzed-str-save-autofail", "Paralyzed: a Str save fails automatically.", ["§4.1", "§4.3"],
         build("Wizard 5", 5, [], [md("save_effect", name="Crush", ability="str", dc=12, dice="2d10",
                                      type="bludgeoning", on_success="none")], abilities={"int": 16}),
         {"saves": {"str": 6}, "hp": 11, "condition": "paralyzed"}, golden=[("save.per_target_fail", 1.0, 0)])
    case("save-legendary-resistance-hold-person", "Hold Person style condition (DC 15 vs +2: F 0.6) vs Legendary "
         "Resistance 3: expected casts to land (3 + 1) / 0.6 = 6.6667; no damage.", ["§4.3", "§8.9"],
         build("Enchanter", 5, [], [md("save_effect", name="Hold Person", ability="wis", dc=15,
                                       condition="paralyzed")]),
         {"saves": {"wis": 2}, "legendary_resistance": 3}, pmf=False,
         golden=[("dpr", 0.0, 0), ("save.expected_casts_to_land", 20 / 3, 1e-12)])
    sacred = md("save_effect", name="Sacred Flame", ability="dex", dc_ability="wis", dice="1d8", type="radiant",
                on_success="none", cantrip=True, targets=1)
    case("save-sacred-flame-cantrip", "Sacred Flame at level 5: cantrip dice x2 (2d8), DC 8 + 3 + 3 = 14, no damage "
         "on a success, single target with HP 9.", ["§3.4", "§4.3"],
         build("Cleric 5", 5, [], [sacred], abilities={"wis": 16}), {"saves": {"dex": 2}, "hp": 9})
    case("save-cone-six-targets", "Cone 60 ft -> 6 targets (DMG p.249), Con save half, vs HP 30 and cold "
         "resistance.", ["§4.3"],
         build("Wizard 9", 9, [], [md("save_effect", name="Cone of Cold", ability="con", dc=17, dice="8d8",
                                      type="cold", shape="cone", size=60)], abilities={"int": 20}),
         {"saves": {"con": 3}, "hp": 30, "resistances": ["cold"]}, pmf=False)
    case("save-line-four-targets", "Line 100 ft -> ceil(100/30) = 4 targets.", ["§4.3"],
         build("Wizard 5", 5, [], [md("save_effect", name="Lightning Bolt", ability="dex", dc=15, dice="8d6",
                                      type="lightning", shape="line", size=100)], abilities={"int": 16}),
         {"saves": {"dex": 1}, "hp": 22}, pmf=False)
    case("save-dc-ability-and-flat-amount", "dc_ability + dc_bonus and a flat amount: DC 8 + 3 + 4 + 1 = 16, "
         "3d6+4 thunder, cube 10 -> 2 targets.", ["§3.4", "§4.3"],
         build("Sorcerer 5", 5, [], [md("save_effect", name="Blast", ability="con", dc_ability="cha", dc_bonus=1,
                                       dice="3d6", amount=4, type="thunder", shape="cube", size=10)],
               abilities={"cha": 18}), {"saves": {"con": 1}, "hp": 12}, pmf=False)
    case("save-bonus-action-effect-vs-bonus-action-attack", "The Bonus Action goes to the better of a bonus-action "
         "save effect (2d6 fire, Dex DC 13) and an offhand attack.", ["§4.2"],
         build("Breath + blade", 5, [shortsword, offhand],
               [md("save_effect", name="Fire Breath", ability="dex", dc=13, dice="2d6", type="fire",
                   action_cost="bonus_action", targets=1)], abilities=dex18),
         {"ac": 15, "saves": {"dex": 1}}, pmf=False)

    # ============================== horizons ========================================================================
    b2_as = fighter14([PA_AUTO, GWM14_BA, ACTION_SURGE])
    case("horizon-action-surge-round1", "Action Surge in the round-1 horizon: four greatsword attacks, PA auto, "
         "crit bonus attack.", ["§4.2", "§4.4"], b2_as, {"ac": 15}, pmf=False)
    case("horizon-action-surge-fight-r3", "Action Surge in a 3-round fight: spent in round 1 only.", ["§4.4"],
         b2_as, {"ac": 15}, horizon=fight_h(3))
    case("horizon-action-surge-day-e6", "Day horizon, E 6, S 2 -> U 3: marginal per use 19.5227 -> +3.254 over "
         "the fight DPR without Action Surge.", ["§4.4", "§8.12"], b2_as, {"ac": 15}, horizon=day_h(3, 6, 2),
         golden=[("day.features.0.contribution", 3.254, 5e-4)])
    case("horizon-action-surge-day-e8", "Day horizon, E 8, S 2: +2.440.", ["§4.4", "§8.12"], b2_as, {"ac": 15},
         horizon=day_h(3, 8, 2), golden=[("day.features.0.contribution", 2.440, 5e-4)])
    case("horizon-action-surge-2024-day-e4-s1", "2024 build 3 + Action Surge, E 4, S 1 -> U 2: +3.991.",
         ["§4.4", "§8.12"], fighter24([HWM, HEW, ACTION_SURGE]), {"ac": 15}, horizon=day_h(3, 4, 1),
         golden=[("day.features.0.contribution", 3.991, 5e-4)])
    case("horizon-action-surge-day-dmg2014-preset", "Day preset dmg2014 (E 7, S 2).", ["§4.4"], b2_as, {"ac": 15},
         horizon=day_h(3, 7, 2, "dmg2014"))
    case("horizon-action-surge-day-light-preset", "Day preset light (E 3.5, S 1).", ["§4.4"], b2_as, {"ac": 15},
         horizon=day_h(3, 3.5, 1, "light"))
    hexer = build("Hexblade-ish", 5, [shortsword, offhand],
                  [md("extra_damage", name="Hex", dice="1d6", type="necrotic", when="every_hit", concentration=True,
                      setup="bonus_action")], abilities=dex18)
    case("horizon-hex-setup-round1", "Hex with setup bonus_action in round 1: the offhand attack is lost that "
         "round, Hex applies to the round's attacks.", ["§4.2", "§4.4"], hexer, {"ac": 15})
    case("horizon-hex-setup-fight-r3", "Same, 3-round fight: the setup cost is paid in round 1 only.", ["§4.4"],
         hexer, {"ac": 15}, horizon=fight_h(3))
    case("horizon-setup-action", "A setup: action modifier costs round 1's Action (no Attack action) in a 2-round "
         "fight.", ["§4.2", "§4.4"],
         build("Buffer", 5, [LONGSWORD2], [md("extra_damage", name="Magic Weapon", amount=2, setup="action",
                                              concentration=True)], abilities=STR18), {"ac": 15},
         horizon=fight_h(2))
    smite_every = smite("any_hit", when="every_hit", resource={"uses": 3, "per": "long_rest"})
    case("horizon-smite-every-hit-fight-r3-any-hit", "2014 smite on every hit with 3 uses over a 3-round fight: "
         "uses run out.", ["§4.4"], smite_build(smite_every), {"ac": 15}, horizon=fight_h(3), riders=True)
    case("horizon-smite-every-hit-fight-r3-crits-only", "Same with crits_only.", ["§4.4"],
         smite_build(dict(smite_every, policy="crits_only")), {"ac": 15}, horizon=fight_h(3), riders=True)
    case("horizon-smite-every-hit-fight-r3-optimal", "Same with optimal (lambda 0): per-turn decisions (see the "
         "alternative for full-fight backward induction).", ["§4.2", "§4.4"],
         smite_build(dict(smite_every, policy="optimal", use_value=0)), {"ac": 15}, horizon=fight_h(3), riders=True)
    case("horizon-smite-day", "2014 smite (3 uses per long rest) amortized over a dmg2014 day.", ["§4.4"],
         smite_build(smite_every), {"ac": 15}, horizon=day_h(3, 7, 2, "dmg2014"))
    case("horizon-two-limited-features-day", "Action Surge and a limited smite together over a day: additive "
         "amortization.", ["§4.4"],
         build("Paladin-fighter", 5, [GREATSWORD2],
               [PA_AUTO, GWM14_BA, ACTION_SURGE,
                md("extra_damage", name="Divine Smite", dice="2d8", type="radiant", when="every_hit",
                   resource={"uses": 2, "per": "long_rest"})], edition="2014", abilities=STR18, style="gwf"),
         {"ac": 15}, horizon=day_h(3, 6, 2))
    case("horizon-fight-without-state-equals-round1", "A build with no carried state: fight DPR = round-1 DPR.",
         ["§4.4"], fighter14([PA_AUTO, GWM14_BA]), {"ac": 15}, horizon=fight_h(3),
         golden=[("dpr", 19.611625, 1e-9)])

    # ============================== economy: bonus action and reaction =============================================
    ls2 = weapon("Longsword", "1d8", "slashing", ["melee", "versatile"], count=2)
    bash = weapon("Shield Bash", "1d4", "bludgeoning", ["melee"], action="bonus_action")
    case("econ-bonus-action-attack-vs-hew", "Bonus Action: a crit-triggered extra longsword attack (better) beats "
         "the every-turn shield bash when a crit happened.", ["§4.2"],
         build("Choice", 5, [ls2, bash], [md("extra_attack", name="Crit follow-up", attack="Longsword",
                                              action="bonus_action", trigger="crit")], abilities=STR18), {"ac": 15})
    case("econ-hit-trigger-extra-attack", "An extra attack triggered by any hit vs a weaker bonus-action attack.",
         ["§4.2"], build("Choice", 5, [ls2, bash], [md("extra_attack", name="On-hit follow-up", attack="Longsword",
                                                         action="bonus_action", trigger="hit")], abilities=STR18),
         {"ac": 15})
    big_bash = weapon("Big Bash", "3d8", "bludgeoning", ["melee"], action="bonus_action")
    case("econ-bonus-action-attack-beats-trigger", "When the every-turn bonus-action attack is better, it is taken "
         "even after a crit.", ["§4.2"],
         build("Choice", 5, [ls2, big_bash], [md("extra_attack", name="Crit follow-up", attack="Longsword",
                                                  action="bonus_action", trigger="crit")], abilities=STR18),
         {"ac": 15})
    case("econ-always-extra-attack-vs-bonus-action-attack", "An always-available bonus-action extra attack (the "
         "glaive again) vs a weaker bonus-action attack: the better one every turn.", ["§4.2"],
         build("Choice", 5, [weapon("Glaive", "1d10", "slashing", ["melee", "heavy", "two-handed", "reach"],
                                    count=2), bash],
               [md("extra_attack", name="Glaive follow-up", attack="Glaive", action="bonus_action", trigger="always")],
               abilities=STR18), {"ac": 15}, pmf=False)
    rogue = build("Rogue 5", 5, [weapon("Rapier", "1d8", "piercing", ["melee", "finesse"], ability="dex")],
                  [sneak, md("extra_attack", name="Opportunity Attack", attack="Rapier", action="reaction",
                             trigger_probability=0.4)], abilities=dex18)
    case("econ-reaction-opportunity-attack", "A reaction attack (trigger probability 0.4) with a fresh Sneak Attack "
         "(another creature's turn).", ["§4.2"], rogue, {"ac": 15}, riders=True, alt="no_reaction_in_round1")
    case("econ-reaction-no-resources", "A reaction attack does not spend resources: the limited smite never lands "
         "on it.", ["§4.2"],
         build("Sentinel", 5, [LONGSWORD2],
               [smite("any_hit", when="every_hit", resource={"uses": 4, "per": "long_rest"}),
                md("extra_attack", name="Sentinel", attack="Longsword", action="reaction", trigger_probability=0.5)],
               edition="2014", abilities=STR18), {"ac": 15}, riders=True, alt="no_reaction_in_round1")

    # ============================== warlock baseline (golden 11) ====================================================
    wl = [6.30, 8.25, 8.25, 8.90, 17.80, 17.80, 17.80, 19.10, 20.50, 19.10, 28.65, 28.65, 28.65, 28.65, 28.65, 28.65,
          38.20, 38.20, 38.20, 38.20]
    for lvl in range(1, 21):
        case(f"warlock-baseline-l{lvl:02d}", f"warlock_baseline preset at level {lvl} vs the DMG CR = {lvl} row "
             "(target null).", ["§3.6", "§4.7", "§8.11"],
             build("Warlock baseline", lvl, preset="warlock_baseline"), None, golden=[("dpr", wl[lvl - 1], 1e-9)],
             pmf=False)
    case("target-cr-row", "target {cr: \"5\"}: the DMG CR 5 row's AC 15.", ["§3.5"], ls1, {"cr": "5"},
         golden=[("dpr", 5.75, 1e-12)])
    case("target-null-uses-level-row", "No target: the CR = level row (L5 -> AC 15).", ["§3.5", "§4.5"], ls1, None,
         golden=[("dpr", 5.75, 1e-12)])


# ----------------------------------------------------------------------------------------------------------------------
# Evaluation of a case.
# ----------------------------------------------------------------------------------------------------------------------


def rider_rows(cc, uses, rdmg):
    rb = cc.rb
    rows = []
    for r in rb.riders:
        if r.when == "first_hit_per_turn" or (r.when == "every_hit" and r.slot):
            u = uses.get(r.mid, F0)
            d = rdmg.get(r.mid, F0)
            rows.append({"name": r.name, "expected_damage": num(d), "uses_per_round": num(u),
                         "damage_per_use": num(d / u) if u else None})
    for s in rb.sas:
        u = uses.get(s.mid, F0)
        d = rdmg.get(s.mid, F0)
        rows.append({"name": rb.names[s.mid], "expected_damage": num(d), "uses_per_round": num(u),
                     "damage_per_use": num(d / u) if u else None})
    return rows


def pa_rows(cc, pa):
    if not cc.rb.pas:
        return None
    return [{"probability": frac_str(p), "on": bool(on)} for p, on in pa]


def compute_case(c):
    h = c["horizon"]
    exp = {}
    cc = CaseCtx(c["build"], c["target"], c["rulings"])
    notes = [c["_notes"]] if c["_notes"] else []
    alts = []
    if h["kind"] == "round1":
        res, pa = round1(cc, want=True)
        exp["dpr"] = num(res.ev)
        exp["p_zero"] = num(res.pmf.get(0, F0))
        if cc.tg.hp is not None and not cc.rb.saves:
            exp["p_at_least_hp"] = num(sum((p for v, p in res.pmf.items() if v >= cc.tg.hp), F0))
        if c["_pmf"] and len(res.pmf) <= PMF_MAX_SUPPORT:
            exp["pmf"] = [[v, frac_str(p)] for v, p in sorted(res.pmf.items())]
        if c["_riders"]:
            exp["riders"] = rider_rows(cc, res.uses, res.rdmg)
        pr = pa_rows(cc, pa)
        if pr is not None:
            exp["power_attack"] = pr
        if c["_alt"] == "no_reaction_in_round1":
            cc2 = CaseCtx(c["build"], c["target"], c["rulings"])
            cc2.rb.extras = [e for e in cc2.rb.extras if e.action != "reaction"]
            r2, _ = round1(cc2, want=False)
            alts.append({"reading": "the round1 horizon excludes the reaction (it is one turn, not a round)",
                         "dpr": num(r2.ev)})
    elif h["kind"] == "fight":
        dpr, uses, rdmg = fight(cc, h["rounds"])
        exp["dpr"] = num(dpr)
        if c["_riders"]:
            exp["riders"] = rider_rows(cc, uses, rdmg)
        alt, _, _ = fight(CaseCtx(c["build"], c["target"], c["rulings"]), h["rounds"], scope="fight")
        if alt != dpr:
            alts.append({"reading": "decisions by full-fight backward induction (the value of uses left and Vex "
                                    "pending in later rounds counts), instead of per turn", "dpr": num(alt)})
            notes.append("Decision scope matters here: the expected value uses per-turn decisions; see "
                         "alternatives.")
    else:
        total, d0, detail = day(c["build"], c["target"], c["rulings"], h["rounds"], h["encounters_per_day"],
                                h["short_rests"])
        exp["dpr"] = num(total)
        exp["day"] = {"fight_dpr_without_limited_features": num(d0), "features": detail}
    if cc.tg.second_rate > 0 and h["kind"] != "day" and any(a.mastery == "cleave" for a in cc.rb.attacks):
        cc3 = CaseCtx(c["build"], c["target"], c["rulings"])
        cc3.cleave_per_hit = True
        alt = round1(cc3, want=False)[0].ev if h["kind"] == "round1" else fight(cc3, h["rounds"])[0]
        if alt != Fr(exp["dpr"]["exact"]):
            alts.append({"reading": "second_target_rate drawn again on every melee hit with the Cleave weapon until a "
                                    "Cleave happens (instead of once per turn at the first such hit)",
                         "dpr": num(alt)})
    if alts:
        exp["alternatives"] = alts
    if cc.rb.saves:
        exp["save"] = save_stats(cc)
    if notes:
        exp["notes"] = " ".join(notes)
    return exp


def lookup(obj, path):
    for part in path.split("."):
        obj = obj[int(part)] if isinstance(obj, list) else obj[part]
    if isinstance(obj, dict) and "exact" in obj:
        return obj["value"]
    return obj


def check_golden(cid, exp, path, want, tol, failures):
    got = lookup(exp, path)
    if isinstance(want, bool):
        ok = got == want
    else:
        ok = got is not None and abs(got - want) <= tol
    if not ok:
        failures.append(f"{cid}: {path} = {got!r}, golden {want!r} (tolerance {tol})")
    return ok


def reference_values():
    """Non-DPR goldens (contract sections 2 and 8) computed by the same enumeration."""
    ref = {}

    def hit(mode, lucky=False, ea=False, bonus=7, ac=15, crit=20, bd=None):
        pm, pn, pc = attack_odds(bonus, ac, crit, mode, lucky, ea, bd, False)
        return pn + pc, pc

    bless = freeze(bonus_dice_pmf(["1d4"]))
    rows = [
        ("normal", hit("normal")[0], 0.65), ("advantage", hit("advantage")[0], 0.8775),
        ("elven_accuracy", hit("advantage", ea=True)[0], 0.957125), ("lucky", hit("normal", True)[0], 0.6825),
        ("lucky_advantage", hit("advantage", True)[0], 0.898625),
        ("lucky_disadvantage", hit("disadvantage", True)[0], 0.46475),
        ("bless_normal", hit("normal", bd=bless)[0], 0.775),
        ("bless_advantage", hit("advantage", bd=bless)[0], 0.94625),
        ("bless_disadvantage", hit("disadvantage", bd=bless)[0], 0.60375),
        ("crit_normal", hit("normal")[1], 0.05), ("crit_advantage", hit("advantage")[1], 0.0975),
        ("crit_disadvantage", hit("disadvantage")[1], 0.0025),
        ("crit_elven_accuracy", hit("advantage", ea=True)[1], 0.142625),
        ("crit_floor_hit_plus5_ac30_crit19", hit("normal", bonus=5, ac=30, crit=19)[0], 0.10),
        ("crit_floor_crit_plus5_ac30_crit19", hit("normal", bonus=5, ac=30, crit=19)[1], 0.10),
    ]
    ref["d20_at_plus7_vs_ac15"] = [{"id": k, "exact": frac_str(v), "value": float(v), "golden": g} for k, v, g in rows]
    saves = [("dc15_vs_plus2", save_fail_odds(2, 15, "normal", None, False), 0.60),
             ("dc15_vs_plus2_advantage", save_fail_odds(2, 15, "advantage", None, False), 0.36),
             ("dc15_vs_plus20", save_fail_odds(20, 15, "normal", None, False), 0.0),
             ("dc15_vs_minus20", save_fail_odds(-20, 15, "normal", None, False), 1.0)]
    ref["save_fail"] = [{"id": k, "exact": frac_str(v), "value": float(v), "golden": g} for k, v, g in saves]
    ref["legendary_resistance_casts_f06_l3"] = {"exact": frac_str(Fr(4) / Fr(3, 5)), "value": float(Fr(20, 3)),
                                                "golden": 6.6667}
    gwf = []
    for m in (4, 6, 8, 10, 12):
        plain = mean(die_pmf(m))
        g14 = mean(die_pmf(m, ("gwf2014",)))
        g24 = mean(die_pmf(m, ("gwf2024",)))
        gwf.append({"die": f"d{m}", "plain": frac_str(plain), "gwf2014": frac_str(g14), "gwf2024": frac_str(g24),
                    "gain2014": frac_str(g14 - plain), "gain2024": frac_str(g24 - plain)})
    ref["gwf_expected_per_die"] = gwf
    sa = []
    for label, dice, rm in (("1d8", ((1, 1, 8),), ()), ("1d10", ((1, 1, 10),), ()), ("1d12", ((1, 1, 12),), ()),
                            ("2d6", ((1, 2, 6),), ()), ("2d6 gwf2024", ((1, 2, 6),), ("gwf2024",))):
        one = dict(dice_set(dice, rm, 1))
        two = conv(one, one)
        sa.append({"dice": label, "hit": frac_str(mean(one)), "hit_with_sa": num(mean(max_of_two(one))),
                   "crit": frac_str(mean(two)), "crit_with_sa_crit_dice": num(mean(max_of_two(two))),
                   "crit_with_sa_one_set": num(mean(conv(max_of_two(one), one)))})
    ref["savage_attacker_table"] = sa
    pn, pa = Fr(13, 20), Fr(351, 400)
    xstar = pn / (1 - pa + pn)
    x = pn
    for _ in range(200):
        x = x * pa + (1 - x) * pn
    ref["vex_steady_state"] = {"p_normal": frac_str(pn), "p_advantage": frac_str(pa), "exact": frac_str(xstar),
                               "value": float(xstar), "golden": 0.841424,
                               "iterated_chain_after_200_attacks": float(x)}
    ref["rpgbot_target_by_level"] = [{"level": lvl, "exact": frac_str(Fr(CR_ROWS[str(lvl)][2], 12)),
                                      "value": float(Fr(CR_ROWS[str(lvl)][2], 12))} for lvl in range(1, 21)]
    # Fireball: the wrong model (independent damage rolls per goblin) for contrast.
    cc = CaseCtx(build("2014 Wizard 5", 5, [], [md("save_effect", name="Fireball", ability="dex", dc=15, dice="8d6",
                                                   type="fire", shape="sphere", size=20)], edition="2014"),
                 {"saves": {"dex": 2}, "hp": 7}, None)
    s = save_stats(cc)
    p_each = Fr(s["p_each_dies"]["exact"])
    ref["fireball_independent_rolls_wrong_model"] = {"p_all_4_die": num(p_each ** 4), "golden": 0.998775,
                                                     "shared_roll_p_all_4_die": s["p_all_die"]}
    ref["typical_save_bonus_by_cr"] = {str(cr): typical_save_bonus(str(cr)) for cr in range(1, 31)}
    # Research A4's per-attack toggle rule for the 2014 L5 fighter (GWF 2014, +7, 2d6+4): on iff P'/P > D/(D+10),
    # D = 37/3 the average non-crit hit. The turn-level choice in the gwm14-pa-auto-crit-ba-ac* cases must agree.
    d_hit = Fr(25, 3) + 4
    toggle = []
    for ac in range(13, 20):
        p_off = sum(attack_odds(7, ac, 20, "normal", False, False, None, False)[1:])
        p_on = sum(attack_odds(2, ac, 20, "normal", False, False, None, False)[1:])
        toggle.append({"ac": ac, "p": frac_str(p_off), "p_power": frac_str(p_on), "ratio": float(p_on / p_off),
                       "threshold": float(d_hit / (d_hit + 10)), "rule_on": p_on / p_off > d_hit / (d_hit + 10)})
    ref["power_attack_toggle_rule_2014_l5"] = toggle
    return ref


# Contract section 8 items 4 and 5, printed values (4 decimals where the contract prints 4).
SA_TABLE_GOLDEN = {"1d8": (5.8125, 10.8457), "1d10": (7.15, 13.3166), "1d12": (8.4861, 15.7861),
                   "2d6": (8.3719, 15.9334), "2d6 gwf2024": (8.9105, 17.3000)}
GWF_TABLE_GOLDEN = {"d4": ("5/2", "3/1", "13/4"), "d6": ("7/2", "25/6", "4/1"), "d8": ("9/2", "21/4", "39/8"),
                    "d10": ("11/2", "63/10", "29/5"), "d12": ("13/2", "22/3", "27/4")}


def generate():
    CASES.clear()
    DELTAS.clear()
    define_cases()
    failures = []
    out_cases = []
    by_id = {}
    for c in CASES:
        exp = compute_case(c)
        for path, want, tol in c["_golden"]:
            check_golden(c["id"], exp, path, want, tol, failures)
        row = {"id": c["id"], "description": c["description"], "contract_refs": c["contract_refs"],
               "build": c["build"], "target": c["target"], "rulings": c["rulings"], "horizon": c["horizon"],
               "expected": exp}
        out_cases.append(row)
        by_id[c["id"]] = exp
    deltas = []
    for d in DELTAS:
        a = Fr(by_id[d["plus"]]["dpr"]["exact"])
        b = Fr(by_id[d["minus"]]["dpr"]["exact"])
        if d["id"].startswith("calibration"):
            v = a / b - 1
        else:
            v = a - b
        row = {"id": d["id"], "description": d["description"], "contract_refs": d["refs"], "plus": d["plus"],
               "minus": d["minus"], "kind": "relative" if d["id"].startswith("calibration") else "difference",
               "exact": frac_str(v), "value": float(v), "golden": d["golden"]}
        if d["golden"] is not None and abs(float(v) - d["golden"]) > d["tol"]:
            failures.append(f"delta {d['id']}: {float(v)!r}, golden {d['golden']!r} (tolerance {d['tol']})")
        deltas.append(row)
    ref = reference_values()
    for group in ("d20_at_plus7_vs_ac15", "save_fail"):
        for r in ref[group]:
            if abs(r["value"] - r["golden"]) > 1e-12:
                failures.append(f"reference {group}.{r['id']}: {r['value']!r}, golden {r['golden']!r}")
    for row in ref["savage_attacker_table"]:
        hit_g, crit_g = SA_TABLE_GOLDEN[row["dice"]]
        if (abs(row["hit_with_sa"]["value"] - hit_g) > 5e-5
                or abs(row["crit_with_sa_crit_dice"]["value"] - crit_g) > 5e-5):
            failures.append(f"reference savage_attacker_table {row['dice']}")
    for row in ref["gwf_expected_per_die"]:
        if (row["plain"], row["gwf2014"], row["gwf2024"]) != GWF_TABLE_GOLDEN[row["die"]]:
            failures.append(f"reference gwf_expected_per_die {row['die']}")
    for row in ref["power_attack_toggle_rule_2014_l5"]:
        if row["rule_on"] != (row["ac"] <= 16):
            failures.append(f"reference power_attack_toggle_rule ac {row['ac']}")
        chosen = by_id[f"gwm14-pa-auto-crit-ba-ac{row['ac']}"]["power_attack"][0]["on"]
        if chosen != row["rule_on"]:
            failures.append(f"power attack auto at AC {row['ac']} disagrees with the per-attack toggle rule")
    if abs(ref["vex_steady_state"]["value"] - 0.841424) > 5e-7:
        failures.append("reference vex_steady_state")
    if abs(ref["fireball_independent_rolls_wrong_model"]["p_all_4_die"]["value"] - 0.998775) > 5e-7:
        failures.append("reference fireball_independent_rolls_wrong_model")
    doc = {
        "schema": "dnd-mcp/dpr-oracle-cases",
        "schema_version": 1,
        "generated_by": "DndMcp.Tests/Dpr/Fixtures/oracle/oracle.py (python3, stdlib only, exact fractions)",
        "notes": "Every number is exact: 'exact' is a reduced fraction num/den, 'value' its nearest double. dpr is "
                 "the expected damage per round of the horizon. pmf / p_zero are the round-1 turn's damage "
                 "distribution. README.md lists the semantic readings the contract left open.",
        "cases": out_cases,
        "deltas": deltas,
        "reference": ref,
    }
    return doc, failures


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--check", action="store_true", help="verify cases.json is current; write nothing")
    args = ap.parse_args(argv)
    doc, failures = generate()
    text = json.dumps(doc, indent=1, ensure_ascii=False) + "\n"
    status = 0
    if failures:
        print("GOLDEN MISMATCHES:", file=sys.stderr)
        for f in failures:
            print("  " + f, file=sys.stderr)
        status = 1
    if args.check:
        try:
            with open(CASES_PATH, encoding="utf-8") as fh:
                current = fh.read()
        except FileNotFoundError:
            current = None
        if current != text:
            print("cases.json is stale: run python3 oracle.py", file=sys.stderr)
            status = 1
        else:
            print(f"cases.json is current ({len(doc['cases'])} cases, {len(doc['deltas'])} deltas)")
    else:
        with open(CASES_PATH, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print(f"wrote {CASES_PATH}: {len(doc['cases'])} cases, {len(doc['deltas'])} deltas")
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
