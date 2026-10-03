using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DndMcp.Cli;

/// <summary>
/// Wraps a logger provider so the generic host's "Hosting failed to start" entry is dropped when, and only when, the
/// failure is a <see cref="StartupRefusedException"/>: a refusal whose service has already logged the reason, and which
/// <see cref="DndMcpCli.RunServerAsync"/> then summarises in one line. Every other entry passes through untouched.
///
/// <para>
/// <b>Why.</b> Without it a SQLite library missing FTS5 ends the process with the check's own error line, then the host's
/// multi-line repeat of the same exception with a stack trace and build paths, then the one-line summary: three reports
/// of one fact, the middle one reading like a crash (contract §9: "a failed start … exit code 1 with a one-line stderr
/// message"). Logging filters cannot do this: they see the category and level, never the event id or the exception.
/// </para>
/// <para>
/// <b>What breaks if it is widened.</b> Any other exception thrown from a hosted service's start (a bug in the index
/// warm-up, a transport failure) keeps the host's entry, because its stack trace is the only clue to it; so does every
/// other host event, including a background service's fault. The match is the host's category, the event id
/// <see cref="HostedServiceStartupFaulted"/> and the exception type, all three.
/// </para>
/// </summary>
internal sealed class ReportedStartFailureLogFilter : ILoggerProvider, ISupportExternalScope
{
    /// <summary>The category the generic host logs its own lifecycle under.</summary>
    public const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>Microsoft.Extensions.Hosting's event id for "Hosting failed to start" (<c>LoggerEventIds.HostedServiceStartupFaulted</c>).</summary>
    public const int HostedServiceStartupFaulted = 11;

    private readonly ILoggerProvider _inner;
    private readonly bool _ownsInner;

    /// <param name="inner">The provider whose output is filtered.</param>
    /// <param name="ownsInner">Dispose <paramref name="inner"/> with this one: true when nothing else will (the wrapper built it).</param>
    public ReportedStartFailureLogFilter(ILoggerProvider inner, bool ownsInner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
    }

    /// <summary>The provider this one filters.</summary>
    public ILoggerProvider Inner => _inner;

    public ILogger CreateLogger(string categoryName)
    {
        var logger = _inner.CreateLogger(categoryName);
        return categoryName == HostCategory ? new HostLogger(logger) : logger;
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => (_inner as ISupportExternalScope)?.SetScopeProvider(scopeProvider);

    // Whoever owns the console provider must dispose it: that flushes its queue, which RunServerAsync relies on to print
    // its summary last.
    public void Dispose()
    {
        if (_ownsInner)
        {
            _inner.Dispose();
        }
    }

    /// <summary>A start failure already reported by the service that refused: the refusal itself, or all of an aggregate.</summary>
    internal static bool IsReported(Exception? exception) => exception switch
    {
        StartupRefusedException => true,
        AggregateException { InnerExceptions.Count: > 0 } all => all.InnerExceptions.All(e => e is StartupRefusedException),
        _ => false,
    };

    private sealed class HostLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == HostedServiceStartupFaulted && IsReported(exception))
            {
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>Registers <see cref="ReportedStartFailureLogFilter"/> around the logger providers.</summary>
internal static class ReportedStartFailureLogging
{
    /// <summary>
    /// Wraps every logger provider registered so far (call it after the providers are added) in a
    /// <see cref="ReportedStartFailureLogFilter"/>. A provider registered by type is still built and disposed by the
    /// container (registered as itself, so the container picks its constructor as before); one registered by factory is
    /// built by the wrapper and disposed with it; one registered as an instance stays the caller's to dispose, as it was.
    /// </summary>
    public static ILoggingBuilder DropReportedStartFailures(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        var providers = logging.Services.Where(d => d.ServiceType == typeof(ILoggerProvider) && !d.IsKeyedService).ToList();
        foreach (var descriptor in providers)
        {
            logging.Services.Remove(descriptor);
            if (descriptor.ImplementationType is { } type)
            {
                logging.Services.Add(ServiceDescriptor.Singleton(type, type));
            }

            logging.Services.Add(ServiceDescriptor.Singleton<ILoggerProvider>(services => descriptor switch
            {
                { ImplementationInstance: ILoggerProvider instance } => new ReportedStartFailureLogFilter(instance, ownsInner: false),
                { ImplementationFactory: { } factory } => new ReportedStartFailureLogFilter((ILoggerProvider)factory(services), ownsInner: true),
                _ => new ReportedStartFailureLogFilter((ILoggerProvider)services.GetRequiredService(descriptor.ImplementationType!), ownsInner: false),
            }));
        }

        return logging;
    }
}
