using Inventory.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Inventory.Api.OpenApi;

/// <summary>
/// Documents what the pipeline actually enforces, read from endpoint metadata so the docs cannot drift:
/// protected operations get the Bearer requirement (padlock) plus 401, admin-only ones also 403,
/// and rate-limited ones 429.
/// </summary>
public sealed class AuthorizationOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

        if (!metadata.OfType<DisableRateLimitingAttribute>().Any())
            AddProblemResponse(operation, StatusCodes.Status429TooManyRequests, "Rate limit exceeded (see `Retry-After`).");

        if (metadata.OfType<IAllowAnonymous>().Any())
            return;

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SwaggerSetup.BearerSchemeId }
            }] = Array.Empty<string>()
        });

        AddProblemResponse(operation, StatusCodes.Status401Unauthorized, "Missing, invalid or expired bearer token.");

        var requiresAdmin = metadata.OfType<IAuthorizeData>().Any(a =>
            a.Policy == AuthorizationPolicies.AdminOnly || a.Roles?.Contains(AuthorizationPolicies.AdminRole) == true);
        if (requiresAdmin)
        {
            AddProblemResponse(operation, StatusCodes.Status403Forbidden, "Requires the Admin role.");
            operation.Description = string.Join("\n\n", new[] { operation.Description, "**Requires role:** `Admin`." }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    private static void AddProblemResponse(OpenApiOperation operation, int status, string description)
    {
        var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (operation.Responses.ContainsKey(key))
            return;

        operation.Responses[key] = new OpenApiResponse
        {
            Description = description,
            Content =
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = nameof(Microsoft.AspNetCore.Mvc.ProblemDetails) }
                    }
                }
            }
        };
    }
}
