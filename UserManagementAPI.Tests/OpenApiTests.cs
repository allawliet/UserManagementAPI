using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace UserManagementAPI.Tests;

public class OpenApiTests
{
    private static async Task<JsonElement> LoadDocument()
    {
        using var f = TestApp.Create().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        return await f.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");
    }

    [Theory]
    [InlineData("get", "/api/users", new[] { "200", "400", "401" })]
    [InlineData("post", "/api/users", new[] { "201", "400", "401", "409", "413" })]
    [InlineData("get", "/api/users/{id}", new[] { "200", "401", "404" })]
    [InlineData("put", "/api/users/{id}", new[] { "200", "400", "401", "404", "409", "413" })]
    [InlineData("delete", "/api/users/{id}", new[] { "204", "401", "404" })]
    public async Task Operations_document_their_real_status_codes_summary_and_bearer_requirement(
        string method, string path, string[] statuses)
    {
        var op = (await LoadDocument()).GetProperty("paths").GetProperty(path).GetProperty(method);

        Assert.False(string.IsNullOrWhiteSpace(op.GetProperty("summary").GetString()));
        Assert.Equal(statuses.Order(), op.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        Assert.True(op.GetProperty("security")[0].TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task Bearer_scheme_is_declared_and_the_public_status_route_requires_none()
    {
        var doc = await LoadDocument();

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.False(doc.GetProperty("paths").GetProperty("/").GetProperty("get").TryGetProperty("security", out _));
    }
}
