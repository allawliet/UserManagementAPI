using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UserManagementAPI.Models;
using UserManagementAPI.Services;

namespace UserManagementAPI.Tests;

public class MiddlewareTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f, string? token = TestApp.Token)
    {
        var c = f.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private sealed class ThrowingStore : UserStore
    {
        public override User? Get(int id) => throw new InvalidOperationException("secret internal detail");
    }

    // ---- authentication ----

    [Fact]
    public async Task Valid_token_is_allowed()
    {
        using var f = TestApp.Create();
        Assert.Equal(HttpStatusCode.OK, (await Client(f).GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Missing_token_returns_401_json_with_challenge_header()
    {
        using var f = TestApp.Create();
        var r = await Client(f, token: null).GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bearer", r.Headers.WwwAuthenticate.ToString());
        Assert.Equal("Invalid or missing token.", (await Json(r)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("Bearer", "wrong-token")]
    [InlineData("Bearer", "test-toke")]
    [InlineData("Bearer", "TEST-TOKEN")]
    [InlineData("Basic", "test-token")]
    public async Task Invalid_token_or_scheme_returns_401(string scheme, string value)
    {
        using var f = TestApp.Create();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/users")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/users/1")]
    [InlineData("POST", "/api/users")]
    [InlineData("PUT", "/api/users/1")]
    [InlineData("DELETE", "/api/users/1")]
    [InlineData("GET", "/api/does-not-exist")]
    public async Task Every_protected_route_requires_a_token(string method, string path)
    {
        using var f = TestApp.Create();
        var r = await Client(f, token: null).SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Status_page_stays_public()
    {
        using var f = TestApp.Create();
        Assert.Equal(HttpStatusCode.OK, (await Client(f, token: null).GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task No_configured_tokens_fails_closed_even_for_an_empty_bearer()
    {
        using var f = TestApp.Create(token: "");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, token: "anything").GetAsync("/api/users")).StatusCode);

        var c = f.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/users")).StatusCode);
    }

    // ---- error handling ----

    [Fact]
    public async Task Unhandled_exception_returns_consistent_500_json_without_leaking_details()
    {
        using var f = TestApp.Create(s => s.AddSingleton<UserStore>(new ThrowingStore()));
        var r = await Client(f).GetAsync("/api/users/1");
        var text = await r.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Internal server error.", JsonDocument.Parse(text).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("secret internal detail", text);
        Assert.DoesNotContain("   at ", text);
    }

    [Fact]
    public async Task Auth_is_checked_before_the_endpoint_runs()
    {
        using var f = TestApp.Create(s => s.AddSingleton<UserStore>(new ThrowingStore()));
        var r = await Client(f, token: null).GetAsync("/api/users/1");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task All_error_statuses_share_the_same_json_shape()
    {
        using var f = TestApp.Create();
        var c = Client(f);
        var responses = new[]
        {
            await c.GetAsync("/api/users/999"),                                           // 404
            await c.PostAsJsonAsync("/api/users", new { firstName = "" }),               // 400 validation
            await c.GetAsync("/api/users?page=0"),                                        // 400 paging
            await c.PostAsync("/api/users", new StringContent("{bad", System.Text.Encoding.UTF8, "application/json")), // 400 body
            await Client(f, token: null).GetAsync("/api/users"),                          // 401
        };
        await c.PostAsJsonAsync("/api/users", new { firstName = "A", lastName = "B", email = "x@b.co", department = "IT" });
        responses = [.. responses, await c.PostAsJsonAsync("/api/users",                  // 409
            new { firstName = "A", lastName = "B", email = "x@b.co", department = "IT" })];

        foreach (var r in responses)
        {
            Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
            var body = await Json(r);
            Assert.Equal((int)r.StatusCode, body.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        }
    }

    // ---- logging ----

    private static (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, LogCapture Logs) WithLogs(
        Action<IServiceCollection>? more = null)
    {
        var logs = new LogCapture();
        var f = TestApp.Create(s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            more?.Invoke(s);
        });
        return (f, logs);
    }

    [Fact]
    public async Task Logs_method_path_and_status_for_success_and_client_errors()
    {
        var (f, logs) = WithLogs();
        using var _ = f;
        var c = Client(f);

        await c.GetAsync("/api/users");
        await c.GetAsync("/api/users/999");
        await c.PostAsJsonAsync("/api/users", new { firstName = "" });

        var lines = logs.Messages("RequestLoggingMiddleware").ToList();
        Assert.Contains(lines, l => l.StartsWith("HTTP GET /api/users responded 200 in "));
        Assert.Contains(lines, l => l.StartsWith("HTTP GET /api/users/999 responded 404 in "));
        Assert.Contains(lines, l => l.StartsWith("HTTP POST /api/users responded 400 in "));
        Assert.Contains(logs.Entries, e => e.Message.Contains("404") && e.Level == LogLevel.Warning);
        Assert.Contains(logs.Entries, e => e.Message.Contains("responded 200") && e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task Logs_500_for_unhandled_exceptions_and_the_exception_itself()
    {
        var (f, logs) = WithLogs(s => s.AddSingleton<UserStore>(new ThrowingStore()));
        using var _ = f;
        await Client(f).GetAsync("/api/users/1");

        Assert.Contains(logs.Entries, e => e.Category.EndsWith("RequestLoggingMiddleware")
            && e.Level == LogLevel.Error && e.Message.StartsWith("HTTP GET /api/users/1 responded 500 in "));
        Assert.Contains(logs.Entries, e => e.Category.EndsWith("ErrorHandlingMiddleware")
            && e.Message.StartsWith("Unhandled exception on GET /api/users/1"));
    }

    [Fact]
    public async Task Rejected_requests_are_still_audited_and_the_token_is_never_logged()
    {
        var (f, logs) = WithLogs();
        using var _ = f;
        await Client(f, token: null).GetAsync("/api/users");
        await Client(f, token: "super-secret-wrong-token").GetAsync("/api/users");
        await Client(f).GetAsync("/api/users");

        var rejections = logs.Messages("TokenAuthenticationMiddleware").Where(m => m.Contains("responded 401")).ToList();
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, m => Assert.StartsWith("HTTP GET /api/users responded 401", m));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("super-secret-wrong-token") || e.Message.Contains(TestApp.Token));
    }

    [Fact]
    public async Task Encoded_newlines_in_the_path_never_produce_multi_line_log_entries()
    {
        var (f, logs) = WithLogs();
        using var _ = f;
        await Client(f).GetAsync("/api/users/1%0AINFO%20forged%20entry");

        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains('\n') || e.Message.Contains('\r'));
        Assert.Contains(logs.Messages("RequestLoggingMiddleware"), l => l.Contains("responded 404"));
    }

    [Theory]
    [InlineData("/ok/path", "/ok/path")]
    [InlineData("/a\nINFO forged", "/a_INFO forged")]
    [InlineData("/a\r\nb\tc", "/a__b_c")]
    [InlineData("/a\u2028INFO forged", "/a_INFO forged")]
    [InlineData("/a\u2029b", "/a_b")]
    [InlineData("/a\u202Eb", "/a_b")]
    [InlineData("/a\u200Bb", "/a_b")]
    [InlineData("/münchen", "/münchen")]
    [InlineData(null, "")]
    public void LogSanitizer_replaces_control_characters(string? input, string expected) =>
        Assert.Equal(expected, UserManagementAPI.Middleware.LogSanitizer.Clean(input));

    [Fact]
    public void LogSanitizer_truncates_very_long_values() =>
        Assert.Equal(256, UserManagementAPI.Middleware.LogSanitizer.Clean(new string('a', 5000)).Length);
}
