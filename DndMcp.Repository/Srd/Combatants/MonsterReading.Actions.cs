using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Combatants;

internal sealed partial class MonsterReading
{
    /// <summary>Most severe first: the order <see cref="StatBlockAction.Condition"/> is chosen in.</summary>
    private static readonly string[] Severity =
    [
        StatBlockValues.Conditions.Petrified, StatBlockValues.Conditions.Paralyzed, StatBlockValues.Conditions.Stunned,
        StatBlockValues.Conditions.Unconscious, StatBlockValues.Conditions.Incapacitated, StatBlockValues.Conditions.Restrained,
        StatBlockValues.Conditions.Grappled, StatBlockValues.Conditions.Blinded, StatBlockValues.Conditions.Frightened,
        StatBlockValues.Conditions.Charmed, StatBlockValues.Conditions.Poisoned, StatBlockValues.Conditions.Prone,
        StatBlockValues.Conditions.Exhaustion, StatBlockValues.Conditions.Deafened, StatBlockValues.Conditions.Invisible,
    ];

    /// <summary>Actions with no effect in a gridless fight, by name (parentheticals stripped): why.</summary>
    private static readonly Dictionary<string, string> NoCombatActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Change Shape"] = "changing form",
        ["Shape-Shift"] = "changing form",
        ["Etherealness"] = "leaving the plane of the fight",
        ["Ethereal Stride"] = "leaving the plane of the fight",
        ["Ethereal Jaunt"] = "leaving the plane of the fight",
        ["Read Thoughts"] = "reading thoughts",
        ["Illusory Appearance"] = "a disguise",
        ["Detect"] = "a Perception check",
        ["Nightmare Haunting"] = "haunting a sleeping creature",
        ["Create Specter"] = "raising a corpse that died violently within a minute",
        ["Destroy Metal"] = "destroying unattended metal",
        ["Tree Stride"] = "movement",
        ["Bubble Dash"] = "movement",
        ["Aquatic Charge"] = "movement",
        ["Charge"] = "movement",
        ["Leap"] = "movement",
        ["Teleport"] = "movement",
        ["Move"] = "movement",
        ["Prowl"] = "movement and hiding",
        ["Shadow Stealth"] = "hiding",
        ["Ignited Illumination"] = "light",
        ["Reel"] = "pulling a creature (no grid)",
    };

    private void ReadActionList(IReadOnlyList<RecordAction> list, string slot, List<StatBlockAction> into)
    {
        foreach (var action in list)
        {
            if (action.IsMultiattack || action.Spellcasting is not null)
            {
                continue;
            }

            var members = list.Where(a => a != action && a.Name.EndsWith(" " + action.Name, StringComparison.Ordinal)).Select(a => a.Name).ToList();
            if (members.Count > 0 && action.AttackBonus is null && action.Dc is null)
            {
                // 2024 sphinx "Roar": the header of its First, Second and Third Roar actions, each once, in order.
                var where = slot == StatBlockValues.ActionSlots.Action ? action.Name : $"{SlotWord(slot)} {action.Name}";
                _families[action.Name] = members;
                _log.Approximated(where, $"Its uses are the actions {string.Join(", ", members)}, once each; they come in order in the text, and the simulator may use any one it has not used.");
                into.Add(new StatBlockAction
                {
                    Name = action.Name,
                    Kind = StatBlockValues.ActionKinds.NotModelled,
                    Slot = slot,
                    Usage = Usage(action.Usage, where),
                    Text = ProseText.Normalize(action.Desc),
                    Notes = [$"Used through its members: {string.Join(", ", members)}."],
                });
                continue;
            }

            var read = ReadAction(action, action.Name, slot);
            if (_families.Values.Any(f => f.Contains(action.Name)))
            {
                read = read.Select(r => r with { Usage = new UsageSpec(StatBlockValues.UsageKinds.PerDay, Uses: 1) }).ToList();
            }

            into.AddRange(read);
        }
    }

    /// <summary>
    /// Actions that "make one Bite attack against a creature it is grappling" (2014 Swallow): use_actions placeholders
    /// whose names are resolved once every action is read. One whose name does not resolve becomes not modelled.
    /// </summary>
    private void ResolvePlaceholders(List<StatBlockAction> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var action = list[i];
            if (action.Kind != StatBlockValues.ActionKinds.UseActions || action.Slot == StatBlockValues.ActionSlots.Legendary)
            {
                continue;
            }

            var where = action.Slot == StatBlockValues.ActionSlots.Action ? action.Name : $"{SlotWord(action.Slot)} {action.Name}";
            var resolved = action.Uses.SelectMany(u => Resolve(u.ActionName, where)).Distinct(StringComparer.Ordinal).ToList();
            if (resolved.Count == 0 || resolved.Contains(action.Name))
            {
                list[i] = action with { Kind = StatBlockValues.ActionKinds.NotModelled, Uses = [] };
                _log.NotModelled(where, $"Not simulated: {FirstSentence(action.Text)}");
                continue;
            }

            list[i] = action with { Uses = resolved.Take(1).Select(n => new ActionUse(n, 1)).ToList() };
            _log.Approximated(where, $"Simulated as one {resolved[0]}; the rest is not: {Excerpt(action.Text[ProseText.SentenceEnd(action.Text, 0)..])}");
        }
    }

    /// <summary>One entry of an action-like list → the stat block action(s) it becomes.</summary>
    private IReadOnlyList<StatBlockAction> ReadAction(RecordAction a, string name, string slot)
    {
        var text = ProseText.Normalize(a.Desc);
        var where = slot == StatBlockValues.ActionSlots.Action ? name : $"{SlotWord(slot)} {name}";
        if (a.BreathOptions is { } options)
        {
            return BreathOptions(a, options, text, slot);
        }

        if (a.SubAttacks is { Count: > 0 } roars)
        {
            return Roars(a, roars, text, slot);
        }

        if (slot == StatBlockValues.ActionSlots.Reaction)
        {
            return [Reaction(a, name, text, where)];
        }

        var usage = Usage(a.Usage, where);
        var header = ProseText.ReadAttackHeader(text);
        if (header is not null || (a.AttackBonus is not null && ProseText.HitText(text) is not null))
        {
            return [Attack(a, name, text, header, slot, usage, where)];
        }

        if (a.Dc is not null || (ProseText.ReadSave(text) is not null && ProseText.DamageMentions(text).Count + ProseText.ConditionMentions(text).Count > 0))
        {
            return [Save(a, name, text, slot, usage, where)];
        }

        if (ProseText.ReadHealing(text) is { } healing)
        {
            return [Heal(name, text, healing, slot, usage, where)];
        }

        if (AutoDamage().Match(text) is { Success: true } auto && ProseText.DamageMentions(text) is { Count: > 0 } hits && hits[0].Type is not null)
        {
            // 2024 lich Deathly Teleport, 2014 aboleth Psychic Drain: damage with no roll and no save.
            var area = ProseText.ReadArea(text);
            _log.Approximated(where, $"Simulated as {hits[0].Dice} {hits[0].Type} damage that always lands; the rest is not: {Excerpt(text)}");
            return
            [
                new StatBlockAction
                {
                    Name = name, Kind = StatBlockValues.ActionKinds.AutoHit, Slot = slot, Damage = [new DamageRoll(hits[0].Dice, hits[0].Type)],
                    Area = area, Targets = 1, Magical = MagicalEffect().IsMatch(text), Usage = usage, Text = text,
                    Notes = area is null ? [] : ["The area's creatures each take the damage; the engine counts them as for a save action's area."],
                },
            ];
        }

        if (slot is StatBlockValues.ActionSlots.Action or StatBlockValues.ActionSlots.BonusAction &&
            MakesOneAttack().IsMatch(text) && ProseText.UsedNames(text) is { Count: > 0 } used)
        {
            return [new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.UseActions, Slot = slot, Uses = [new ActionUse(used[0], 1)], Usage = usage, Text = text }];
        }

        return [Other(name, text, slot, usage, where)];
    }

    private static string SlotWord(string slot) => slot switch
    {
        StatBlockValues.ActionSlots.BonusAction => "bonus action",
        StatBlockValues.ActionSlots.Reaction => "reaction",
        StatBlockValues.ActionSlots.Legendary => "legendary action",
        _ => "action",
    };

    private StatBlockAction Reaction(RecordAction a, string name, string text, string where)
    {
        var parry = ParryBonus().Match(text);
        if (parry.Success)
        {
            return new StatBlockAction
            {
                Name = name,
                Kind = StatBlockValues.ActionKinds.Parry,
                Slot = StatBlockValues.ActionSlots.Reaction,
                AcBonus = int.Parse(parry.Groups["n"].Value, CultureInfo.InvariantCulture),
                Usage = Usage(a.Usage, where),
                Text = text,
            };
        }

        _log.NotModelled(where, $"Reactions other than Parry are not simulated: {FirstSentence(text)}");
        return new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = StatBlockValues.ActionSlots.Reaction, Text = text };
    }

    private StatBlockAction Other(string name, string text, string slot, UsageSpec usage, string where)
    {
        var bare = ProseText.StripParentheticals(name);
        var action = new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = slot, Usage = usage, Text = text };
        if (NoCombatActions.TryGetValue(bare, out var why) || MovementOnly().IsMatch(text) is var movement && movement)
        {
            _log.Note($"{Capitalize(where)}: no combat effect in the simulator ({why ?? "movement"}).");
            return action;
        }

        _log.NotModelled(where, $"Not simulated: {FirstSentence(text)}");
        return action;
    }

    private StatBlockAction Heal(string name, string text, DamageFormula healing, string slot, UsageSpec usage, string where)
    {
        var selfOnly = SelfHeal().IsMatch(text);
        if (ProseText.DamageMentions(text).Count > 0 || RemovesMore().IsMatch(text))
        {
            _log.Approximated(where, "Only the hit points it restores are simulated.");
        }

        return new StatBlockAction
        {
            Name = name,
            Kind = StatBlockValues.ActionKinds.Heal,
            Slot = slot,
            Healing = healing,
            SelfOnly = selfOnly,
            Magical = true,
            Usage = usage,
            Text = text,
        };
    }

    private StatBlockAction Attack(RecordAction a, string name, string text, AttackHeader? header, string slot, UsageSpec usage, string where)
    {
        var notes = new List<string>();
        var bonus = a.AttackBonus ?? header!.Bonus;
        if (header is not null && a.AttackBonus is { } dataBonus && dataBonus != header.Bonus)
        {
            _log.Conflict(where, string.Create(CultureInfo.InvariantCulture, $"The data gives +{dataBonus} to hit and the text {header.Bonus:+0;-0}; the data's is used."));
        }

        if (header is null)
        {
            _log.Unparsed(where, "The attack line could not be read; it is taken as a melee attack.");
        }

        var range = header?.Range ?? StatBlockValues.AttackRanges.Melee;
        var hit = ProseText.HitText(text) ?? string.Empty;
        if (TargetRestriction().Match(text) is { Success: true } restriction)
        {
            _log.Approximated(where, $"It may target only {restriction.Groups["who"].Value.Trim()}; the simulator lets it target any creature.");
        }

        var (damage, riders, reported) = ReadHit(a, hit, where, notes);
        if (damage.Count == 0 && riders.Count == 0)
        {
            _log.NotModelled(where, $"Its hit does nothing the simulator models: {Excerpt(hit)}");
        }
        else
        {
            WarnUntracked(hit, reported, where);
        }
        var magical = header?.IsSpell == true || _magicWeapons || a.Name.Contains("Spell", StringComparison.Ordinal);
        return new StatBlockAction
        {
            Name = name,
            Kind = StatBlockValues.ActionKinds.Attack,
            Slot = slot,
            AttackBonus = bonus,
            Range = range,
            AlsoRanged = header?.AlsoRanged == true,
            Damage = damage,
            OnHit = riders,
            Magical = magical,
            IsSpell = header?.IsSpell == true,
            Usage = usage,
            Text = text,
            Notes = notes,
        };
    }

    /// <summary>
    /// The "Hit:" text → the damage every hit deals and the riders (a save, a condition). Each damage roll is read in
    /// its sentence; the data's damage entries are the cross-check.
    /// </summary>
    private (IReadOnlyList<DamageRoll> Damage, IReadOnlyList<ActionEffect> Riders, IReadOnlyList<(int Start, int End)> Reported) ReadHit(RecordAction a, string hit, string where, List<string> notes)
    {
        var conditional = ConditionalSpans(hit, where);
        var reported = new List<(int Start, int End)>(conditional);
        var mentions = ProseText.DamageMentions(hit).Where(m => !InSpans(conditional, m.Index)).ToList();
        var save = ProseText.ReadSave(hit);
        var saveAt = save?.Index ?? hit.Length;

        var damage = new List<DamageRoll>();
        var saveDamage = new List<DamageRoll>();
        var hitOngoing = new List<DamageRoll>();
        var saveOngoing = new List<DamageRoll>();
        var previousEnd = 0;
        foreach (var m in mentions)
        {
            var before = hit[previousEnd..m.Index];
            var after = hit[m.End..ProseText.SentenceEnd(hit, m.End)];
            var lead = hit[ProseText.SentenceStart(hit, m.Index)..m.Index];
            previousEnd = m.End;
            var roll = new DamageRoll(m.Dice, m.Type);
            if (m.AlternativeType is not null)
            {
                notes.Add($"\"{m.Dice} {m.Type} or {m.AlternativeType}\": {m.Type} is used.");
            }

            if (Ongoing().IsMatch(after) || OngoingLead().IsMatch(lead))
            {
                (m.Index > saveAt ? saveOngoing : hitOngoing).Add(roll);
                continue;
            }

            if (m.Index > saveAt)
            {
                saveDamage.Add(roll);
                continue;
            }

            if (AlternativeBefore().IsMatch(before))
            {
                if (TwoHands().IsMatch(after) && damage.Count > 0)
                {
                    if (_m.ArmorClass.Any(ac => ac.Source?.Contains("shield", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        notes.Add($"Versatile: it carries a shield, so the one-handed {damage[0].Dice} is used, not the two-handed {m.Dice}.");
                    }
                    else
                    {
                        notes.Add($"Versatile: the two-handed {m.Dice} is used, not the one-handed {damage[0].Dice}.");
                        damage[0] = roll;
                    }
                }
                else
                {
                    _log.Approximated(where, $"The text offers {m.Dice} {m.Type} damage instead{Clause(after)}; the first damage is used.");
                }

                continue;
            }

            if (ConditionalAfter().IsMatch(after) || before.Contains("extra", StringComparison.OrdinalIgnoreCase))
            {
                _log.NotModelled(where, $"Its extra {m.Dice} {m.Type} damage{Clause(after)} is not simulated.");
                continue;
            }

            damage.Add(roll);
        }

        CrossCheckDamage(a, ProseText.DamageMentions(hit), where);
        if (mentions.Count == 0 && damage.Count == 0 && a.Damage.Count > 0 && !conditional.Any())
        {
            _log.Unparsed(where, "Its damage could not be read from the text; the data's damage entries are used as on-hit damage.");
            damage.AddRange(a.Damage.Select(d => new DamageRoll(ProseText.Dice(d.DamageDice) ?? DamageFormula.Zero, d.DamageType.Index)));
        }

        foreach (var choice in a.DamageChoices)
        {
            var first = choice.From.Options.FirstOrDefault();
            if (first is not null && !mentions.Any(m => m.Dice.Equals(ProseText.Dice(first.DamageDice))))
            {
                _log.Unparsed(where, $"Its damage choice ({string.Join(" or ", choice.From.Options.Select(o => $"{o.DamageDice} {o.DamageType.Index}"))}) is not in the text; it is left out.");
            }
        }

        var riders = new List<ActionEffect>();
        var hitPart = hit[..Math.Min(saveAt, hit.Length)];
        var hitConditions = Conditions(hitPart, null, conditional, 0, where);
        if (hitConditions.Count > 0)
        {
            riders.Add(new ActionEffect
            {
                Kind = StatBlockValues.EffectKinds.Condition,
                Condition = WithOngoing(hitConditions[0], hitOngoing),
                ExtraConditions = hitConditions.Skip(1).ToList(),
                MaxSize = ProseText.ReadMaxSize(hit[ProseText.SentenceStart(hit, FirstConditionIndex(hitPart, conditional))..]),
            });
        }
        else if (hitOngoing.Count > 0)
        {
            _log.NotModelled(where, $"Its ongoing {string.Join(" + ", hitOngoing.Select(d => $"{d.Dice} {d.DamageType}"))} damage is tied to no condition the simulator tracks; it is not simulated.");
        }

        if (save is not null)
        {
            var (_, region) = SaveRegions(hit[save.Index..], new SaveClause(0, save.Length, save.Ability, save.Dc));
            region = hit[save.Index..(save.Index + save.Length)] + region;
            if (EscalatesLater().IsMatch(hit[save.Index..]))
            {
                _log.Approximated(where, "Only the save's first effect is simulated (the text escalates on a later or worse failure).");
            }

            if (SwallowPattern().IsMatch(region))
            {
                _log.Approximated(where, "Being swallowed is simulated as its conditions and damage for the rest of the fight; total cover, regurgitation and escape from the corpse are not.");
            }
            var spec = new SaveSpec(save.Ability, save.Dc, ProseText.SaysHalf(region) && saveDamage.Count > 0 ? StatBlockValues.OnSuccess.Half : StatBlockValues.OnSuccess.None);
            var saveConditions = Conditions(region, spec, conditional, save.Index, where);
            if (saveDamage.Count == 0 && saveConditions.Count == 0)
            {
                if (saveOngoing.Count > 0)
                {
                    _log.NotModelled(where, "Its save's ongoing damage is tied to no condition the simulator tracks; it is not simulated.");
                }
                else
                {
                    _log.NotModelled(where, $"Not simulated: {Excerpt(region)}");
                    reported.Add((save.Index, hit.Length));
                }
            }
            else
            {
                riders.Add(new ActionEffect
                {
                    Kind = StatBlockValues.EffectKinds.Save,
                    Save = spec,
                    Damage = saveDamage,
                    Condition = saveConditions.Count > 0 ? WithOngoing(saveConditions[0], saveOngoing) : null,
                    ExtraConditions = saveConditions.Skip(1).ToList(),
                    MaxSize = ProseText.ReadMaxSize(hit[ProseText.SentenceStart(hit, save.Index)..save.Index]),
                });
            }

            CrossCheckDc(a, save, where);
            if (ProseText.ReadSave(hit, save.End) is not null)
            {
                _log.NotModelled(where, "A second saving throw in its text is not simulated.");
            }
        }
        else if (a.Dc is { DcValue: { } dcValue } dc && ProseText.ReadEscapeDc(hit) != dcValue)
        {
            _log.Conflict(where, string.Create(CultureInfo.InvariantCulture, $"The data gives a DC {dcValue} {dc.DcType.Index} save the text does not; it is not used."));
        }

        foreach (var m in mentions.Where(m => m.Type is null))
        {
            notes.Add($"Its {m.Dice} damage has the type chosen elsewhere in the stat block; it is untyped.");
        }

        return (damage, riders, reported);
    }

    /// <summary>
    /// Effects in an attack's hit or a save's failure that the simulator does not track (a curse, a Speed change, forced
    /// movement, a lower hit point maximum, lost reactions). Named in one warning per action, so a simulated action
    /// never hides the part of its text the numbers leave out.
    /// </summary>
    private void WarnUntracked(string text, IReadOnlyList<(int Start, int End)> conditional, string where)
    {
        var found = new List<string>();
        foreach (var (pattern, label) in Untracked)
        {
            if (pattern.Matches(text).Any(m => !InSpans(conditional, m.Index)) && !found.Contains(label))
            {
                found.Add(label);
            }
        }

        if (found.Count > 0)
        {
            _log.Approximated(where, $"Not simulated from its text: {string.Join(", ", found)}.");
        }
    }

    private static readonly (Regex Pattern, string Label)[] Untracked =
    [
        (new Regex(@"\bcursed\b|\bcurse\b", RegexOptions.IgnoreCase), "a curse"),
        (new Regex(@"\bSpeed (?:is |decreases|becomes|is halved)|\bspeed (?:is reduced|is halved|becomes|decreases)|\bspeed reduced\b", RegexOptions.IgnoreCase), "a Speed change"),
        (new Regex(@"\bpushed\b|\bpulled\b|\bpulls the target\b|\bmoves the target\b|\bthrown\b", RegexOptions.IgnoreCase), "forced movement"),
        (new Regex(@"hit point maximum", RegexOptions.IgnoreCase), "a hit point maximum reduction"),
        (new Regex(@"can't (?:take|use) (?:a Bonus Action or )?reactions", RegexOptions.IgnoreCase), "lost reactions"),
        (new Regex(@"can't regain hit points", RegexOptions.IgnoreCase), "no healing"),
        (new Regex(@"regains hit points equal", RegexOptions.IgnoreCase), "its own healing"),
        (new Regex(@"penalty to (?:AC|the AC)|takes a -\d+ penalty", RegexOptions.IgnoreCase), "a penalty"),
        (new Regex(@"(?:has|have) Disadvantage on|has disadvantage on", RegexOptions.IgnoreCase), "Disadvantage it imposes"),
        (new Regex(@"suffocating|unable to breathe", RegexOptions.IgnoreCase), "suffocation"),
        (new Regex(@"\battaches\b|\battached\b", RegexOptions.IgnoreCase), "attaching"),
        (new Regex(@"\bdisease\b|\bdiseased\b", RegexOptions.IgnoreCase), "a disease"),
    ];

    private static string Clause(string after)
    {
        var dash = after.IndexOf(" \u2014 ", StringComparison.Ordinal);
        var trimmed = (dash >= 0 ? after[..dash] : after).Trim().TrimEnd('.');
        return trimmed.Length == 0 ? string.Empty : " (" + (trimmed.Length > 90 ? trimmed[..90] + "…" : trimmed) + ")";
    }

    private static ConditionEffect WithOngoing(ConditionEffect condition, List<DamageRoll> ongoing) =>
        ongoing.Count == 0 ? condition : condition with { OngoingDamage = ongoing };

    // A condition clause's first condition position (for its size limit), skipping conditional sentences.
    private static int FirstConditionIndex(string text, IReadOnlyList<(int Start, int End)> spans) =>
        ProseText.ConditionMentions(text).FirstOrDefault(c => !InSpans(spans, c.Index))?.Index ?? 0;

    /// <summary>
    /// The conditions a clause imposes, most severe first, each with its duration. <paramref name="offset"/> is the
    /// clause's position in the text the conditional spans were measured on.
    /// </summary>
    private List<ConditionEffect> Conditions(string clause, SaveSpec? save, IReadOnlyList<(int Start, int End)> conditional, int offset, string where)
    {
        var stone = clause.IndexOf("begins to turn to stone", StringComparison.OrdinalIgnoreCase);
        if (stone >= 0)
        {
            // 2014 cockatrice and gorgon: restrained now, petrified only on a second failure.
            clause = clause[..ProseText.SentenceEnd(clause, stone)] + " It must repeat the saving throw at the end of its next turn.";
            _log.Approximated(where, "Only the first stage (Restrained until the end of its next turn) is simulated; a second failed save petrifies it in the text.");
        }
        else if (clause.Contains("Second Failure", StringComparison.Ordinal))
        {
            _log.Approximated(where, "Only the first stage is simulated; a second failed save worsens it in the text.");
        }

        if (EndsEarly().IsMatch(clause))
        {
            _log.Approximated(where, $"Its condition ends early in the text ({EndsEarly().Match(clause).Value}); the simulator does not end it early.");
        }

        var mentions = ProseText.ConditionMentions(clause).Where(c => !InSpans(conditional, c.Index + offset)).ToList();
        var escape = ProseText.ReadEscapeDc(clause);
        var effects = new List<ConditionEffect>();
        foreach (var mention in mentions)
        {
            var reading = ProseText.ReadDuration(clause, mention.Index, save);
            string duration;
            int? rounds = reading?.Rounds;
            int? escapeDc = null;
            if (reading is not null)
            {
                duration = reading.Duration;
                if (duration == StatBlockValues.Durations.UntilEscape)
                {
                    escapeDc = escape;
                }
            }
            else
            {
                switch (mention.Condition)
                {
                    case StatBlockValues.Conditions.Prone:
                        duration = StatBlockValues.Durations.UntilStands;
                        break;
                    case StatBlockValues.Conditions.Grappled:
                    case not StatBlockValues.Conditions.Prone when escape is not null:
                        // Restrained "until this grapple ends", Blinded while a cloaker is attached: the clause's
                        // escape (or detach) check ends it.
                        duration = StatBlockValues.Durations.UntilEscape;
                        escapeDc = escape;
                        break;
                    default:
                        duration = StatBlockValues.Durations.Fight;
                        break;
                }
            }

            if (duration == StatBlockValues.Durations.UntilEscape && escapeDc is null)
            {
                if (save is not null)
                {
                    escapeDc = save.Dc;
                    _log.Approximated(where, $"No escape DC is given for {mention.Condition}; the save's DC {save.Dc} is used.");
                }
                else
                {
                    duration = StatBlockValues.Durations.Fight;
                    _log.Unparsed(where, $"No escape DC is given for {mention.Condition}; it lasts the fight.");
                }
            }

            if (!StatBlockValues.Conditions.Mechanical.Contains(mention.Condition))
            {
                _log.Note($"{Capitalize(where)}: {mention.Condition} has no effect in the simulator.");
            }

            effects.Add(new ConditionEffect
            {
                Condition = mention.Condition,
                Duration = duration,
                Rounds = rounds,
                EscapeDc = escapeDc,
                SaveEnds = duration == StatBlockValues.Durations.SaveEnds && save is not null ? save with { OnSuccess = StatBlockValues.OnSuccess.None } : null,
            });
        }

        return effects.OrderBy(e => Array.IndexOf(Severity, e.Condition)).ToList();
    }

    /// <summary>
    /// Sentences whose effect depends on something the simulator does not track ("If the boar moved 20+ feet",
    /// "if the attack roll had Advantage", "instead of dealing damage"). Their damage and conditions are left out with
    /// a warning; a size or creature-type test ("If the target is a Large or smaller creature") is not such a sentence.
    /// </summary>
    private List<(int Start, int End)> ConditionalSpans(string text, string where, bool swallowIsConditional = true)
    {
        var spans = new List<(int, int)>();
        var i = 0;
        var previous = false;
        while (i < text.Length)
        {
            var end = ProseText.SentenceEnd(text, i);
            var sentence = text[i..end].Trim();
            var continuation = previous && Continuation().IsMatch(sentence);
            previous = false;
            if (continuation ||
                (sentence.StartsWith("If ", StringComparison.Ordinal) && !PlainIf().IsMatch(sentence)) ||
                (swallowIsConditional && StateContinuation().IsMatch(sentence)) ||
                sentence.Contains("instead of dealing damage", StringComparison.OrdinalIgnoreCase) ||
                FailsByFive().IsMatch(sentence))
            {
                spans.Add((i, end));
                previous = true;
                if (!continuation && (ProseText.DamageMentions(sentence).Count > 0 || ProseText.ConditionMentions(sentence).Count > 0 || sentence.Contains(" die", StringComparison.Ordinal)))
                {
                    _log.NotModelled(where, $"Not simulated: \"{Excerpt(sentence)}\"");
                }
            }

            i = end == i ? i + 1 : end;
        }

        return spans;
    }

    private static bool InSpans(IReadOnlyList<(int Start, int End)> spans, int index) => spans.Any(s => index >= s.Start && index < s.End);

    private void CrossCheckDamage(RecordAction a, IReadOnlyList<DamageMention> mentions, string where)
    {
        foreach (var entry in a.Damage)
        {
            var dice = ProseText.Dice(entry.DamageDice);
            if (dice is null)
            {
                _log.Unparsed(where, $"The data's damage \"{entry.DamageDice}\" is not dice.");
                continue;
            }

            if (!mentions.Any(m => m.Dice.Equals(dice) && (m.Type is null || m.Type == entry.DamageType.Index || m.AlternativeType == entry.DamageType.Index)))
            {
                _log.Conflict(where, $"The data lists {dice} {entry.DamageType.Index} damage the text does not give; the text is used.");
            }
        }
    }

    private void CrossCheckDc(RecordAction a, SaveClause save, string where)
    {
        if (a.Dc is { DcValue: { } value } dc && (value != save.Dc || dc.DcType.Index != save.Ability) &&
            !(dc.DcType.Index == DslValues.Abilities.Str && dc.SuccessType == SaveSuccessTypes.None))
        {
            _log.Conflict(where, string.Create(CultureInfo.InvariantCulture, $"The data gives DC {value} {dc.DcType.Index} and the text DC {save.Dc} {save.Ability}; the text is used."));
        }
    }

    private StatBlockAction Save(RecordAction a, string name, string text, string slot, UsageSpec usage, string where)
    {
        var clause = ProseText.ReadSave(text);
        string ability;
        int dc;
        if (a.Dc is { DcValue: { } value } data)
        {
            ability = data.DcType.Index;
            dc = value;
            if (clause is not null && (clause.Dc != value || clause.Ability != ability))
            {
                _log.Conflict(where, string.Create(CultureInfo.InvariantCulture, $"The data gives DC {value} {ability} and the text DC {clause.Dc} {clause.Ability}; the data's is used."));
            }
        }
        else if (clause is not null)
        {
            ability = clause.Ability;
            dc = clause.Dc;
            _log.Note($"{Capitalize(where)}: its save (DC {dc} {ability}) is read from the text; the data has none.");
        }
        else
        {
            _log.Unparsed(where, "It has a computed DC the simulator cannot evaluate; it is not simulated.");
            return new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = slot, Usage = usage, Text = text };
        }

        var (targeting, failure) = SaveRegions(text, clause);
        var conditional = ConditionalSpans(failure, where, swallowIsConditional: false);
        if (SwallowPattern().IsMatch(failure))
        {
            _log.Approximated(where, "Being swallowed is simulated as its conditions and damage for the rest of the fight; total cover, regurgitation and escape from the corpse are not.");
        }
        var mentions = ProseText.DamageMentions(failure).Where(m => !InSpans(conditional, m.Index)).ToList();
        var damage = new List<DamageRoll>();
        var ongoing = new List<DamageRoll>();
        foreach (var m in mentions)
        {
            var after = failure[m.End..ProseText.SentenceEnd(failure, m.End)];
            var lead = failure[ProseText.SentenceStart(failure, m.Index)..m.Index];
            (Ongoing().IsMatch(after) || OngoingLead().IsMatch(lead) ? ongoing : damage).Add(new DamageRoll(m.Dice, m.Type));
            if (m.Type is null)
            {
                _log.Note($"{Capitalize(where)}: its damage type is chosen elsewhere in the stat block; the damage is untyped.");
            }
        }

        if (mentions.Count == 0 && a.Damage.Count > 0 && ProseText.DamageMentions(text).Count == 0)
        {
            _log.Unparsed(where, "Its damage could not be read from the text; the data's entries are used.");
            damage.AddRange(a.Damage.Select(d => new DamageRoll(ProseText.Dice(d.DamageDice) ?? DamageFormula.Zero, d.DamageType.Index)));
        }

        CrossCheckDamage(a, ProseText.DamageMentions(text), where);
        var onSuccess = OnSuccess(a, name, text, damage.Count > 0, where);
        var spec = new SaveSpec(ability, dc, onSuccess);
        var conditions = Conditions(failure, spec, conditional, 0, where);
        if (text.Contains("Failure by 5 or More", StringComparison.Ordinal) || FailsByFive().IsMatch(text))
        {
            _log.Approximated(where, "Only the save's first effect is simulated (the text escalates on a worse failure).");
        }

        var fix = _override?.Actions?.GetValueOrDefault(name);
        var area = fix?.Area is { } overrideArea ? new AreaSpec(overrideArea.Shape, overrideArea.Size) : ProseText.ReadArea(targeting);
        var targets = fix?.Targets ?? (area is null ? ProseText.ReadTargetCount(targeting) ?? 1 : 1);
        if (fix?.Area is not null || fix?.Targets is not null)
        {
            _log.Note($"{Capitalize(where)}: {(fix.Area is not null ? "area" : "targets")} from the overrides file ({fix.Note}).");
        }

        if (area is null && fix?.Targets is null && EachCreature().IsMatch(targeting))
        {
            _log.Approximated(where, $"It affects \"{Excerpt(EachCreature().Match(targeting).Value)}\", which names no area; one creature is used.");
        }

        if (damage.Count == 0 && conditions.Count == 0)
        {
            _log.NotModelled(where, $"Its effect is not simulated: {Excerpt(failure)}");
            return new StatBlockAction { Name = name, Kind = StatBlockValues.ActionKinds.NotModelled, Slot = slot, Usage = usage, Text = text };
        }

        WarnUntracked(failure, conditional, where);

        return new StatBlockAction
        {
            Name = name,
            Kind = StatBlockValues.ActionKinds.Save,
            Slot = slot,
            Save = spec,
            Damage = damage,
            Area = area,
            Targets = targets,
            Condition = conditions.Count > 0 ? WithOngoing(conditions[0], ongoing) : null,
            ExtraConditions = conditions.Skip(1).ToList(),
            Magical = MagicalEffect().IsMatch(text),
            Usage = usage,
            Text = text,
        };
    }

    /// <summary>
    /// What a successful save does: the override, else the data, with the prose as the cross-check. A save the text
    /// halves but the data labels "none" is one of the five mislabeled 2014 actions (or a new one): the data is used and
    /// a data_conflict warning raised, unless an override settles it.
    /// </summary>
    private string OnSuccess(RecordAction a, string name, string text, bool hasDamage, string where)
    {
        var proseHalf = ProseText.SaysHalf(text);
        var fix = _override?.Actions?.GetValueOrDefault(name);
        if (fix?.OnSuccess is { } overridden)
        {
            _log.Note($"{Capitalize(where)}: a successful save takes {(overridden == StatBlockValues.OnSuccess.Half ? "half damage" : "no damage")} (overrides file: {fix.Note}).");
            return overridden;
        }

        var data = a.Dc?.SuccessType;
        if (data == SaveSuccessTypes.Half)
        {
            return hasDamage ? StatBlockValues.OnSuccess.Half : StatBlockValues.OnSuccess.None;
        }

        if (proseHalf && hasDamage)
        {
            if (a.Dc is null)
            {
                return StatBlockValues.OnSuccess.Half;
            }

            _log.Conflict(where, "The text halves the damage on a successful save but the data says none; the data is used.");
        }

        return StatBlockValues.OnSuccess.None;
    }

    /// <summary>A save action's text split into what it targets and what a failure does.</summary>
    private static (string Targeting, string Failure) SaveRegions(string text, SaveClause? clause)
    {
        var failure = FailureMarker().Match(text);
        if (failure.Success)
        {
            var rest = text[(failure.Index + failure.Length)..];
            var stop = SuccessMarker().Match(rest);
            return (text[..failure.Index], stop.Success ? rest[..stop.Index] : rest);
        }

        if (clause is null)
        {
            return (text, text);
        }

        // A later save clause ("Moving through the frigid air requires a DC 17 Constitution saving throw") is another
        // effect; the failure of the first ends where it begins.
        var rest2014 = text[clause.End..];
        var second = ProseText.ReadSave(rest2014);
        return (text[..clause.End], second is null ? rest2014 : rest2014[..ProseText.SentenceStart(rest2014, second.Index)]);
    }

    // 2014 "Breath Weapons": one save action per breath, all sharing the action's one recharge.
    private IReadOnlyList<StatBlockAction> BreathOptions(RecordAction a, Choice<BreathOption2014> options, string text, string slot)
    {
        var pool = $"recharge:{a.Name}";
        var usage = Usage(a.Usage, a.Name);
        if (usage.Kind == StatBlockValues.UsageKinds.Recharge)
        {
            usage = usage with { Pool = pool };
        }

        var result = new List<StatBlockAction>();
        var names = options.From.Options.Select(o => o.Name).ToList();
        foreach (var option in options.From.Options)
        {
            var start = text.IndexOf(option.Name + ".", StringComparison.Ordinal);
            var paragraph = start < 0 ? string.Empty : text[(start + option.Name.Length + 1)..];
            var next = names.Where(n => n != option.Name).Select(n => paragraph.IndexOf(n + ".", StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(paragraph.Length).Min();
            paragraph = paragraph[..next].Trim();
            var record = a with
            {
                Name = option.Name,
                Desc = paragraph,
                Dc = option.Dc,
                Damage = option.Damage ?? [],
                DamageChoices = [],
                BreathOptions = null,
            };
            var action = Save(record, option.Name, paragraph, slot, usage, option.Name);
            result.Add(action with { Notes = [.. action.Notes, $"One of {a.Name}; they share one recharge."] });
        }

        _log.Note($"{a.Name}: one save action per breath ({string.Join(", ", names)}), sharing one recharge.");
        return result;
    }

    // 2014 androsphinx Roar: its three roars as save actions, one use each.
    private IReadOnlyList<StatBlockAction> Roars(RecordAction a, IReadOnlyList<MonsterSubAttack2014> roars, string text, string slot)
    {
        var result = new List<StatBlockAction>();
        foreach (var roar in roars)
        {
            var start = text.IndexOf(roar.Name + ".", StringComparison.Ordinal);
            var paragraph = start < 0 ? string.Empty : text[(start + roar.Name.Length + 1)..];
            var next = roars.Where(r => r.Name != roar.Name).Select(r => paragraph.IndexOf(r.Name + ".", StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(paragraph.Length).Min();
            paragraph = paragraph[..next].Trim();
            // The roar paragraphs name no area; the action's own "Each creature within 500 feet of the sphinx" does.
            var intro = EachCreature().Match(text) is { Success: true } each ? each.Value + ". " : string.Empty;
            var record = a with { Name = roar.Name, Desc = intro + paragraph, Dc = roar.Dc, Damage = roar.Damage ?? [], DamageChoices = [], SubAttacks = null, Usage = null };
            result.Add(Save(record, roar.Name, intro + paragraph, slot, new UsageSpec(StatBlockValues.UsageKinds.PerDay, Uses: 1), roar.Name));
        }

        _log.Approximated(a.Name, "Its roars come in order (first, second, third); the simulator may use any one it has not used.");
        return result;
    }

    private static string FirstSentence(string text)
    {
        var end = ProseText.SentenceEnd(text, 0);
        return Excerpt(text[..end]);
    }

    private static string Excerpt(string text)
    {
        var trimmed = text.Trim().TrimStart('.', ',', ';', ' ');
        return trimmed.Length <= 140 ? trimmed : trimmed[..140].TrimEnd() + "…";
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"^The [a-z' -]+ makes one [A-Za-z' ]+ attack against\b", RegexOptions.IgnoreCase)]
    private static partial Regex MakesOneAttack();

    [GeneratedRegex(@"(?:each creature within \d+ feet[^.]*|One creature [^.]*) takes \d+ \(", RegexOptions.IgnoreCase)]
    private static partial Regex AutoDamage();

    [GeneratedRegex(@"adds (?<n>\d+) to its AC", RegexOptions.IgnoreCase)]
    private static partial Regex ParryBonus();

    [GeneratedRegex(@"^The [a-z' -]+ (?:magically )?(?:moves|teleports|flies|swims|jumps|shape-shifts|polymorphs|takes the (?:Dash|Disengage|Hide))[^.]*\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MovementOnly();

    [GeneratedRegex(@"^The [a-z' -]+ (?:magically )?regains", RegexOptions.IgnoreCase)]
    private static partial Regex SelfHeal();

    [GeneratedRegex(@"removes|ends|cures", RegexOptions.IgnoreCase)]
    private static partial Regex RemovesMore();

    [GeneratedRegex(@"one (?<who>willing creature[^.]*?|creature that (?:is|has)[^.]*?|[A-Za-z ]*?creature Grappled by[^.]*?|[A-Za-z ]*?Charmed[^.]*?)(?:\.|\s+Hit:)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetRestriction();

    [GeneratedRegex(@"^\s*(?:plus \d+ \(\d+d\d+(?:\s*[+-]\s*\d+)?\) [A-Za-z]+ damage\s+)?(?:at the (?:start|end) of each|every \d+|at the end of every)", RegexOptions.IgnoreCase)]
    private static partial Regex Ongoing();

    [GeneratedRegex(@"(?:,|—)?\s*or\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeBefore();

    [GeneratedRegex(@"^\s*(?:In addition,\s*)?At the (?:start|end) of each\b", RegexOptions.IgnoreCase)]
    private static partial Regex OngoingLead();

    [GeneratedRegex(@"^\s*While (?:swallowed|attached|engulfed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StateContinuation();

    [GeneratedRegex(@"^(?:While|Until|A swallowed|The swallowed|This)\b")]
    private static partial Regex Continuation();

    [GeneratedRegex(@"begins to turn to stone|Second Failure|First Failure", RegexOptions.IgnoreCase)]
    private static partial Regex TwoStage();

    [GeneratedRegex(@"until the web is destroyed|until the rope is destroyed|ends early|(?:if|until) (?:it|the creature|the target) takes damage|takes an action to (?:wake|shake)|uses an action to (?:wake|shake)", RegexOptions.IgnoreCase)]
    private static partial Regex EndsEarly();

    [GeneratedRegex(@"\bswallow", RegexOptions.IgnoreCase)]
    private static partial Regex SwallowPattern();

    [GeneratedRegex(@"with two hands", RegexOptions.IgnoreCase)]
    private static partial Regex TwoHands();

    [GeneratedRegex(@"^\s*(?:damage\s+)?if\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionalAfter();

    [GeneratedRegex(@"^If (?:the )?target is (?:a |an )?(?:(?:Tiny|Small|Medium|Large|Huge|Gargantuan) or smaller\b ?)?(?:creature|Humanoid|object)?(?: (?:that isn't|other than) (?:an? )?[A-Za-z]+(?:(?:,| or) (?:an? )?[A-Za-z]+)*)?,", RegexOptions.IgnoreCase)]
    private static partial Regex PlainIf();

    [GeneratedRegex(@"fails? (?:the saving throw |the save )?by 5 or more", RegexOptions.IgnoreCase)]
    private static partial Regex FailsByFive();

    [GeneratedRegex(@"Failure by 5 or More\s*:|Second Failure\s*:|begins to turn to stone|fails (?:the saving throw |the save )?by 5 or more", RegexOptions.IgnoreCase)]
    private static partial Regex EscalatesLater();

    [GeneratedRegex(@"(?:First )?Failure\s*:")]
    private static partial Regex FailureMarker();

    [GeneratedRegex(@"\b(?:Success|Failure or Success|Second Failure|Failure by 5 or More)\s*:")]
    private static partial Regex SuccessMarker();

    [GeneratedRegex(@"\beach (?:creature|enemy|Humanoid)[^.]*", RegexOptions.IgnoreCase)]
    private static partial Regex EachCreature();

    [GeneratedRegex(@"\bmagic(?:al|ally)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MagicalEffect();
}
