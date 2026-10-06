using DndMcp.Domain.Characters;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// A sheet as the AUTHOR sees it (contract §7.3): everything, as one screen. Author-only: it carries the whole parsed
/// <see cref="CharacterSheet"/> (player, notes, spells, resources, sim_profile) and the holdings and coins, none of which
/// a non-author view may print. The host's formatter decides the layout; the derived numbers are here so it never
/// re-derives a rule (the effective maximum is <see cref="Domain.Rules.HitPointMath"/>'s).
/// </summary>
/// <param name="Ref">The character's author handle (<c>character:belmakor</c>).</param>
/// <param name="Name">The character's name.</param>
/// <param name="Edition">The ruleset the sheet follows: its own, else the campaign's (a mixed campaign's default 2024).</param>
/// <param name="Sheet">The stored sheet, parsed (as of the read's session, when one was asked for).</param>
/// <param name="EffectiveMaxHp">The effective hit point maximum, or null without a max_hp.</param>
/// <param name="ProficiencyBonus">By the sheet's level, or null without one.</param>
/// <param name="InitiativeBonus">The stored bonus, else the Dex modifier, else 0.</param>
/// <param name="Dying">At 0 HP, not stable and not dead.</param>
/// <param name="Dead">Three failed death saves, Exhaustion 6 or an effective maximum of 0.</param>
/// <param name="NextXpThreshold">The XP of the next level, when the sheet tracks XP and has a level below 20.</param>
/// <param name="Inventory">Holdings with a quantity above 0, in the order they were gained.</param>
/// <param name="Coins">The coin balance (the sum of the currency ledger).</param>
/// <param name="SimProfile">A summary of the stored sim_profile, or null without one.</param>
public sealed record AuthorSheetView(
    string Ref,
    string Name,
    string Edition,
    CharacterSheet Sheet,
    int? EffectiveMaxHp,
    int? ProficiencyBonus,
    int InitiativeBonus,
    bool Dying,
    bool Dead,
    int? NextXpThreshold,
    IReadOnlyList<HoldingView> Inventory,
    CoinsView Coins,
    SimProfileView? SimProfile);

/// <summary>One holding (author view; holdings are author-only in v1, contract D20).</summary>
/// <param name="Name">The item's name as stored (the linked item entity's name when it has one).</param>
/// <param name="Quantity">How many (fractional for weights and measures).</param>
/// <param name="SrdRef">The SRD entry it is, when given.</param>
/// <param name="Equipped">Equipped.</param>
/// <param name="Attuned">Attuned.</param>
/// <param name="Charges">Its charges object as JSON text, when it has one.</param>
/// <param name="Notes">The author's note.</param>
/// <param name="Item">The linked item entity's author handle, when there is one.</param>
public sealed record HoldingView(string Name, double Quantity, string? SrdRef, bool Equipped, bool Attuned, string? Charges, string? Notes, string? Item);

/// <summary>A coin balance: each denomination's sum over the character's currency ledger (author view).</summary>
public sealed record CoinsView(long Cp, long Sp, long Ep, long Gp, long Pp)
{
    /// <summary>No coins of any kind (every sum 0).</summary>
    public bool IsEmpty => Cp == 0 && Sp == 0 && Ep == 0 && Gp == 0 && Pp == 0;

    /// <summary>A denomination is below 0 (more was taken out than was recorded in).</summary>
    public bool HasNegative => Cp < 0 || Sp < 0 || Ep < 0 || Gp < 0 || Pp < 0;
}

/// <summary>A stored sim_profile in brief (author view): its name, edition and level, and its attacks and modifiers by name.</summary>
/// <param name="Unreadable">The stored profile no longer reads as a build (a later version's shape): nothing else is filled.</param>
public sealed record SimProfileView(string? Name, string? Edition, int? Level, IReadOnlyList<string> Attacks, IReadOnlyList<string> Modifiers, bool Unreadable = false);

/// <summary>
/// The sheet line a NON-AUTHOR view gets (contract §7.4), and nothing else: the name as the view knows it, the level, the
/// classes (an SRD class by its SRD name; a homebrew class name and every subclass only when they pass the view-text
/// check, else left out), the species (likewise), hit points, AC, exhaustion and conditions by name (a non-SRD name that
/// fails the check is "an effect"). Built only for a current party member the view is shown undisguised; the model holds
/// exactly what it prints (the leak rule): never the player, lineage, background, abilities, saves, resources, slots,
/// spells, feats, features, inventory, coins, XP, sim_profile, notes, sheet_source or concentration.
/// </summary>
/// <param name="Ref">The view's ref for the character (<c>character:slug</c> or <c>e:&lt;n&gt;</c>).</param>
/// <param name="Name">The name the view knows it by.</param>
/// <param name="Level">The level, when the sheet has one.</param>
/// <param name="Classes">The classes the view may read.</param>
/// <param name="Species">The species, when it passes the check.</param>
/// <param name="Hp">Current hit points, when tracked.</param>
/// <param name="EffectiveMaxHp">The effective maximum, when tracked.</param>
/// <param name="TempHp">Temporary hit points.</param>
/// <param name="Ac">Armor class, when on the sheet.</param>
/// <param name="Exhaustion">The Exhaustion level (0 = none).</param>
/// <param name="Conditions">Condition names (SRD names; others as passed, else "an effect").</param>
public sealed record PublicSheetLine(
    string Ref,
    string Name,
    int? Level,
    IReadOnlyList<PublicClassLine> Classes,
    string? Species,
    int? Hp,
    int? EffectiveMaxHp,
    int TempHp,
    int? Ac,
    int Exhaustion,
    IReadOnlyList<string> Conditions);

/// <summary>One class on the public line: its name (an SRD class by its SRD name), its level, and its subclass when it passes.</summary>
public sealed record PublicClassLine(string Name, int Level, string? Subclass);

/// <summary>
/// The sheet part of a <c>campaign_get</c> entity (<c>include: ["sheet"]</c>): the author view for the author, the public
/// line for any other view, never both. Absent (null on the detail) when the view gets no sheet: the character has none,
/// or the view may not be shown it (§7.4: an NPC's sheet, a dead or departed member's, a disguised one's read exactly as
/// none).
/// </summary>
public sealed record SheetView(AuthorSheetView? Author, PublicSheetLine? Line);

/// <summary>One character a <c>campaign_character get</c> shows: its sheet in the view's form, or none.</summary>
/// <param name="Ref">The view's ref for it (the author's <c>character:slug</c>).</param>
/// <param name="Name">The name the view knows it by.</param>
/// <param name="Author">The author view of its sheet (author reads only).</param>
/// <param name="Line">The public line (non-author reads only).</param>
public sealed record CharacterSheetRead(string Ref, string Name, AuthorSheetView? Author, PublicSheetLine? Line)
{
    /// <summary>The view gets a sheet for it; false reads as "no sheet yet" (§7.1), whether it has none or may not be shown one.</summary>
    public bool HasSheet => Author is not null || Line is not null;
}

/// <summary>What <c>campaign_character get</c> returns.</summary>
/// <param name="Campaign">The campaign's slug.</param>
/// <param name="Perspective">The view's perspective text (<c>author</c>, <c>party</c>, <c>character:vars</c>).</param>
/// <param name="AuthorView">The author view (author, or dm in a DM campaign).</param>
/// <param name="IsList">The list form (a DM campaign and no character): every current member the view may be shown.</param>
/// <param name="Characters">One character, or the list.</param>
/// <param name="Excluded">
/// The list form's dead or departed members linked to the party, for the author's note; always empty outside the author
/// view (no count of what a view is not shown).
/// </param>
public sealed record SheetGetResult(
    string Campaign,
    string Perspective,
    bool AuthorView,
    bool IsList,
    IReadOnlyList<CharacterSheetRead> Characters,
    IReadOnlyList<ExcludedMember> Excluded);
