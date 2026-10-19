using BC_LocalAI.Models;
using BC_LocalAI.Services;

namespace BC_LocalAI.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request was cancelled by the client.");
        }
        catch (AiServiceException exception) when (!context.Response.HasStarted)
        {
            // Provider failures were already logged (without prompt/response) by the service.
            context.Response.StatusCode = (int)exception.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new ApiErrorResponse(new ApiError(exception.Code, exception.Message)));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "An unhandled error occurred while processing the request.");

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new ApiErrorResponse(new ApiError(
                    "INTERNAL_ERROR",
                    "An unexpected error occurred")));
        }
    }
}
