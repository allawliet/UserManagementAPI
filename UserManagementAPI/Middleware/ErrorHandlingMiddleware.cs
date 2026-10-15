namespace UserManagementAPI.Middleware;

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nobody is left to answer.
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private static string MessageFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The request body is missing or malformed.",
        StatusCodes.Status408RequestTimeout => "The request timed out.",
        StatusCodes.Status413PayloadTooLarge => "Request body is too large.",
        StatusCodes.Status414UriTooLong => "The request target is too long.",
        StatusCodes.Status431RequestHeaderFieldsTooLarge => "Request headers are too large.",
        _ => "The request could not be processed."
    };

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var (status, message) = ex switch
        {
            BadHttpRequestException bad => (bad.StatusCode, MessageFor(bad.StatusCode)),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error.")
        };

        if (status >= 500)
            logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, LogSanitizer.Clean(context.Request.Path));

        if (context.Response.HasStarted)
        {
            logger.LogWarning("Response already started; aborting the connection.");
            context.Abort();
            return;
        }

        context.Response.Clear();
        await ErrorResponse.WriteAsync(context, status, message);
    }
}
