using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Sets MySQL's <c>innodb_lock_wait_timeout</c> for every connection EF opens, so a request queued behind a busy
/// row fails fast (and is retried / answered with 503) instead of holding a pooled connection for MySQL's default 50 s.
/// Enabled only when <c>Database:LockWaitTimeoutSeconds</c> is configured.
/// </summary>
public sealed class LockWaitTimeoutInterceptor(int seconds) : DbConnectionInterceptor
{
    private readonly string _sql = $"SET SESSION innodb_lock_wait_timeout = {seconds};";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
