namespace Inventory.Application.Reservations;

public interface IReservationService
{
    /// <exception cref="Common.Exceptions.ValidationException">The request is invalid.</exception>
    /// <exception cref="Products.ProductNotFoundException">The product does not exist.</exception>
    /// <exception cref="InsufficientStockException">Not enough stock is available.</exception>
    Task<ReservationResponse> CreateAsync(CreateReservationRequest request, CancellationToken cancellationToken);

    /// <summary>Active → Confirmed; moves the quantity from reserved to sold.</summary>
    /// <exception cref="ReservationNotFoundException">The reservation does not exist.</exception>
    /// <exception cref="Domain.Exceptions.InvalidReservationTransitionException">The reservation is not Active.</exception>
    /// <exception cref="Domain.Exceptions.ReservationExpiredException">The reservation has expired.</exception>
    /// <exception cref="ReservationConcurrencyException">Another request changed the reservation first.</exception>
    Task<ReservationResponse> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>Active → Cancelled; releases the reserved quantity.</summary>
    /// <exception cref="ReservationNotFoundException">The reservation does not exist.</exception>
    /// <exception cref="Domain.Exceptions.InvalidReservationTransitionException">The reservation is not Active.</exception>
    /// <exception cref="ReservationConcurrencyException">Another request changed the reservation first.</exception>
    Task<ReservationResponse> CancelAsync(Guid reservationId, CancellationToken cancellationToken);
}
