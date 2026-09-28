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
/// </summary>
/// <param name="type">The CLR type the value binds as, e.g. <c>typeof(CompareSpec)</c>.</param>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
internal sealed class CheckedAsAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}
