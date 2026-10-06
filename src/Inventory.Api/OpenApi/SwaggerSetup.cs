using Microsoft.OpenApi.Models;

namespace Inventory.Api.OpenApi;

public static class SwaggerSetup
{
    public const string BearerSchemeId = "Bearer";
    private const string DocumentName = "v1";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = "Inventory Reservation API",
                Version = DocumentName,
                Description =
                    "Reserve stock for two minutes, then confirm (sell) or cancel (release) it. " +
                    "Unconfirmed reservations expire automatically.\n\n" +
                    "**To call protected endpoints:** `POST /api/v1/auth/login`, copy `accessToken` from the response, " +
                    "click **Authorize** and paste the token (without the `Bearer ` prefix).\n\n" +
                    "Errors are RFC 9457 `application/problem+json` with `errorCode` and `correlationId`."
            });

            // "http"/"bearer" makes Swagger UI add the "Bearer " prefix itself.
            options.AddSecurityDefinition(BearerSchemeId, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "Paste the `accessToken` returned by `POST /api/v1/auth/login`."
            });

            foreach (var assembly in new[] { typeof(Program).Assembly, typeof(Application.DependencyInjection).Assembly })
            {
                var xml = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
                if (File.Exists(xml))
                    options.IncludeXmlComments(xml, includeControllerXmlComments: true);
            }

            // Registered after the XML comment filters so its additions are not overwritten by <remarks>.
            // Security requirements and 401/403/429 responses derived from the real endpoint metadata.
            options.OperationFilter<AuthorizationOperationFilter>();

            options.SupportNonNullableReferenceTypes();
            // Lets referenced types (e.g. ReservationStatus) carry a per-property description.
            options.UseAllOfToExtendReferenceSchemas();
        });

        return services;
    }

    /// <summary>Serves <c>/swagger/v1/swagger.json</c> and the UI at <c>/swagger</c>.</summary>
    public static IApplicationBuilder UseApiDocumentation(this IApplicationBuilder app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Inventory Reservation API v1");
            options.DocumentTitle = "Inventory Reservation API";
            options.EnablePersistAuthorization(); // keep the token across page reloads
            options.DisplayRequestDuration();
        });
        return app;
    }
}
