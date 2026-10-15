using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UserManagementAPI.Tests;

public class UserApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = TestApp.Create();
    private readonly HttpClient _client;

    public UserApiTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", TestApp.Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object Valid(string email = "new@techhive.example") =>
        new { firstName = "Alan", lastName = "Turing", email, department = "IT" };

    private Task<HttpResponseMessage> PostRaw(string body, string contentType = "application/json") =>
        _client.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, contentType));

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Crud_round_trip()
    {
        var created = await _client.PostAsJsonAsync("/api/users", Valid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetInt32();
        Assert.Equal($"/api/users/{id}", created.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/users/{id}")).StatusCode);

        var put = await _client.PutAsJsonAsync($"/api/users/{id}",
            new { firstName = "Alan", lastName = "Turing", email = "new@techhive.example", department = "Research" });
        Assert.Equal("Research", (await Json(put)).GetProperty("department").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/users/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/users/999")]
    [InlineData("/api/users/0")]
    [InlineData("/api/users/-1")]
    [InlineData("/api/users/abc")]
    public async Task Get_nonexistent_or_malformed_id_returns_404(string url)
    {
        var r = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Put_and_delete_nonexistent_id_return_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/users/999", Valid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/users/999")).StatusCode);
    }

    [Fact]
    public async Task Put_validates_the_body_before_looking_up_the_id()
    {
        var invalid = await _client.PutAsJsonAsync("/api/users/999", new { firstName = "", lastName = "B", email = "a@b.co", department = "IT" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/users/999", Valid())).StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_to_unknown_route_returns_404_json()
    {
        var r = await _client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("Not Found", (await Json(r)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{"firstName":"","lastName":"B","email":"a@b.co","department":"IT"}""", "firstName")]
    [InlineData("""{"firstName":"   ","lastName":"B","email":"a@b.co","department":"IT"}""", "firstName")]
    [InlineData("""{"lastName":"B","email":"a@b.co","department":"IT"}""", "firstName")]
    [InlineData("""{"firstName":"A","lastName":"B","email":"not-an-email","department":"IT"}""", "email")]
    [InlineData("""{"firstName":"A","lastName":"B","email":"a@b","department":"IT"}""", "email")]
    [InlineData("""{"firstName":"A","lastName":"B","email":"a@b.","department":"IT"}""", "email")]
    [InlineData("""{"firstName":"A","lastName":"B","email":"Bob <a@b.co>","department":"IT"}""", "email")]
    [InlineData("""{"firstName":"A","lastName":"B","email":"a@b.co","department":""}""", "department")]
    public async Task Invalid_input_returns_400_with_camelCase_field_errors(string body, string field)
    {
        var r = await PostRaw(body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await Json(r)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Too_long_field_returns_400()
    {
        var r = await PostRaw($$"""{"firstName":"{{new string('A', 51)}}","lastName":"B","email":"a@b.co","department":"IT"}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_before_validation_and_storage()
    {
        var r = await PostRaw("""{"firstName":"  Alan ","lastName":"Turing","email":" padded@techhive.example ","department":"IT"}""");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("Alan", body.GetProperty("firstName").GetString());
        Assert.Equal("padded@techhive.example", body.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{bad json")]
    [InlineData("""{"firstName":123,"lastName":"B","email":"a@b.co","department":"IT"}""")]
    public async Task Missing_or_malformed_body_returns_400_problem_json_without_stack_trace(string body)
    {
        var r = await PostRaw(body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("   at ", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_content_type_returns_415()
    {
        var r = await PostRaw("hello", "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        var r = await PostRaw($$"""{"firstName":"{{new string('A', 100_000)}}"}""");
        Assert.True((int)r.StatusCode is 400 or 413);
    }

    [Fact]
    public async Task Duplicate_email_is_case_insensitive_409_on_post_and_put()
    {
        await _client.PostAsJsonAsync("/api/users", Valid("dup@techhive.example"));
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/users", Valid("DUP@techhive.example"))).StatusCode);

        var other = await _client.PostAsJsonAsync("/api/users", Valid("other@techhive.example"));
        var id = (await Json(other)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PutAsJsonAsync($"/api/users/{id}", Valid("dup@techhive.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/api/users/{id}", Valid("other@techhive.example"))).StatusCode);
    }

    [Fact]
    public async Task Deleted_users_email_can_be_reused_and_changed_email_is_released()
    {
        var a = await Json(await _client.PostAsJsonAsync("/api/users", Valid("a@techhive.example")));
        var idA = a.GetProperty("id").GetInt32();
        await _client.PutAsJsonAsync($"/api/users/{idA}", Valid("a2@techhive.example"));
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/users", Valid("a@techhive.example"))).StatusCode);

        await _client.DeleteAsync($"/api/users/{idA}");
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/users", Valid("a2@techhive.example"))).StatusCode);
    }

    [Fact]
    public async Task Concurrent_posts_with_same_email_create_exactly_one_user()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _client.PostAsJsonAsync("/api/users", Valid("race@techhive.example")))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(49, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Get_list_is_paged_ordered_and_reports_total()
    {
        for (var i = 0; i < 25; i++)
            await _client.PostAsJsonAsync("/api/users", Valid($"u{i}@techhive.example"));

        var first = await _client.GetAsync("/api/users");
        Assert.Equal(20, (await Json(first)).GetArrayLength());
        Assert.Equal("27", first.Headers.GetValues("X-Total-Count").Single());

        var last = await Json(await _client.GetAsync("/api/users?page=2&pageSize=20"));
        Assert.Equal(7, last.GetArrayLength());
        var ids = last.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);

        Assert.Equal(0, (await Json(await _client.GetAsync("/api/users?page=99"))).GetArrayLength());
    }

    [Theory]
    [InlineData("page=2147483647")]
    [InlineData("page=2147483647&pageSize=100")]
    public async Task Huge_page_number_returns_empty_list_instead_of_wrapping_to_page_one(string query)
    {
        var r = await _client.GetAsync($"/api/users?{query}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0, (await Json(r)).GetArrayLength());
        Assert.Equal("2", r.Headers.GetValues("X-Total-Count").Single());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task Invalid_paging_returns_400(string query)
    {
        var r = await _client.GetAsync($"/api/users?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
