using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UserManagementAPI.Middleware;

namespace UserManagementAPI.Tests;

public class RequestLoggingEdgeCaseTests
{
    private static async Task<IHost> StartAsync(LogCapture logs, RequestDelegate endpoint) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureLogging(l => l.AddProvider(logs))
                .Configure(app =>
                {
                    app.UseMiddleware<ErrorHandlingMiddleware>();
                    app.UseMiddleware<RequestLoggingMiddleware>();
                    app.Run(endpoint);
                }))
            .StartAsync();

    [Fact]
    public async Task Exception_after_the_response_started_logs_the_committed_status_as_an_error()
    {
        var logs = new LogCapture();
        using var host = await StartAsync(logs, async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("partial");
            await ctx.Response.Body.FlushAsync();
            throw new InvalidOperationException("boom after headers");
        });

        try
        {
            using var response = await host.GetTestClient().GetAsync("/partial", HttpCompletionOption.ResponseHeadersRead);
            await response.Content.ReadAsStringAsync();
        }
        catch (Exception) { /* the aborted connection may surface as a client error */ }

        for (var i = 0; i < 50 && !logs.Entries.Any(e => e.Category.EndsWith("RequestLoggingMiddleware")); i++)
            await Task.Delay(20);

        var entry = Assert.Single(logs.Entries, e => e.Category.EndsWith("RequestLoggingMiddleware"));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("responded 200", entry.Message);
        Assert.Contains("aborted", entry.Message);
    }

    [Fact]
    public async Task Exception_before_the_response_starts_still_logs_500()
    {
        var logs = new LogCapture();
        using var host = await StartAsync(logs, _ => throw new InvalidOperationException("boom"));

        var response = await host.GetTestClient().GetAsync("/early");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = Assert.Single(logs.Entries, e => e.Category.EndsWith("RequestLoggingMiddleware"));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("responded 500", entry.Message);
        Assert.DoesNotContain("aborted", entry.Message);
    }

    [Fact]
    public async Task Normal_request_still_logs_method_path_and_status_without_a_note()
    {
        var logs = new LogCapture();
        using var host = await StartAsync(logs, ctx => Task.CompletedTask);

        await host.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod("GET"), "/ok"));

        Assert.Contains(logs.Messages("RequestLoggingMiddleware"), m => m.StartsWith("HTTP GET /ok responded 200"));
    }
}
