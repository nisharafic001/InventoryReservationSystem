using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Persistence;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies pending EF Core migrations, retrying while the database is still starting.
    /// Safe with several instances starting at once: the provider serializes migrations with a database lock.
    /// </summary>
    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services, int maxAttempts = 10, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Applying {Count} migration(s): {Migrations}.", pending.Count, pending);
                    await db.Database.MigrateAsync(cancellationToken);
                }

                logger.LogInformation("Database schema is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 10));
                // Exception type only: messages from the driver can include server details.
                logger.LogWarning("Database not ready ({ErrorType}); retrying in {Delay} (attempt {Attempt}/{MaxAttempts}).",
                    ex.GetType().Name, delay, attempt, maxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
