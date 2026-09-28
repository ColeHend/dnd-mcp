namespace DndMcp.Hosting;

/// <summary>
/// Marks an untyped (<c>object?</c>) tool parameter whose value must have the shape of another parameter of the same tool,
/// named by its schema name: <c>balance_compare</c>'s <c>variant</c> is a whole build, exactly like its <c>baseline</c>;
/// <c>balance_simulate</c>'s <c>enemies</c> is a list of entries exactly like its <c>party</c> (the guard then checks it
/// item by item). A parameter whose shape no sibling has uses <see cref="CheckedAsAttribute"/> instead.
///
/// <para>
/// <b>Why not just type it.</b> The SDK writes every typed parameter's full schema into the tool's input schema. A build's
/// schema is about 12 KB (its attack and modifier fields, each described for the model), and a second copy made
/// <c>balance_compare</c>'s definition 38 KB, some 10,000 tokens a session pays whenever the tool is loaded, to tell the
/// model nothing the baseline's schema beside it does not. Published untyped, with a description saying "the same fields
/// as baseline", it costs a line.
/// </para>
/// <para>
/// <b>What keeps it checked.</b> <see cref="ToolArgumentGuard"/> validates a marked argument exactly as it validates the
/// named parameter (unknown fields refused, field types, integer ranges, then a test-deserialize as that parameter's CLR
/// type), so a mistake inside it gets the same message it would get in the baseline. Without the guard's check an untyped
/// object would bind whatever it held and a misspelt field would be silently ignored. The tool then deserializes the
/// value with <c>McpJson.Options</c>, exactly as the SDK would have bound the typed parameter.
/// </para>
/// </summary>
/// <param name="parameter">The schema name of the parameter whose shape this one has, e.g. "baseline".</param>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
internal sealed class SameShapeAsAttribute(string parameter) : Attribute
{
    public string Parameter { get; } = parameter;
}
