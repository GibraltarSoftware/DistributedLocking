using System;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Gibraltar.DistributedLocking.Test
{
    public class NUnitLogger<T> : ILogger<T>
    {
        public NUnitLogger()
        {
            LogLevel = LogLevel.Information;
        }

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel;

        public LogLevel LogLevel { get; set; }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var message = formatter(state, exception);
            TestContext.WriteLine($"{logLevel}: {message}");
        }
    }

    public static class NUnitLogger
    {
        public static ILogger<T> Create<T>()
        {
            return new NUnitLogger<T>();
        }
    }
}