using System.ComponentModel.DataAnnotations;
using Microsoft.OpenApi;
using UserManagementAPI.Middleware;
using UserManagementAPI.Models;
using UserManagementAPI.Services;

const int DefaultPageSize = 20;
const int MaxPageSize = 100;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "TechHive User Management API";
        document.Info.Description = "CRUD API for TechHive Solutions' HR and IT teams. Every /api route needs an Authorization: Bearer <token> header.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "Shared API token, sent as 'Authorization: Bearer <token>'."
            }
        };
        return Task.CompletedTask;
    });
    o.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true)
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }
            ];
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton<UserStore>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["error"] = ctx.ProblemDetails.Title ?? "Error");
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);

var app = builder.Build();

// Order matters: errors first so they catch everything below, then authentication,
// then logging (which only sees requests that passed authentication).
app.UseMiddleware<ErrorHandlingMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseMiddleware<TokenAuthenticationMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

static IDictionary<string, string[]>? Validate(UserRequest request)
{
    var results = new List<ValidationResult>();
    Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
    return results.Count == 0
        ? null
        : results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(""), (r, m) => (Member: ToCamelCase(m), r.ErrorMessage))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
}

static string ToCamelCase(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

static IResult DuplicateEmail() =>
    Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A user with this email already exists.");

app.MapGet("/", () => Results.Ok(new
{
    name = "UserManagementAPI",
    status = "running",
    users = "/api/users (requires Authorization: Bearer <token>)",
    openApi = "/openapi/v1.json"
}))
    .WithSummary("Service status (public)")
    .WithTags("Status");

var users = app.MapGroup("/api/users")
    .WithTags("Users")
    .ProducesProblem(StatusCodes.Status401Unauthorized);

users.MapGet("/", (int? page, int? pageSize, UserStore store, HttpContext http) =>
{
    var p = page ?? 1;
    var size = pageSize ?? DefaultPageSize;
    var errors = new Dictionary<string, string[]>();
    if (p < 1) errors["page"] = ["page must be 1 or greater."];
    if (size is < 1 or > MaxPageSize) errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var (items, total) = store.GetPage(p, size);
    http.Response.Headers["X-Total-Count"] = total.ToString();
    return Results.Ok(items);
})
    .WithSummary("List users (paged)")
    .WithDescription("Returns one page of users ordered by id. The total number of users is in the X-Total-Count response header.")
    .Produces<IReadOnlyList<User>>(StatusCodes.Status200OK)
    .ProducesValidationProblem();

users.MapGet("/{id:int}", (int id, UserStore store) =>
    store.Get(id) is { } user ? Results.Ok(user) : Results.NotFound())
    .WithSummary("Get a user by id")
    .Produces<User>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status404NotFound);

users.MapPost("/", (UserRequest request, UserStore store) =>
{
    request.Normalize();
    if (Validate(request) is { } errors) return Results.ValidationProblem(errors);

    var result = store.Add(request);
    return result.Status == StoreStatus.DuplicateEmail
        ? DuplicateEmail()
        : Results.Created($"/api/users/{result.User!.Id}", result.User);
})
    .WithSummary("Create a user")
    .Produces<User>(StatusCodes.Status201Created)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

users.MapPut("/{id:int}", (int id, UserRequest request, UserStore store) =>
{
    request.Normalize();
    if (Validate(request) is { } errors) return Results.ValidationProblem(errors);

    var result = store.Update(id, request);
    return result.Status switch
    {
        StoreStatus.NotFound => Results.NotFound(),
        StoreStatus.DuplicateEmail => DuplicateEmail(),
        _ => Results.Ok(result.User)
    };
})
    .WithSummary("Replace a user")
    .Produces<User>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

users.MapDelete("/{id:int}", (int id, UserStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound())
    .WithSummary("Delete a user")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound);

app.Run();

public partial class Program;
