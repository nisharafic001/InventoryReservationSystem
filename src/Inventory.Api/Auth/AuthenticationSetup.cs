using Inventory.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.Api.Auth;

public static class AuthenticationSetup
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from validated JwtOptions at runtime, so tests and environments can supply their own settings.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                // Keep claim names as issued ("sub", "username", "role") instead of legacy XML claim URIs.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = JwtTokenGenerator.CreateSigningKey(jwt),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtTokenGenerator.UsernameClaim,
                    RoleClaimType = JwtTokenGenerator.RoleClaim
                };
            });

        services.AddAuthorizationBuilder()
            // Secure by default: every endpoint requires a valid token unless it opts out with [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
                policy.RequireAuthenticatedUser().RequireRole(AuthorizationPolicies.AdminRole));

        return services;
    }
}
