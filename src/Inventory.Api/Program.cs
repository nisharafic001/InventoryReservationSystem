using System.Text.Json.Serialization;
using Inventory.Api.Auth;
using Inventory.Api.BackgroundJobs;
using Inventory.Api.ErrorHandling;
using Inventory.Api.Observability;
using Inventory.Api.OpenApi;
using Inventory.Api.RateLimiting;
using Inventory.Application;
using Inventory.Application.Abstractions;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStructuredLogging(builder.Configuration);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddJwtAuthentication();
builder.Services.AddApiRateLimiting(builder.Configuration);

builder.Services.AddOptions<ReservationExpiryOptions>()
    .Bind(builder.Configuration.GetSection(ReservationExpiryOptions.SectionName))
    .Validate(o => o.PollingInterval > TimeSpan.Zero, "ReservationExpiry:PollingInterval must be positive.")
    .Validate(o => o.BatchSize > 0, "ReservationExpiry:BatchSize must be positive.")
    .ValidateOnStart();
builder.Services.AddHostedService<ReservationExpiryWorker>();

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddConsistentProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddApiDocumentation();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

// Outermost: every log line and error response of the request carries the correlation id.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseStructuredRequestLogging();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Interactive docs: on in Development, opt-in elsewhere via Swagger:Enabled. Served before auth and rate limiting.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
    app.UseApiDocumentation();

app.UseAuthentication();
// After authentication (limits are per user), before authorization (floods of bad tokens are limited too).
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok("OK"))
    .AllowAnonymous()
    .DisableRateLimiting()
    .WithTags("Health")
    .WithSummary("Liveness probe.")
    .WithDescription("Returns 200 \"OK\" while the process is running. No authentication, not rate limited.")
    .Produces<string>();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
    .AllowAnonymous()
    .DisableRateLimiting();
app.MapControllers();

// Containers opt in (Database:MigrateOnStartup=true); otherwise apply migrations with `dotnet ef database update`.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    await app.Services.MigrateDatabaseAsync();

// Optional bootstrap users from configuration/environment (none by default).
await app.Services.SeedConfiguredUsersAsync(app.Configuration);

app.Run();

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
