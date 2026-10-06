using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inventory.Api.Observability;

/// <summary>Readiness: the API can only serve inventory requests while MySQL is reachable.</summary>
public sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database unreachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No exception details in the response: they can contain connection information.
            return HealthCheckResult.Unhealthy("Database unreachable.");
        }
    }
}
