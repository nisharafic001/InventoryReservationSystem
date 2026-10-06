using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Inventory.Api.RateLimiting;

public static class RateLimitingSetup
{
    public const string LoginPolicy = "login";
    public const string ReservationsPolicy = "reservations";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingSettings>()
            .Bind(configuration.GetSection(RateLimitingSettings.SectionName))
            .Validate(s => s.IsValid(), "RateLimiting limits, windows and periods must all be positive.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });

        // Built from the validated settings at runtime, so each environment (and test host) can tune them.
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingSettings>>((options, settingsOptions) =>
            {
                var settings = settingsOptions.Value;
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = OnRejectedAsync;

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.General.PermitLimit,
                        Window = settings.General.Window,
                        QueueLimit = 0
                    }));

                options.AddPolicy(LoginPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(IpKey(context), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.Login.PermitLimit,
                        Window = settings.Login.Window,
                        QueueLimit = 0
                    }));

                options.AddPolicy(ReservationsPolicy, context =>
                    RateLimitPartition.GetTokenBucketLimiter(ClientKey(context), _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = settings.Reservations.TokenLimit,
                        TokensPerPeriod = settings.Reservations.TokensPerPeriod,
                        ReplenishmentPeriod = settings.Reservations.ReplenishmentPeriod,
                        AutoReplenishment = true,
                        QueueLimit = 0
                    }));
            });

        return services;
    }

    /// <summary>Authenticated callers are limited per user; anonymous ones per IP.</summary>
    private static string ClientKey(HttpContext context) =>
        context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is { } userId ? $"user:{userId}" : IpKey(context);

    // Behind a reverse proxy, configure ForwardedHeaders so RemoteIpAddress is the real client.
    private static string IpKey(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingSetup))
            .LogWarning("Rate limit exceeded for {RequestMethod} {RequestPath}.", httpContext.Request.Method, httpContext.Request.Path);

        // Same ProblemDetails shape as every other error (errorCode RATE_LIMITED, correlationId, ...).
        await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = StatusCodes.Status429TooManyRequests }
        });
    }
}
