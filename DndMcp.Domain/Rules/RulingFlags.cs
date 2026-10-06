namespace DndMcp.Domain.Rules;

/// <summary>
/// The rulings the shared combat rules make where the SRD leaves a choice to the table, as the wire names a result echoes
/// when one decided something ("echo the ones used"). They are constants rather than options: v1 of the tracker, the
/// sheet and the simulator all play them the same way, and the echo is what lets a table that rules otherwise see where
/// the number came from.
/// </summary>
/// <remarks>
/// Strings on the wire, never renamed: a combat_log row's <c>detail</c> stores them, so a renamed flag would make old rows
/// say something no build recognises. Add values; never rename one.
/// </remarks>
public static class RulingFlags
{
    /// <summary>
    /// The concentration DC and a death-save failure taken at 0 HP read the damage total BEFORE temporary hit points absorb
    /// it: 35 with 7 absorbed is DC 17, and damage temporary hit points absorb entirely still costs a dying creature a
    /// failure. SRD 5.1 "If you take any damage while you have 0 hit points" and "half the damage you take" do not say
    /// whether absorbed damage counts; the simulator counts it (<c>Fight.ApplyDamage</c> tests the total), so the tracker
    /// does too, and a sheet and its simulated twin agree.
    /// </summary>
    public const string ConcentrationOnPreTempDamage = "concentration_on_pre_temp_damage";

    /// <summary>
    /// A grant of temporary hit points to a creature that has some keeps the higher of the two. RAW the creature chooses
    /// (SRD 5.1 "you decide whether to keep the ones you have or to gain the new ones"; SRD 5.2 the same); keeping the
    /// higher is that choice nearly always and is what the simulator does (<c>Fight.GainTempHp</c>).
    /// </summary>
    public const string TempHpKeepHigher = "temp_hp_keep_higher";

    /// <summary>
    /// Equal initiative totals are ordered by the higher initiative bonus, then by insertion order (<c>order_key</c>), and
    /// the tie is flagged. RAW the GM and players decide (SRD 5.1 "Initiative"; SRD 5.2 the same); a deterministic default
    /// keeps scripted fights exact, and the flag says how to reorder (a total such as 14.5).
    /// </summary>
    public const string InitiativeTiesByBonusThenOrder = "initiative_ties_by_bonus_then_order";

    /// <summary>
    /// A duration in rounds ("1 minute" = 10) ends at the START of its anchor's turn in round <c>applied + N</c>: exactly
    /// when "1 minute has passed" (SRD 5.2 Spells). The simulator's counter ends about half a turn sooner; seeding a
    /// simulation from a live fight accepts that difference (contract D12).
    /// </summary>
    public const string RoundsExpireRaw = "rounds_expire_raw";

    /// <summary>Every flag, for tests and validation.</summary>
    public static readonly IReadOnlyList<string> All =
        [ConcentrationOnPreTempDamage, TempHpKeepHigher, InitiativeTiesByBonusThenOrder, RoundsExpireRaw];
}
