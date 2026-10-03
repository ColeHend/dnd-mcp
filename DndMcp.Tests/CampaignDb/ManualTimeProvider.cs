namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// A clock that moves only when a test moves it, so timestamps, backup names, the daily backup and 40 days of retention
/// are deterministic. Hand-written (no mocks, house rule); thread-safe because concurrency tests read it from several
/// threads. Starts at 2026-09-01 12:00:00 UTC unless given a start.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>The default start: noon UTC, so a few hours either way stays on the same UTC day.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly object _lock = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) => _now = (start ?? DefaultStart).ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock forward (or back, with a negative span).</summary>
    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now = _now.Add(by);
        }
    }

    /// <summary>Sets the clock.</summary>
    public void SetUtcNow(DateTimeOffset now)
    {
        lock (_lock)
        {
            _now = now.ToUniversalTime();
        }
    }
}
