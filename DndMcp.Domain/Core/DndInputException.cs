namespace DndMcp.Domain.Core;

/// <summary>
/// Input the caller can fix: a malformed dice expression, an unknown rules reference, a build that fails
/// validation. The message is shown to the model verbatim, so it must say what was wrong AND what would
/// be accepted instead — the model uses it to correct its next call.
///
/// <para>
/// This type exists because the MCP SDK only forwards the message of an <c>McpException</c>; every other
/// exception reaches the model as the bare "An error occurred invoking '&lt;tool&gt;'." with no detail.
/// Domain code cannot throw <c>McpException</c> without taking an MCP dependency (Domain has none, by
/// design), so it throws this and the host's call-tool filter translates it. An exception of any other
/// type is treated as a bug: logged to stderr and deliberately NOT described to the model.
/// </para>
/// <para>
/// Never put secrets or DM-only campaign text in the message — it goes to whoever is driving the client.
/// </para>
/// </summary>
public sealed class DndInputException : Exception
{
    public DndInputException(string message)
        : base(message)
    {
    }

    public DndInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
