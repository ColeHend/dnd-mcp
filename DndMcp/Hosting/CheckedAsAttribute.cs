using System.Text.Json;

namespace DndMcp.Hosting;

/// <summary>
/// Marks an untyped (<c>object?</c>) tool parameter that the argument guard checks as <see cref="Type"/>: its fields
/// against that type's JSON schema, then a test-deserialize as that type. <see cref="SameShapeAsAttribute"/>'s sibling for
/// a parameter whose shape no other parameter of the tool has: <c>balance_simulate</c>'s <c>compare</c> holds a whole
/// feature (attacks and modifiers, about 12 KB of schema), which pushed the tool's definition to 31.8 KB of its 32 KB
/// budget, where the next DSL field would have broken it.
///
/// <para>
/// <b>What the model loses, and why it is enough.</b> The published schema is only the parameter's description, which
/// names the fields; the attacks and modifiers inside it are the same types as a build's, whose schema the tool does
/// publish (<c>party</c>). <see cref="ToolArgumentGuard"/> generates the type's schema with the server's JSON options
/// (the SDK's own generator), so a mistake inside the value gets the message a typed parameter would get ("argument
/// 'compare' field 'feature' field 'modifiers' item 1 has unknown field 'knd'"). Without the guard an untyped object would
/// bind whatever it held and a misspelt field would be silently ignored.
/// </para>
/// <para>
/// <b><see cref="Or"/>: one word accepted in place of the shape</b> (contract §15 H1). <c>encounter_difficulty</c>'s
/// <c>party</c> is a list of levels or the word <c>"campaign"</c> (the campaign's current party, its levels from the
/// sheets). A typed <c>int[]</c> could never receive the word (the guard and the binder refuse a string where an array
/// belongs), and an untyped parameter with no check would lose every party message the guard gives today ("argument
/// 'party' item 2 should be integer but was the string \"five\""). With the literal, a string equal to it (ignoring case
/// and surrounding spaces: <see cref="IsLiteral"/>, the one test the guard and the tool both use) passes untouched, and
/// every other value is checked exactly as before, so each existing message stays byte-identical; the guard's list of
/// accepted parameters says <c>party (array of integer or "campaign", required)</c>, so a model that sent another word
/// learns the one that works.
/// </para>
/// </summary>
/// <param name="type">The CLR type the value binds as, e.g. <c>typeof(CompareSpec)</c>.</param>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
internal sealed class CheckedAsAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;

    /// <summary>
    /// A word accepted in place of a value of <see cref="Type"/>, e.g. "campaign"; null (the default) accepts none. Set as
    /// a named argument: <c>[CheckedAs(typeof(int[]), Or = "campaign")]</c>.
    /// </summary>
    public string? Or { get; set; }

    /// <summary>
    /// True when <paramref name="value"/> is the string <see cref="Or"/>, ignoring case and surrounding white space
    /// (<c>"Campaign"</c>, <c>" campaign "</c>); false for any other value, and always when there is no literal. The guard
    /// skips its shape check on exactly these values and the tool reads exactly these as the word, so a spelling the guard
    /// let through is never one the tool then refuses.
    /// </summary>
    public bool IsLiteral(JsonElement value) =>
        Or is { } literal && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString()?.Trim(), literal, StringComparison.OrdinalIgnoreCase);
}
