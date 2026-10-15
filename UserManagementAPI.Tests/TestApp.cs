using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UserManagementAPI.Tests;

internal static class TestApp
{
    public const string Token = "test-token";

    public static WebApplicationFactory<Program> Create(
        Action<IServiceCollection>? services = null, string? token = Token) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection([new("Auth:Tokens:0", token ?? "")]));
            if (services is not null) b.ConfigureTestServices(services);
        });
}

internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);

    public IEnumerable<string> Messages(string categorySuffix) =>
        Entries.Where(e => e.Category.EndsWith(categorySuffix)).Select(e => e.Message);

    public void Dispose() { }

    private sealed class CaptureLogger(string category,
        ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt) =>
            sink.Enqueue((category, level, fmt(state, ex)));
    }
}
