using System.Linq.Expressions;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Inventory.Infrastructure.Persistence.Repositories;

public sealed class ReservationRepository(AppDbContext dbContext) : IReservationRepository
{
    // Each write is one short transaction. Deadlocks and lock-wait timeouts abort it as a whole, so it is retried as a whole
    // (see TransientMySqlRetry); the conditional statements make a retry apply at most once.
    public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken) =>
        Retry(() => ReserveStockAndAddOnceAsync(reservation, cancellationToken), cancellationToken);

    public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) =>
        Retry(() => ConfirmOnceAsync(reservation, cancellationToken), cancellationToken);

    public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) =>
        Retry(() => CancelOnceAsync(reservation, cancellationToken), cancellationToken);

    public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken) =>
        Retry(() => ExpireOnceAsync(reservation, nowUtc, cancellationToken), cancellationToken);

    private Task<T> Retry<T>(Func<Task<T>> operation, CancellationToken cancellationToken) =>
        TransientMySqlRetry.ExecuteAsync(operation, beforeRetry: dbContext.ChangeTracker.Clear, cancellationToken);

    private async Task<StockReservationOutcome> ReserveStockAndAddOnceAsync(
        Reservation reservation, CancellationToken cancellationToken)
    {
        var productId = reservation.ProductId;
        var quantity = reservation.Quantity;
        var nowUtc = reservation.CreatedAtUtc;

        // Disposing without a commit rolls back, so an exception anywhere below leaves stock untouched.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Single atomic conditional UPDATE: the availability check and the increment happen in one
        // statement under the row lock, so concurrent requests cannot oversell. Translates to:
        //   UPDATE Products SET ReservedQuantity = ReservedQuantity + @quantity, UpdatedAtUtc = @now
        //   WHERE Id = @productId AND TotalQuantity - SoldQuantity - ReservedQuantity >= @quantity
        var affectedRows = await dbContext.Products
            .Where(p => p.Id == productId
                        && p.TotalQuantity - p.SoldQuantity - p.ReservedQuantity >= quantity)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity + quantity)
                    .SetProperty(p => p.UpdatedAtUtc, nowUtc),
                cancellationToken);

        if (affectedRows == 0)
        {
            await transaction.RollbackAsync(cancellationToken);

            // Only classifies the failure for the response; no stock decision depends on this read.
            var productExists = await dbContext.Products.AnyAsync(p => p.Id == productId, cancellationToken);
            return productExists ? StockReservationOutcome.InsufficientStock : StockReservationOutcome.ProductNotFound;
        }

        dbContext.Reservations.Add(reservation);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // INSERT failed: release the stock held by the UPDATE above.
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.Entry(reservation).State = EntityState.Detached;
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        return StockReservationOutcome.Reserved;
    }

    public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    private async Task<bool> ConfirmOnceAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        EnsureStatus(reservation, ReservationStatus.Confirmed);
        var confirmedAtUtc = reservation.ConfirmedAtUtc!.Value;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Conditional state transition: only one concurrent confirm/cancel/expire can match Status = Active.
        // InnoDB re-evaluates the WHERE against the latest committed row after waiting for its lock.
        var transitioned = await dbContext.Reservations
            .Where(r => r.Id == reservation.Id
                        && r.Status == ReservationStatus.Active
                        && r.ExpiresAtUtc > confirmedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, ReservationStatus.Confirmed)
                    .SetProperty(r => r.ConfirmedAtUtc, confirmedAtUtc),
                cancellationToken);

        if (transitioned == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // Reserved → sold, in the same transaction as the state change.
        await MoveStockAsync(
            reservation,
            confirmedAtUtc,
            setters => setters
                .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - reservation.Quantity)
                .SetProperty(p => p.SoldQuantity, p => p.SoldQuantity + reservation.Quantity)
                .SetProperty(p => p.UpdatedAtUtc, confirmedAtUtc),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> CancelOnceAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        EnsureStatus(reservation, ReservationStatus.Cancelled);
        var cancelledAtUtc = reservation.CancelledAtUtc!.Value;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var transitioned = await dbContext.Reservations
            .Where(r => r.Id == reservation.Id && r.Status == ReservationStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, ReservationStatus.Cancelled)
                    .SetProperty(r => r.CancelledAtUtc, cancelledAtUtc),
                cancellationToken);

        if (transitioned == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // Release the hold, in the same transaction as the state change.
        await MoveStockAsync(
            reservation,
            cancelledAtUtc,
            setters => setters
                .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - reservation.Quantity)
                .SetProperty(p => p.UpdatedAtUtc, cancelledAtUtc),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(
        DateTime nowUtc, int maxCount, CancellationToken cancellationToken) =>
        // Served by IX_Reservations_Status_ExpiresAtUtc.
        await dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAtUtc <= nowUtc)
            .OrderBy(r => r.ExpiresAtUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    private async Task<bool> ExpireOnceAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        EnsureStatus(reservation, ReservationStatus.Expired);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Conditional state transition: if several instances (or a confirm/cancel) race for this row,
        // only one matches Status = Active, so the stock below is released exactly once.
        var transitioned = await dbContext.Reservations
            .Where(r => r.Id == reservation.Id
                        && r.Status == ReservationStatus.Active
                        && r.ExpiresAtUtc <= nowUtc)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, ReservationStatus.Expired),
                cancellationToken);

        if (transitioned == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await MoveStockAsync(
            reservation,
            nowUtc,
            setters => setters
                .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - reservation.Quantity)
                .SetProperty(p => p.UpdatedAtUtc, nowUtc),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task MoveStockAsync(
        Reservation reservation,
        DateTime nowUtc,
        Expression<Func<SetPropertyCalls<Product>, SetPropertyCalls<Product>>> setters,
        CancellationToken cancellationToken)
    {
        // The ReservedQuantity guard can only fail if the data is already inconsistent; disposing the
        // uncommitted transaction then rolls back the state change too.
        var updated = await dbContext.Products
            .Where(p => p.Id == reservation.ProductId && p.ReservedQuantity >= reservation.Quantity)
            .ExecuteUpdateAsync(setters, cancellationToken);

        if (updated != 1)
            throw new InvalidOperationException(
                $"Product '{reservation.ProductId}' does not hold {reservation.Quantity} reserved unit(s) " +
                $"for reservation '{reservation.Id}' at {nowUtc:O}.");
    }

    private static void EnsureStatus(Reservation reservation, ReservationStatus expected)
    {
        if (reservation.Status != expected)
            throw new ArgumentException(
                $"Reservation must be transitioned to {expected} before it is persisted.", nameof(reservation));
    }
}
