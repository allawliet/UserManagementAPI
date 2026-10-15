using Microsoft.AspNetCore.Mvc;

namespace UserManagementAPI.Middleware;

public static class ErrorResponse
{
    public static Task WriteAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        var body = new ProblemDetails { Status = status, Title = message };
        body.Extensions["error"] = message;
        body.Extensions["traceId"] = context.TraceIdentifier;
        return context.Response.WriteAsJsonAsync(body, options: null, contentType: "application/problem+json");
    }
}
