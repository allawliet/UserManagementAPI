using System.Diagnostics;

namespace UserManagementAPI.Middleware;

public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var timer = Stopwatch.StartNew();
        var status = StatusCodes.Status500InternalServerError;
        var aborted = false;
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        catch (Exception ex)
        {
            status = ex switch
            {
                BadHttpRequestException bad => bad.StatusCode,
                OperationCanceledException when context.RequestAborted.IsCancellationRequested => 499,
                _ => StatusCodes.Status500InternalServerError
            };
            // Once headers are sent the client keeps that status, so log it instead of a 500 it never saw.
            if (status == StatusCodes.Status500InternalServerError && context.Response.HasStarted)
            {
                status = context.Response.StatusCode;
                aborted = true;
            }
            throw;
        }
        finally
        {
            var level = aborted || status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms{Note}",
                LogSanitizer.Clean(context.Request.Method), LogSanitizer.Clean(context.Request.Path), status,
                timer.ElapsedMilliseconds, aborted ? " (aborted after the response started)" : "");
        }
    }
}
