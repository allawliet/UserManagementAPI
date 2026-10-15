using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UserManagementAPI.Tests;

public class AuthHardeningTests
{
    private static WebApplicationFactory<Program> InEnvironment(string environment, string? token = TestApp.Token) =>
        TestApp.Create(token: token).WithWebHostBuilder(b => b.UseEnvironment(environment));

    [Fact]
    public async Task Development_serves_the_openapi_document_without_a_token()
    {
        using var f = InEnvironment("Development");
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/openapi/v1.json")]
    [InlineData("DELETE", "/openapi/v1.json")]
    [InlineData("POST", "/")]
    [InlineData("PUT", "/")]
    public async Task Public_exemption_covers_reads_only(string method, string path)
    {
        using var f = InEnvironment("Development");
        var r = await f.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Outside_development_the_openapi_prefix_is_not_exempt()
    {
        using var f = InEnvironment("Production");
        var client = f.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Configured_token_with_surrounding_whitespace_still_matches_the_trimmed_header()
    {
        using var f = TestApp.Create(token: "  spaced-token  ");
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", "spaced-token");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Whitespace_only_configured_token_still_fails_closed()
    {
        using var f = TestApp.Create(token: "   ");
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", "anything");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/users")).StatusCode);
    }
}
