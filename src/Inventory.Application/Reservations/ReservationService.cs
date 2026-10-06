using Inventory.Application.Abstractions;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common;
using Inventory.Application.Common.Exceptions;
using Inventory.Application.Products;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Reservations;

public sealed class ReservationService(
    IReservationRepository reservations,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<ReservationService> logger) : IReservationService
{
    public async Task<ReservationResponse> CreateAsync(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var productId = request.ProductId!.Value;
        var quantity = request.Quantity!.Value;

        // Sets CreatedAtUtc = now and ExpiresAtUtc = now + Reservation.Lifetime.
        var reservation = Reservation.Create(productId, currentUser.UserId, quantity, timeProvider.GetUtcNowDateTime());

        // Availability is checked by the database in the same statement that holds the stock — never in C#.
        var outcome = await reservations.ReserveStockAndAddAsync(reservation, cancellationToken);

        switch (outcome)
        {
            case StockReservationOutcome.Reserved:
                logger.LogInformation(
                    "Reservation {ReservationId} created for product {ProductId}, quantity {Quantity}, user {UserId}, expires {ExpiresAtUtc:O}.",
                    reservation.Id, productId, quantity, reservation.UserId, reservation.ExpiresAtUtc);
                return ReservationResponse.From(reservation);
            case StockReservationOutcome.ProductNotFound:
                throw new ProductNotFoundException(productId);
            case StockReservationOutcome.InsufficientStock:
                logger.LogInformation(
                    "Reservation rejected for product {ProductId}: insufficient stock for quantity {Quantity}.", productId, quantity);
                throw new InsufficientStockException(productId, quantity);
            default:
                throw new InvalidOperationException($"Unexpected outcome '{outcome}'.");
        }
    }

    public async Task<ReservationResponse> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await GetRequiredAsync(reservationId, cancellationToken);

        // Domain rules first (clear errors in the common case); the conditional write below settles races.
        reservation.Confirm(timeProvider.GetUtcNowDateTime());

        if (!await reservations.ConfirmAsync(reservation, cancellationToken))
            throw new ReservationConcurrencyException(reservationId);

        logger.LogInformation(
            "Reservation {ReservationId} confirmed for product {ProductId}, quantity {Quantity}.",
            reservation.Id, reservation.ProductId, reservation.Quantity);

        return ReservationResponse.From(reservation);
    }

    public async Task<ReservationResponse> CancelAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await GetRequiredAsync(reservationId, cancellationToken);

        reservation.Cancel(timeProvider.GetUtcNowDateTime());

        if (!await reservations.CancelAsync(reservation, cancellationToken))
            throw new ReservationConcurrencyException(reservationId);

        logger.LogInformation(
            "Reservation {ReservationId} cancelled for product {ProductId}, quantity {Quantity} released.",
            reservation.Id, reservation.ProductId, reservation.Quantity);

        return ReservationResponse.From(reservation);
    }

    /// <summary>Loads the reservation and checks the caller owns it (admins may act on any reservation).</summary>
    private async Task<Reservation> GetRequiredAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await reservations.GetByIdAsync(reservationId, cancellationToken)
                          ?? throw new ReservationNotFoundException(reservationId);

        if (reservation.UserId != currentUser.UserId && !currentUser.IsInRole(UserRole.Admin))
            throw new ForbiddenAccessException($"Reservation '{reservationId}' belongs to another user.");

        return reservation;
    }

    private static void Validate(CreateReservationRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.ProductId is null || request.ProductId == Guid.Empty)
            errors[nameof(request.ProductId)] = ["Product id is required."];

        if (request.Quantity is null)
            errors[nameof(request.Quantity)] = ["Quantity is required."];
        else if (request.Quantity <= 0)
            errors[nameof(request.Quantity)] = ["Quantity must be greater than zero."];

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }
}
