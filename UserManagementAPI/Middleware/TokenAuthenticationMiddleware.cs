using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UserManagementAPI.Middleware;

public class AuthOptions
{
    public string[] Tokens { get; set; } = [];
}

public class TokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenAuthenticationMiddleware> _logger;
    private readonly byte[][] _tokenHashes;
    private readonly bool _isDevelopment;

    public TokenAuthenticationMiddleware(RequestDelegate next, IOptions<AuthOptions> options,
        IHostEnvironment environment, ILogger<TokenAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();
        // Trimmed like the presented header value, so stray spaces in configuration cannot lock everyone out.
        _tokenHashes = options.Value.Tokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => SHA256.HashData(Encoding.UTF8.GetBytes(t.Trim())))
            .ToArray();

        if (_tokenHashes.Length == 0)
            _logger.LogWarning("No API tokens configured (Auth:Tokens). Every protected request will be rejected.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPublic(context.Request))
        {
            await _next(context);
            return;
        }

        var token = ReadBearerToken(context.Request);
        if (token is null || !IsValid(token))
        {
            _logger.LogWarning("HTTP {Method} {Path} responded 401 ({Reason})",
                context.Request.Method, LogSanitizer.Clean(context.Request.Path),
                token is null ? "missing or malformed Authorization header" : "invalid token");
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorResponse.WriteAsync(context, StatusCodes.Status401Unauthorized, "Invalid or missing token.");
            return;
        }

        await _next(context);
    }

    // Only reads are public: the status page everywhere, the OpenAPI document in Development.
    private bool IsPublic(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.Path == "/" || (_isDevelopment && request.Path.StartsWithSegments("/openapi")));

    private static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private bool IsValid(string token)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var match = false;
        foreach (var known in _tokenHashes)
            match |= CryptographicOperations.FixedTimeEquals(presented, known);
        return match;
    }
}
