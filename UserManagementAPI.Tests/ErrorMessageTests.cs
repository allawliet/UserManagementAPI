using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using UserManagementAPI.Middleware;

namespace UserManagementAPI.Tests;

public class ErrorMessageTests
{
    private static async Task<string?> ErrorFor(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task Wrong_content_type_gets_the_standard_415_json_not_a_malformed_body_message()
    {
        using var f = TestApp.Create();
        using var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", TestApp.Token);

        var r = await c.PostAsync("/api/users", new StringContent("hello", Encoding.UTF8, "text/plain"));

        Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Unsupported Media Type", await ErrorFor(r));
    }

    [Theory]
    [InlineData(400, "The request body is missing or malformed.")]
    [InlineData(408, "The request timed out.")]
    [InlineData(413, "Request body is too large.")]
    [InlineData(414, "The request target is too long.")]
    [InlineData(431, "Request headers are too large.")]
    [InlineData(426, "The request could not be processed.")]
    public async Task Bad_http_request_statuses_get_a_matching_generic_message(int status, string expected)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseMiddleware<ErrorHandlingMiddleware>();
                    app.Run(_ => throw new BadHttpRequestException("internal detail", status));
                }))
            .StartAsync();

        var r = await host.GetTestClient().GetAsync("/x");

        Assert.Equal(status, (int)r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await ErrorFor(r));
        Assert.DoesNotContain("internal detail", await r.Content.ReadAsStringAsync());
    }
}
