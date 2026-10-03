using Microsoft.Extensions.Logging;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps what was logged, for tests that assert a failure was reported as a warning
/// instead of thrown (a failed daily backup must never block the write it protects). Thread-safe.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public IReadOnlyList<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
