using System.Globalization;
using System.Text;
using DndMcp.Domain.Characters;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The markdown of a character sheet as one view reads it (contract §7.3, §7.4): <c>campaign_character get</c> (one
/// character, or a DM campaign's list form), the <c>campaign://&lt;slug&gt;/party</c> resource, and the sheet section of
/// <c>campaign_get include: ["sheet"]</c> (<see cref="EntityMarkdown"/>).
///
/// <para>
/// <b>Two shapes, never mixed.</b> The author gets the whole sheet as one screen (<see cref="AuthorSheetView"/>): a title
/// line <c>&lt;name&gt; — level &lt;L&gt; &lt;class (subclass) / class …&gt;, &lt;ruleset&gt;</c>, the hit point line
/// (<c>HP 96/110 (+7 temp) · AC 17 · Init +4 · PB +4 · Exhaustion 0</c>), then slots, resources, conditions,
/// concentration, death saves, abilities, saves, defenses, XP, inventory, coins and the sim_profile. Any other view gets
/// the public line (<see cref="PublicSheetLine"/>) and only what that record holds: the name it knows, level, classes,
/// species, hit points, AC, exhaustion and condition names. The reader has already decided what the line may say (the
/// view-text check, and which characters get a line at all), so this formatter never prints a label, count or
/// placeholder for something absent: a field the line leaves out is not mentioned, a member the list leaves out is not
/// counted, and the words of the author view ("Lineage", "Player", "Resources", "sim_profile") appear only in the author
/// view. A field added to the author record is rendered only if it is added here, inside the author view.
/// </para>
/// <para>
/// <b>No sheet reads the same whatever the reason</b> (§7.4): a character with none, and one whose sheet the view may not
/// be shown (an NPC's, a dead member's, a disguised one's), both read "&lt;Name&gt; — no sheet yet", so the text never says
/// that a sheet exists. Only the author's read adds the update call that makes one (§7.1, fix F1 L06): in another view
/// that call named a sheet the view is not shown, and the model, who is the author, followed it and patched that real
/// sheet (the old king's classes, level and HP) instead of making one.
/// </para>
/// <para>
/// <b>The live fight</b> (fix F1, U04): an author read of a character that is a sheet-seeded combatant of the active fight
/// still prints the sheet (D5) and adds <see cref="SheetLiveFight"/>'s one line with where the fight has it now.
/// </para>
/// </summary>
internal static class SheetMarkdown
{
    /// <summary>Entries of a list (feats, spells, inventory) shown before "… and N more".</summary>
    public const int MaxListed = 60;

    /// <summary>How much of the sheet's notes the author view shows.</summary>
    public const int MaxNotes = 4_000;

    private const string CapHint = "the sheet is complete; read one character at a time, or campaign_get with include [\"sheet\"]";

    /// <summary>
    /// The XP line of a sheet with no XP value (fix F2, review U02). It says only what is true: no total is recorded, and the
    /// action that starts one. "not tracked (milestone levels)" read as a levelling policy the DM had chosen, which nobody
    /// had (a sheet made without xp has none), so a model asked to split a fight's XP left every sheet without it and
    /// declined to start a total, "because that changes how levelling works for the whole campaign".
    /// </summary>
    internal const string NoXpTotal = "none recorded (add a total with campaign_character xp to track it)";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] AbilityOrder = ["str", "dex", "con", "int", "wis", "cha"];

    /// <summary><c>campaign_character get</c>: one character's sheet in the view's form, or the list form.</summary>
    public static string FormatGet(CampaignRow campaign, SheetGetResult result, CampaignView view)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(view);
        if (result.IsList)
        {
            return FormatList(campaign, result, view.Banner, view.LiveFight);
        }

        var read = result.Characters.Single();
        var b = new StringBuilder();
        if (read.Author is { } author)
        {
            b.Append("# ").Append(Title(author)).Append('\n');
            AppendAuthorBody(b, author, SheetLiveFight.For(view.LiveFight, author.Ref));
        }
        else if (read.Line is { } line)
        {
            b.Append("# ").Append(line.Name).Append('\n');
            Banner(b, view.Banner);
            b.Append('\n').Append(PublicLineText(line)).Append('\n');
        }
        else
        {
            b.Append("# ").Append(read.Name).Append(" — no sheet yet\n");
            Banner(b, view.Banner);
            if (result.AuthorView)
            {
                b.Append('\n').Append(NoSheetText(campaign.Slug, read.Ref)).Append('\n');
            }
        }

        return CampaignMarkdownText.Cap(b.ToString(), CapHint);
    }

    /// <summary>
    /// The list form (a DM campaign's <c>get</c> with no character) and <c>campaign://&lt;slug&gt;/party</c>: one line per
    /// current party member the view is given. The author's list has every member (one without a sheet with the call that
    /// makes one) and names the dead or departed members left out; another view's has only the lines the reader gave and
    /// nothing about the rest. A member in the live fight from its sheet gets its <see cref="SheetLiveFight"/> line under its own.
    /// </summary>
    /// <param name="live">The live-fight lines by handle, for an author read now; null for none.</param>
    public static string FormatList(CampaignRow campaign, SheetGetResult result, string? banner, IReadOnlyDictionary<string, string>? live = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        var b = new StringBuilder("# ").Append(campaign.Name).Append(": the party's sheets\n");
        Banner(b, banner);
        b.Append('\n');
        var shown = 0;
        foreach (var read in result.Characters)
        {
            if (read.Author is { } author)
            {
                b.Append("- ").Append(AuthorLineText(author)).Append('\n');
                if (SheetLiveFight.For(live, author.Ref) is { } line)
                {
                    b.Append("  - ").Append(line).Append('\n');
                }
            }
            else if (read.Line is { } line)
            {
                b.Append("- ").Append(PublicLineText(line)).Append('\n');
            }
            else if (result.AuthorView)
            {
                b.Append("- **").Append(read.Name).Append("** (`").Append(read.Ref).Append("`) — no sheet yet: ")
                    .Append(UpdateCall(campaign.Slug, read.Ref)).Append('\n');
            }
            else
            {
                continue;
            }

            shown++;
        }

        if (shown == 0)
        {
            b.Append(result.AuthorView
                ? $"No party sheets yet: {UpdateCall(campaign.Slug, "character:…")} makes one for a member (link a character member_of the party first).\n"
                : "Nothing to show.\n");
        }

        if (result.AuthorView && result.Excluded.Count > 0)
        {
            b.Append("\nNot in the party now: ")
                .Append(string.Join(", ", result.Excluded.Select(e => $"{e.Name} (`{e.Handle}`, {e.Reason})")))
                .Append(".\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), CapHint);
    }

    /// <summary>
    /// The sheet section of a <c>campaign_get</c> entity (<c>include: ["sheet"]</c>): the author view without its title
    /// (the entity's own heading names the character), or the public line.
    /// </summary>
    /// <param name="live">The character's live-fight line (author view, now), or null.</param>
    public static void AppendSection(StringBuilder b, SheetView sheet, string heading, string? live = null)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(sheet);
        b.Append('\n').Append(heading).Append("Sheet\n");
        if (sheet.Author is { } author)
        {
            b.Append(Summary(author)).Append('\n');
            AppendAuthorBody(b, author, live);
        }
        else if (sheet.Line is { } line)
        {
            b.Append(PublicLineText(line)).Append('\n');
        }
    }

    /// <summary>"Belmakor Silverwind — level 12 Wizard (Bladesinger), 2014": the author view's title line.</summary>
    public static string Title(AuthorSheetView author) => author.Name + " — " + Summary(author);

    /// <summary>"level 12 Wizard (Bladesinger), 2014", or "no level yet, 2014".</summary>
    public static string Summary(AuthorSheetView author)
    {
        ArgumentNullException.ThrowIfNull(author);
        var sheet = author.Sheet;
        var classes = Classes(sheet.Classes.Select(c => (c.Srd?.Name ?? c.Class, c.Level, c.Subclass)).ToList(), sheet.Level);
        var level = sheet.Level is { } l ? "level " + Number(l) : "no level yet";
        return level + (classes.Length > 0 ? " " + classes : string.Empty) + ", " + author.Edition;
    }

    /// <summary>
    /// The public line (§7.4): "**Belmakor Silverwind** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf ·
    /// HP 96/110 (+7 temp) · AC 17 · Exhaustion 1 · poisoned, an effect". Only the record's fields, each only when present.
    /// </summary>
    public static string PublicLineText(PublicSheetLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var b = new StringBuilder("**").Append(line.Name).Append("** (`").Append(line.Ref).Append("`)");
        var what = new List<string>();
        if (line.Level is { } level)
        {
            what.Add("level " + Number(level));
        }

        var classes = Classes(line.Classes.Select(c => (c.Name, c.Level, c.Subclass)).ToList(), line.Level);
        if (classes.Length > 0)
        {
            what.Add(classes);
        }

        var parts = new List<string>();
        if (what.Count > 0 || line.Species is not null)
        {
            parts.Add(string.Join(" ", what) + (line.Species is { } species ? (what.Count > 0 ? ", " : string.Empty) + species : string.Empty));
        }

        if (line.Hp is { } hp && line.EffectiveMaxHp is { } max)
        {
            parts.Add($"HP {Number(hp)}/{Number(max)}" + (line.TempHp > 0 ? $" (+{Number(line.TempHp)} temp)" : string.Empty));
        }

        if (line.Ac is { } ac)
        {
            parts.Add("AC " + Number(ac));
        }

        if (line.Exhaustion > 0)
        {
            parts.Add("Exhaustion " + Number(line.Exhaustion));
        }

        if (line.Conditions.Count > 0)
        {
            parts.Add(string.Join(", ", line.Conditions));
        }

        if (parts.Count > 0)
        {
            b.Append(" — ").Append(string.Join(" · ", parts));
        }

        return b.ToString();
    }

    /// <summary>
    /// One member in the author's list form: name and handle, the summary, then the hit point line's numbers and what is on
    /// the sheet now (conditions, concentration, dying or dead).
    /// </summary>
    public static string AuthorLineText(AuthorSheetView author)
    {
        ArgumentNullException.ThrowIfNull(author);
        var sheet = author.Sheet;
        var parts = new List<string> { Summary(author), HitPointsText(author), "AC " + (sheet.Ac is { } ac ? Number(ac) : "—"), "Init " + Signed(author.InitiativeBonus) };
        if (sheet.Exhaustion > 0)
        {
            parts.Add("Exhaustion " + Number(sheet.Exhaustion));
        }

        if (sheet.Conditions.Count > 0)
        {
            parts.Add(string.Join(", ", sheet.Conditions.Select(c => OneLine(c.Name))));
        }

        if (sheet.Concentration is { } concentration)
        {
            parts.Add("concentrating on " + OneLine(concentration.Display));
        }

        if (DeathText(author) is { } death)
        {
            parts.Add(death);
        }

        return $"**{author.Name}** (`{author.Ref}`) — {string.Join(" · ", parts)}";
    }

    /// <summary>
    /// "No sheet yet. Make one with campaign_character {…}." for the AUTHOR's read of a character with none (another view's
    /// "no sheet yet" prints its heading and banner only: fix F1, L06).
    /// </summary>
    public static string NoSheetText(string campaignSlug, string reference) =>
        $"No sheet yet. Make one with {UpdateCall(campaignSlug, reference)}; give classes, ac and max_hp as well when you have them.";

    /// <summary>
    /// The update call that creates a sheet, the contract's (§7.1) with the campaign appended as every sheet reminder names
    /// it: <c>campaign_character {"action": "update", "character": …, "sheet": {"level": …}, "campaign": …}</c>.
    /// </summary>
    public static string UpdateCall(string campaignSlug, string reference) =>
        $"campaign_character {{\"action\": \"update\", \"character\": \"{reference}\", \"sheet\": {{\"level\": …}}, \"campaign\": \"{campaignSlug}\"}}";

    // Everything below the title line of the author view (§7.3), with the live-fight line (when there is one) right under the
    // hit point line it qualifies.
    private static void AppendAuthorBody(StringBuilder b, AuthorSheetView author, string? live)
    {
        var sheet = author.Sheet;
        var who = new List<string> { $"`{author.Ref}`" };
        AddIf(who, sheet.Player, "player ");
        AddIf(who, sheet.Species, string.Empty);
        AddIf(who, sheet.Lineage, "lineage ");
        AddIf(who, sheet.Background, "background ");
        AddIf(who, sheet.SheetSource, "source ");
        b.Append(string.Join(" · ", who)).Append('\n');

        var line = new List<string>
        {
            HitPointsText(author),
            "AC " + (sheet.Ac is { } ac ? Number(ac) : "—"),
            "Init " + Signed(author.InitiativeBonus),
            "PB " + (author.ProficiencyBonus is { } pb ? Signed(pb) : "—"),
            "Exhaustion " + Number(sheet.Exhaustion),
        };
        b.Append(string.Join(" · ", line)).Append("\n\n");
        if (live is not null)
        {
            b.Append("- ").Append(live).Append('\n');
        }

        if (DeathText(author) is { } death)
        {
            b.Append("- **").Append(death).Append("**\n");
        }

        Item(b, "Spell slots", sheet.SpellSlots.OrderBy(s => SlotOrder(s.Key)).Select(s => SlotText(s.Key, s.Value)));
        Item(b, "Resources", sheet.Resources.Values.Select(ResourceText));
        Item(b, "Conditions", sheet.Conditions.Select(ConditionText));
        if (sheet.Concentration is { } concentration)
        {
            b.Append("- **Concentration:** ").Append(ConcentrationText(concentration)).Append('\n');
        }

        Item(b, "Abilities", AbilityOrder.Where(sheet.Abilities.ContainsKey)
            .Select(a => $"{Capital(a)} {Number(sheet.Abilities[a])} ({Signed(sheet.Modifier(a) ?? 0)})"));
        var saves = new List<string>();
        if (sheet.Saves.Abilities.Count > 0)
        {
            saves.Add("proficient " + string.Join(", ", sheet.Saves.Abilities.Select(Capital)));
        }

        saves.AddRange(sheet.Saves.Bonus.OrderBy(p => Array.IndexOf(AbilityOrder, p.Key)).Select(p => $"{Capital(p.Key)} {Signed(p.Value)}"));
        Item(b, "Saves", saves);
        var defenses = new List<string>();
        DefenseList(defenses, "resist", sheet.Defenses.Resist);
        DefenseList(defenses, "immune", sheet.Defenses.Immune);
        DefenseList(defenses, "vulnerable", sheet.Defenses.Vulnerable);
        DefenseList(defenses, "condition immune", sheet.Defenses.ConditionImmune);
        Item(b, "Defenses", defenses);
        Item(b, "Hit Dice", sheet.HitDice.OrderByDescending(d => SheetJson.DieOf(d.Key)).Select(d => $"{d.Key} {Number(d.Value.Left)}/{Number(d.Value.Max)} left"));

        b.Append("- **XP:** ").Append(sheet.Xp is { } xp
            ? Thousands(xp) + (author.NextXpThreshold is { } next ? $" (the next level at {Thousands(next)})" : string.Empty)
            : NoXpTotal).Append('\n');
        var other = new List<string>();
        AddIf(other, sheet.Speed is { } speed ? Number(speed) + " ft" : null, "Speed ");
        AddIf(other, sheet.PassivePerception is { } passive ? Number(passive) : null, "Passive Perception ");
        AddIf(other, sheet.SpellSaveDc is { } dc ? Number(dc) : null, "Spell save DC ");
        AddIf(other, sheet.SpellAttack is { } attack ? Signed(attack) : null, "Spell attack ");
        if (sheet.Inspiration)
        {
            other.Add("Inspiration");
        }

        Item(b, "Other", other);
        Item(b, "Feats", sheet.Feats.Select(EntryText));
        Item(b, "Features", sheet.Features.Select(EntryText));
        Item(b, "Spells", sheet.Spells.Select(EntryText));
        Item(b, "Languages", sheet.Languages.Select(EntryText));
        Item(b, "Inventory", author.Inventory.Select(HoldingText));
        b.Append("- **Coins:** ").Append(author.Coins.IsEmpty ? "none" : CoinsText(author.Coins)).Append('\n');
        if (author.Coins.HasNegative)
        {
            b.Append("  - A coin balance is below 0: more was spent than the ledger holds.\n");
        }

        if (author.SimProfile is { } profile)
        {
            b.Append("- **sim_profile:** ").Append(SimProfileText(profile)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(sheet.NotesMd))
        {
            var notes = sheet.NotesMd.Trim();
            b.Append("\n**Notes:**\n").Append(notes.Length <= MaxNotes ? notes : notes[..CampaignMarkdownText.WholeCharacters(notes, MaxNotes)] + "…").Append('\n');
        }
    }

    // "HP 96/110 (+7 temp)", with the maximum's reduction when there is one; "HP not tracked" without a maximum.
    private static string HitPointsText(AuthorSheetView author)
    {
        var sheet = author.Sheet;
        if (sheet.Hp is null && author.EffectiveMaxHp is null)
        {
            return "HP not tracked";
        }

        var text = $"HP {(sheet.Hp is { } hp ? Number(hp) : "—")}/{(author.EffectiveMaxHp is { } max ? Number(max) : "—")}";
        if (sheet.TempHp > 0)
        {
            text += $" (+{Number(sheet.TempHp)} temp)";
        }

        if (sheet.MaxHpReduction > 0 && sheet.MaxHp is { } full)
        {
            text += $" (maximum {Number(full)}, reduced by {Number(sheet.MaxHpReduction)})";
        }

        return text;
    }

    // Dying, stable or dead: what the death saves say (null when none applies). A death is stored as three failures
    // whatever caused it (massive damage, damage at 0 of at least the maximum, the saves themselves), so only the causes the
    // sheet itself shows are named (fix F1, C10): "three failed death saves" for a massive-damage death was a cause that
    // did not happen.
    private static string? DeathText(AuthorSheetView author)
    {
        var saves = author.Sheet.DeathSaves;
        if (author.Dead)
        {
            return author.Sheet.Exhaustion >= SheetLimits.MaxExhaustion ? "Dead (exhaustion 6)"
                : author.EffectiveMaxHp == 0 ? "Dead (a hit point maximum of 0)"
                : "Dead";
        }

        if (author.Dying)
        {
            return $"Dying: {Plural(saves.Successes, "success", "successes")}, {Plural(saves.Failures, "failure", "failures")}";
        }

        return author.Sheet.Hp == 0 && saves.Stable ? "Stable at 0 HP" : null;
    }

    // "1st 3/4", "pact (3rd) 1/2": slots left of the maximum.
    private static string SlotText(string key, SpellSlotEntry slot)
    {
        var label = key == SpellSlotEntry.PactKey
            ? "pact" + (slot.Level is { } level ? $" ({Ordinal(level)})" : string.Empty)
            : int.TryParse(key, NumberStyles.None, Invariant, out var spellLevel) ? Ordinal(spellLevel) : key;
        return $"{label} {Number(slot.Left)}/{Number(slot.Max)}";
    }

    // Spell levels in order, then pact, then anything else.
    private static int SlotOrder(string key) =>
        int.TryParse(key, NumberStyles.None, Invariant, out var level) ? level : key == SpellSlotEntry.PactKey ? 100 : 200;

    // "Bladesong 3/4 (long rest)", "Contingency: set (Polymorph → T-rex at low HP)".
    private static string ResourceText(SheetResource resource)
    {
        var text = resource.IsCounted
            ? $"{OneLine(resource.Name)} {Number(resource.Left)}/{Number(resource.Max ?? 0)} ({resource.RechargeOrDefault.Replace('_', ' ')})"
            : $"{OneLine(resource.Name)}: {OneLine(resource.State ?? "—")}";
        return string.IsNullOrWhiteSpace(resource.Note) ? text : $"{text} — {OneLine(resource.Note)}";
    }

    // "cursed (Mucus Cloud) (until removed; from Aboleth)", "Bladesong (98 rounds left; note)".
    private static string ConditionText(SheetCondition condition)
    {
        var about = new List<string>
        {
            condition.IsTimed && condition.RemainingRounds is { } rounds ? Plural(rounds, "round", "rounds") + " left" : condition.DurationOrDefault.Replace('_', ' '),
        };
        AddIf(about, condition.Source, "from ");
        AddIf(about, condition.Note, string.Empty);
        return $"{OneLine(condition.Name)} ({string.Join("; ", about)})";
    }

    // "Circle of Power (5th level; 98 rounds left)".
    private static string ConcentrationText(SheetConcentration concentration)
    {
        var about = new List<string>();
        if (concentration.Level is { } level)
        {
            about.Add(Ordinal(level) + " level");
        }

        if (concentration.RemainingRounds is { } rounds)
        {
            about.Add(Plural(rounds, "round", "rounds") + " left");
        }

        AddIf(about, concentration.Note, string.Empty);
        return OneLine(concentration.Display) + (about.Count > 0 ? $" ({string.Join("; ", about)})" : string.Empty);
    }

    private static void DefenseList(List<string> parts, string label, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            parts.Add(label + " " + string.Join(", ", values.Select(OneLine)));
        }
    }

    private static string EntryText(SheetEntry entry)
    {
        var text = OneLine(entry.Name);
        if (entry.Prepared == true)
        {
            text += " (prepared)";
        }

        return entry.Ref is { } reference ? $"{text} (`{reference}`)" : text;
    }

    // "Potion of Healing ×2 (`2024/equipment/potion-of-healing`; equipped)".
    private static string HoldingText(HoldingView holding)
    {
        var about = new List<string>();
        if (holding.SrdRef is { } srd)
        {
            about.Add($"`{srd}`");
        }

        if (holding.Item is { } item)
        {
            about.Add($"`{item}`");
        }

        if (holding.Equipped)
        {
            about.Add("equipped");
        }

        if (holding.Attuned)
        {
            about.Add("attuned");
        }

        AddIf(about, holding.Charges, "charges ");
        AddIf(about, holding.Notes, string.Empty);
        return $"{OneLine(holding.Name)} ×{holding.Quantity.ToString("0.###", Invariant)}" + (about.Count > 0 ? $" ({string.Join("; ", about)})" : string.Empty);
    }

    // "120 gp, 3 sp": every denomination that is not 0, largest first.
    private static string CoinsText(CoinsView coins) =>
        string.Join(", ", new (long Value, string Name)[] { (coins.Pp, "pp"), (coins.Gp, "gp"), (coins.Ep, "ep"), (coins.Sp, "sp"), (coins.Cp, "cp") }
            .Where(c => c.Value != 0)
            .Select(c => c.Value.ToString("N0", Invariant) + " " + c.Name));

    private static string SimProfileText(SimProfileView profile)
    {
        if (profile.Unreadable)
        {
            return "stored, but it no longer reads as a build; give sim_profile again";
        }

        var head = new List<string>();
        AddIf(head, profile.Name, string.Empty);
        AddIf(head, profile.Edition, string.Empty);
        AddIf(head, profile.Level is { } level ? Number(level) : null, "level ");
        var text = head.Count > 0 ? string.Join(", ", head) : "a build";
        if (profile.Attacks.Count > 0)
        {
            text += "; attacks " + string.Join(", ", profile.Attacks.Select(OneLine));
        }

        if (profile.Modifiers.Count > 0)
        {
            text += "; modifiers " + string.Join(", ", profile.Modifiers.Select(OneLine));
        }

        return text;
    }

    // "Wizard (Bladesinger)" for one class at the sheet's level; "Ranger 6 / Rogue 6" with each class's level otherwise.
    private static string Classes(IReadOnlyList<(string Name, int Level, string? Subclass)> classes, int? level)
    {
        var single = classes.Count == 1 && classes[0].Level == level;
        return string.Join(" / ", classes.Select(c =>
            OneLine(c.Name) + (single ? string.Empty : " " + Number(c.Level)) + (string.IsNullOrWhiteSpace(c.Subclass) ? string.Empty : $" ({OneLine(c.Subclass)})")));
    }

    // "- **Spell slots:** 1st 3/4 · 2nd 3/3", nothing when the list is empty, at most MaxListed entries.
    private static void Item(StringBuilder b, string label, IEnumerable<string> values)
    {
        var all = values.ToList();
        if (all.Count == 0)
        {
            return;
        }

        b.Append("- **").Append(label).Append(":** ").Append(string.Join(" · ", all.Take(MaxListed)));
        if (all.Count > MaxListed)
        {
            b.Append(" · … and ").Append(Number(all.Count - MaxListed)).Append(" more");
        }

        b.Append('\n');
    }

    private static void Banner(StringBuilder b, string? banner)
    {
        if (banner is not null)
        {
            b.Append(banner).Append('\n');
        }
    }

    private static void AddIf(List<string> parts, string? value, string prefix)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(prefix + OneLine(value));
        }
    }

    private static string Capital(string ability) => ability.Length == 0 ? ability : char.ToUpperInvariant(ability[0]) + ability[1..];

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => Number(n) + "th",
    };

    private static string Plural(int count, string one, string many) => Number(count) + " " + (count == 1 ? one : many);

    private static string Signed(int value) => SrdMarkdownText.Signed(value);

    private static string Number(long value) => value.ToString(Invariant);

    private static string Thousands(long value) => value.ToString("N0", Invariant);
}
