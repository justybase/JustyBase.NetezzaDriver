using Microsoft.Extensions.Logging;

namespace JustyBase.NetezzaDriver.Tests;

internal static class ProtocolTraceTestLogger
{
    internal static ILoggerFactory? CreateIfEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("NZ_PROTOCOL_TRACE"),
            "1",
            StringComparison.Ordinal)
            ? new ConsoleFactory()
            : null;
    }

    private sealed class ConsoleFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("Backend response:", StringComparison.Ordinal)
                || message.Contains("Backend protocol length", StringComparison.Ordinal)
                || message.Contains("RowStandard descriptor", StringComparison.Ordinal)
                || message.Contains("RowStandard payload", StringComparison.Ordinal))
            {
                Console.WriteLine(message);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
