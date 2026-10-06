using System.Text.Json;
using Inventory.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.ErrorHandling;

/// <summary>
/// Converts every exception that escapes an endpoint into a ProblemDetails response via <see cref="ErrorCatalog"/>.
/// Expected business failures are logged briefly; unexpected ones are logged with the full exception, server-side only.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
            logger.LogInformation("Request aborted by the client.");
            return true;
        }

        var error = ErrorCatalog.FromException(exception);
        if (exception is TemporarilyUnavailableException)
        {
            // Expected under heavy contention: nothing was changed and the client can simply retry.
            logger.LogWarning("Request rejected as {ErrorCode} after retries ({Reason}).", error.ErrorCode, exception.InnerException?.GetType().Name);
            httpContext.Response.Headers.RetryAfter = "1";
        }
        else if (error.Status >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception; returning {ErrorCode}.", error.ErrorCode);
        else
            logger.LogInformation("Request failed with {StatusCode} {ErrorCode}.", error.Status, error.ErrorCode);

        ProblemDetails problem = exception is ValidationException validation
            ? new ValidationProblemDetails(ToJsonFieldNames(validation.Errors))
            : new ProblemDetails();
        problem.Status = error.Status;
        problem.Title = error.Title;
        problem.Detail = error.Detail;
        problem.Extensions[ProblemDetailsSetup.ErrorCodeKey] = error.ErrorCode;

        httpContext.Response.StatusCode = error.Status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    // Report field names as they appear in the JSON contract (camelCase).
    private static Dictionary<string, string[]> ToJsonFieldNames(IReadOnlyDictionary<string, string[]> errors) =>
        errors.ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value);
}
