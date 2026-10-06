using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.ErrorHandling;

public static class ProblemDetailsSetup
{
    public const string ErrorCodeKey = "errorCode";
    public const string CorrelationIdKey = "correlationId";

    /// <summary>
    /// Every ProblemDetails the API writes — from the exception handler, MVC validation, status-code pages,
    /// authentication challenges or the rate limiter — gets the same shape:
    /// type, title, status, detail, instance, errorCode, correlationId.
    /// </summary>
    public static IServiceCollection AddConsistentProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            var defaults = ErrorCatalog.FromStatusCode(status);

            problem.Status = status;
            problem.Detail ??= defaults.Detail;
            // ASP.NET Core only fills "type" for some status codes (not e.g. 429); keep the shape uniform.
            problem.Type ??= status == StatusCodes.Status429TooManyRequests
                ? "https://tools.ietf.org/html/rfc6585#section-4"
                : "about:blank";
            problem.Instance = context.HttpContext.Request.Path; // path only: query strings may carry secrets

            if (!problem.Extensions.ContainsKey(ErrorCodeKey))
            {
                var isValidation = problem is ValidationProblemDetails or HttpValidationProblemDetails;
                problem.Extensions[ErrorCodeKey] = isValidation ? ErrorCatalog.ValidationFailed : defaults.ErrorCode;
                if (isValidation)
                    problem.Title = ErrorCatalog.ValidationTitle;
                else if (problem.Title is null || problem.Title == ReasonPhrase(status))
                    problem.Title = defaults.Title;
            }

            problem.Extensions[CorrelationIdKey] = context.HttpContext.TraceIdentifier;
            problem.Extensions.Remove("traceId"); // superseded by correlationId
        });

        // Malformed JSON: report "invalid input" without parser internals (positions, CLR type names).
        services.Configure<JsonOptions>(options => options.AllowInputFormatterExceptionMessages = false);

        return services;
    }

    private static string? ReasonPhrase(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
}
