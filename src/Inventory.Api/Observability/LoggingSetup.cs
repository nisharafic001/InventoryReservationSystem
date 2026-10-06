using Microsoft.IdentityModel.JsonWebTokens;
using Serilog;
using Serilog.Events;

namespace Inventory.Api.Observability;

public static class LoggingSetup
{
    /// <summary>
    /// Serilog configured from the <c>Serilog</c> section. Instance-based (not the static <c>Log.Logger</c>), so each
    /// host — including test hosts — gets its own pipeline; extra sinks registered in DI are picked up too.
    /// </summary>
    public static IServiceCollection AddStructuredLogging(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSerilog(
            (serviceProvider, logger) => logger
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(serviceProvider)
                .Enrich.FromLogContext(),
            // Never touch the static Log.Logger: parallel hosts (e.g. tests) would write into each other's pipelines.
            preserveStaticLogger: true);

    /// <summary>
    /// One structured event per request: CorrelationId, RequestMethod, RequestPath, StatusCode, Elapsed (ms),
    /// plus the authenticated UserId and ProductId/ReservationId from the route when present.
    /// Headers, query strings and bodies are never logged — so no JWTs, Authorization headers or passwords.
    /// </summary>
    public static IApplicationBuilder UseStructuredRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            // This host's logger, not the static Log.Logger (see AddStructuredLogging).
            options.Logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();

            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";

            options.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= 500 ? LogEventLevel.Error
                : httpContext.Response.StatusCode >= 400 ? LogEventLevel.Warning
                : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnostics, httpContext) =>
            {
                diagnostics.Set(CorrelationIdMiddleware.LogPropertyName, httpContext.TraceIdentifier);

                var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                if (userId is not null)
                    diagnostics.Set("UserId", userId);

                var routeValues = httpContext.Request.RouteValues;
                if (routeValues.TryGetValue("id", out var id) && id is not null
                    && routeValues.TryGetValue("controller", out var controller))
                {
                    if (string.Equals(controller as string, "Products", StringComparison.OrdinalIgnoreCase))
                        diagnostics.Set("ProductId", id);
                    else if (string.Equals(controller as string, "Reservations", StringComparison.OrdinalIgnoreCase))
                        diagnostics.Set("ReservationId", id);
                }
            };
        });
}
