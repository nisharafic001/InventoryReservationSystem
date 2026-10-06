using Inventory.Application.Common.Exceptions;
using MySqlConnector;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Retries a whole unit of work when MySQL aborts it with a deadlock (1213) or a lock wait timeout (1205).
/// Only safe for operations that are idempotent on retry; every reservation write is a conditional transition,
/// so a retry either applies once or matches nothing.
/// </summary>
internal static class TransientMySqlRetry
{
    internal const int MaxAttempts = 3;

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, Action beforeRetry, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt >= MaxAttempts)
                    throw new TemporarilyUnavailableException(
                        "The inventory is busy right now. Nothing was changed; please retry shortly.", ex);

                beforeRetry();
                // Short jittered backoff so competing transactions do not collide again in lock-step.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt + Random.Shared.Next(0, 50)), cancellationToken);
            }
        }
    }

    public static bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is MySqlException { ErrorCode: MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout })
                return true;
        return false;
    }
}
