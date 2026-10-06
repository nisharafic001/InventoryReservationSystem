using System.Text.RegularExpressions;
using Serilog.Context;

namespace Inventory.Api.Observability;

/// <summary>
/// Gives every request a correlation id: the caller's <c>X-Correlation-ID</c> if it is well-formed, otherwise a new one.
/// The id becomes <see cref="HttpContext.TraceIdentifier"/> (used in ProblemDetails), is pushed onto every log event
/// of the request as <c>CorrelationId</c>, and is returned in the response header.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogPropertyName = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();
        if (!IsSafe(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(LogPropertyName, correlationId))
            await next(context);
    }

    // Untrusted input ends up in logs and headers: allow only short, plain tokens (no CR/LF, quotes or spaces).
    private static bool IsSafe(string value) => value.Length is > 0 and <= 64 && SafePattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex SafePattern();
}
