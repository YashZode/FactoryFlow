using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace FactoryFlow.PlcSimulator;

internal sealed class SimulatorTelemetry() : TelemetryContextBase(
    Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new ConsoleLogProvider())))
{
    private sealed class ConsoleLogProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLog(categoryName);
        public void Dispose() { }
    }

    private sealed class ConsoleLog(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= (Environment.GetEnvironmentVariable("FACTORYFLOW_VERBOSE") == "1" ? LogLevel.Trace : LogLevel.Warning);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.Error.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}");
            if (exception is not null) Console.Error.WriteLine(exception);
        }
    }
}


