using Inventory.Application.Reservations;
using Inventory.Api.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>Reserve, confirm and cancel stock. Reservations expire automatically after two minutes.</summary>
[ApiController]
[Route("api/v1/reservations")]
[EnableRateLimiting(RateLimitingSetup.ReservationsPolicy)]
[Authorize]
[Tags("Reservations")]
public sealed class ReservationsController(IReservationService reservationService) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>Reserve stock for two minutes.</summary>
    /// <remarks>
    /// Atomically holds `quantity` units for the caller. The reservation is `Active` until it is confirmed,
    /// cancelled, or expires at `expiresAtUtc` (2 minutes after creation), when its stock is released.
    /// Safe under any concurrency: stock can never be oversold.
    /// </remarks>
    /// <response code="201">Stock held; the reservation is Active.</response>
    /// <response code="400">Missing product id, or quantity not greater than zero (`VALIDATION_FAILED`).</response>
    /// <response code="404">The product does not exist (`PRODUCT_NOT_FOUND`).</response>
    /// <response code="409">Not enough stock available (`INSUFFICIENT_STOCK`).</response>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    public async Task<ActionResult<ReservationResponse>> Create(
        CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var reservation = await reservationService.CreateAsync(request, cancellationToken);

        // No GET endpoint for reservations exists yet, so there is no Location to point at.
        return StatusCode(StatusCodes.Status201Created, reservation);
    }

    /// <summary>Confirm (purchase) an Active reservation.</summary>
    /// <remarks>Moves the reserved quantity to sold. Only the owner (or an Admin) may confirm, and only before it expires.</remarks>
    /// <param name="id">The reservation id returned when it was created.</param>
    /// <response code="200">Confirmed; the stock is sold.</response>
    /// <response code="403">The reservation belongs to another user (`FORBIDDEN`).</response>
    /// <response code="404">The reservation does not exist (`RESERVATION_NOT_FOUND`).</response>
    /// <response code="409">Not Active (`INVALID_RESERVATION_STATE`), expired (`RESERVATION_EXPIRED`), or changed concurrently (`RESERVATION_CONCURRENTLY_MODIFIED`).</response>
    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    public async Task<ActionResult<ReservationResponse>> Confirm(Guid id, CancellationToken cancellationToken) =>
        Ok(await reservationService.ConfirmAsync(id, cancellationToken));

    /// <summary>Cancel an Active reservation.</summary>
    /// <remarks>Releases the reserved quantity back to available stock. Only the owner (or an Admin) may cancel.</remarks>
    /// <param name="id">The reservation id returned when it was created.</param>
    /// <response code="200">Cancelled; the stock is available again.</response>
    /// <response code="403">The reservation belongs to another user (`FORBIDDEN`).</response>
    /// <response code="404">The reservation does not exist (`RESERVATION_NOT_FOUND`).</response>
    /// <response code="409">Not Active (`INVALID_RESERVATION_STATE`) or changed concurrently (`RESERVATION_CONCURRENTLY_MODIFIED`).</response>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    public async Task<ActionResult<ReservationResponse>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await reservationService.CancelAsync(id, cancellationToken));
}
