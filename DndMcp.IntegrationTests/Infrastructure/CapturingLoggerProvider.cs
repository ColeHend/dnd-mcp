using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>One log entry the in-memory server wrote.</summary>
public sealed record CapturedLogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

/// <summary>
/// The in-memory server's log, kept so a failing assertion can show the exception behind a generic
/// "An error occurred invoking '&lt;tool&gt;'." — the SDK sends the model no detail for unexpected exceptions,
/// so without this the only clue to why a happy-path test failed is lost.
/// </summary>
public sealed class CapturedLog
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public void Add(CapturedLogEntry entry) => _entries.Enqueue(entry);

    /// <summary>Warnings and errors (with exceptions) formatted for an assertion message; empty when there are none.</summary>
    public string Describe()
    {
        var problems = Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        if (problems.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder().AppendLine().AppendLine("Server log (warnings and errors):");
        foreach (var entry in problems)
        {
            text.AppendLine($"  [{entry.Level}] {entry.Category}: {entry.Message}");
            if (entry.Exception is not null)
            {
                text.AppendLine($"    {entry.Exception.GetType().FullName}: {entry.Exception.Message}");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Hand-written logger provider (no mocking library, per the repo's test conventions) that records every entry
/// into a <see cref="CapturedLog"/>. It never writes to the console: these tests run in-process with the test
/// runner, and the stdout-purity guarantee is checked separately against the real binary.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly CapturedLog _log;

    public CapturingLoggerProvider(CapturedLog log)
    {
        _log = log;
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _log);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly CapturedLog _log;

        public CapturingLogger(string category, CapturedLog log)
        {
            _category = category;
            _log = log;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _log.Add(new CapturedLogEntry(logLevel, _category, formatter(state, exception), exception));
        }
    }
}
