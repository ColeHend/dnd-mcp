using System.Globalization;
using DndMcp.Hosting;
using DndMcp.Repository.Srd.Index;
using Microsoft.Data.Sqlite;

namespace DndMcp.Cli;

/// <summary>
/// <c>DndMcp srd-build [--force]</c>: builds srd.db at the path the server uses, or confirms the one there is current,
/// and prints what it found.
///
/// <para>
/// The server builds the index itself on first use, so this is never required. It exists to check an install before
/// registering it (a publish copied without its <c>content/</c> directory fails here, with the reason, instead of in the
/// middle of a session) and to pay the one-off build before Claude Code starts the server. It makes the same reuse
/// decision as the server (<see cref="SrdIndexOpener"/>), so "current" here means the server will reuse it too.
/// </para>
/// <para>
/// Unlike the server it does not fall back to a temporary directory when the cache is unwritable: the point is an
/// index at the configured path, so failing to write one is the answer, with exit code 1.
/// </para>
/// </summary>
internal static class SrdBuildCommand
{
    public const string Name = "srd-build";

    public static int Run(IReadOnlyList<string> options, DndMcpServerOptions serverOptions, TextWriter output, TextWriter error)
    {
        var force = false;
        foreach (var option in options)
        {
            if (option == "--force")
            {
                force = true;
                continue;
            }

            error.Write($"Unknown option '{option}' for {Name}.\n{DndMcpCli.Usage}");
            return DndMcpCli.ExitUsage;
        }

        foreach (var warning in serverOptions.PathWarnings())
        {
            error.WriteLine($"warning: {warning}");
        }

        string databasePath;
        try
        {
            databasePath = serverOptions.ResolveSrdDatabasePath();
        }
        catch (InvalidOperationException ex)
        {
            return Fail(error, ex.Message);
        }

        try
        {
            var result = SrdIndexOpener.OpenOrBuild(serverOptions.ContentRoot, databasePath, force);
            using var index = result.Index;
            WriteSummary(output, result, databasePath);
            foreach (var warning in result.Warnings)
            {
                error.WriteLine($"warning: {warning}");
            }

            return DndMcpCli.ExitOk;
        }
        catch (SrdIndexUnavailableException ex)
        {
            return Fail(error, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return Fail(error, $"cannot write {databasePath}: {ex.Message}");
        }
    }

    private static void WriteSummary(TextWriter output, SrdIndexOpenResult result, string databasePath)
    {
        var index = result.Index;
        var milliseconds = ((long)result.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        output.WriteLine(result.Rebuilt
            ? $"srd.db rebuilt in {milliseconds} ms ({result.Reason})."
            : $"srd.db is current; reused in {milliseconds} ms.");
        output.WriteLine($"  path:     {databasePath}");
        output.WriteLine($"  content:  {index.Info.ContentTag}, Rules Glossary sha256 {index.Info.GlossarySha256}");
        foreach (var edition in index.Counts().GroupBy(c => c.Edition))
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {edition.Key}:     {edition.Sum(c => c.Count)} documents"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  total:    {index.Info.DocumentCount} documents"));
    }

    private static int Fail(TextWriter error, string reason)
    {
        error.WriteLine($"{Name} failed: {reason}");
        return DndMcpCli.ExitFailed;
    }
}
