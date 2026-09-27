using DndMcp.Formatting;
using ModelContextProtocol.Server;

namespace DndMcp.Resources;

/// <summary>
/// <c>rules://tables/&lt;slug&gt;</c>: one static resource per <see cref="RulesTables"/> entry.
///
/// <para>
/// Static resources, not one <c>rules://tables/{name}</c> template: a URI with a parameter moves from resources/list to
/// resources/templates/list, where Claude Code may not offer it as an <c>@dnd:</c> mention at all (ServerSurfaceTests pins
/// that the server has no templates). Built from the catalog rather than one attributed method per table, so a table
/// added there is served here with nothing else to remember; ServerSurfaceTests lists the URIs, so that change is still
/// a deliberate diff.
/// </para>
/// </summary>
internal static class RulesTableResources
{
    public static IEnumerable<McpServerResource> Create() =>
        RulesTables.All.Select(table => McpServerResource.Create(
            table.Render,
            new McpServerResourceCreateOptions
            {
                UriTemplate = table.Uri,
                Name = table.Slug,
                Title = table.Title,
                Description = $"{table.Description} Source: {table.Source}.",
                MimeType = "text/markdown",
            }));
}
