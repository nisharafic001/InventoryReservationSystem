using Inventory.Application.Auth;
using Inventory.Application.Common.Exceptions;
using Inventory.Application.Products;
using Inventory.Application.Reservations;
using Inventory.Domain.Exceptions;

namespace Inventory.Api.ErrorHandling;

/// <param name="Detail">Safe, client-facing explanation; <c>null</c> to omit.</param>
public sealed record ApiError(int Status, string ErrorCode, string Title, string? Detail = null);

/// <summary>
/// The single source of truth for how failures are presented to clients. Only messages of exceptions this
/// codebase throws deliberately are surfaced; everything else becomes a generic 500.
/// </summary>
public static class ErrorCatalog
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string ReservationNotFound = "RESERVATION_NOT_FOUND";
    public const string DuplicateSku = "DUPLICATE_SKU";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string InvalidReservationState = "INVALID_RESERVATION_STATE";
    public const string ReservationExpired = "RESERVATION_EXPIRED";
    public const string ReservationModified = "RESERVATION_CONCURRENTLY_MODIFIED";
    public const string BusinessRuleViolation = "BUSINESS_RULE_VIOLATION";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RateLimited = "RATE_LIMITED";
    public const string TemporarilyUnavailable = "TEMPORARILY_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";

    public const string ValidationTitle = "One or more validation errors occurred.";
    public const string InternalErrorDetail = "An unexpected error occurred. Quote the correlation id when reporting this problem.";

    public static ApiError FromException(Exception exception) => exception switch
    {
        ValidationException => new(StatusCodes.Status400BadRequest, ValidationFailed, ValidationTitle),
        BadHttpRequestException ex => new(ex.StatusCode, BadRequest, "Bad request", "The request could not be read."),

        InvalidCredentialsException ex => new(StatusCodes.Status401Unauthorized, InvalidCredentials, "Authentication failed", ex.Message),
        UnauthorizedException ex => new(StatusCodes.Status401Unauthorized, Unauthorized, "Unauthorized", ex.Message),
        ForbiddenAccessException ex => new(StatusCodes.Status403Forbidden, Forbidden, "Forbidden", ex.Message),

        ProductNotFoundException ex => new(StatusCodes.Status404NotFound, ProductNotFound, "Product not found", ex.Message),
        ReservationNotFoundException ex => new(StatusCodes.Status404NotFound, ReservationNotFound, "Reservation not found", ex.Message),
        NotFoundException ex => new(StatusCodes.Status404NotFound, NotFound, "Not found", ex.Message),

        DuplicateSkuException ex => new(StatusCodes.Status409Conflict, DuplicateSku, "Duplicate SKU", ex.Message),
        Application.Reservations.InsufficientStockException ex =>
            new(StatusCodes.Status409Conflict, InsufficientStock, "Insufficient inventory", ex.Message),
        ReservationConcurrencyException ex =>
            new(StatusCodes.Status409Conflict, ReservationModified, "Reservation was modified", ex.Message),
        ConflictException ex => new(StatusCodes.Status409Conflict, "CONFLICT", "Conflict", ex.Message),

        InvalidReservationTransitionException ex =>
            new(StatusCodes.Status409Conflict, InvalidReservationState, "Invalid reservation state", ex.Message),
        ReservationExpiredException ex =>
            new(StatusCodes.Status409Conflict, ReservationExpired, "Reservation expired", ex.Message),
        Domain.Exceptions.InsufficientStockException ex =>
            new(StatusCodes.Status409Conflict, InsufficientStock, "Insufficient inventory", ex.Message),
        DomainException ex => new(StatusCodes.Status400BadRequest, BusinessRuleViolation, "Business rule violation", ex.Message),

        TemporarilyUnavailableException ex =>
            new(StatusCodes.Status503ServiceUnavailable, TemporarilyUnavailable, "Temporarily unavailable", ex.Message),

        // Never echo the message of an unknown exception: it may contain SQL, connection details or internals.
        _ => new(StatusCodes.Status500InternalServerError, InternalError, "Internal server error", InternalErrorDetail)
    };

    /// <summary>Defaults for responses produced without an exception (auth challenges, unknown routes, ...).</summary>
    public static ApiError FromStatusCode(int status) => status switch
    {
        StatusCodes.Status400BadRequest => new(status, BadRequest, "Bad request"),
        StatusCodes.Status401Unauthorized =>
            new(status, Unauthorized, "Unauthorized", "A valid bearer token is required."),
        StatusCodes.Status403Forbidden =>
            new(status, Forbidden, "Forbidden", "You do not have permission to perform this action."),
        StatusCodes.Status404NotFound => new(status, NotFound, "Not found"),
        StatusCodes.Status405MethodNotAllowed => new(status, MethodNotAllowed, "Method not allowed"),
        StatusCodes.Status415UnsupportedMediaType => new(status, UnsupportedMediaType, "Unsupported media type"),
        StatusCodes.Status429TooManyRequests =>
            new(status, RateLimited, "Too many requests", "Rate limit exceeded. Retry later."),
        StatusCodes.Status503ServiceUnavailable => new(status, TemporarilyUnavailable, "Temporarily unavailable", "Please retry shortly."),
        >= 500 => new(status, InternalError, "Internal server error", InternalErrorDetail),
        _ => new(status, $"HTTP_{status}", "Request failed")
    };
}
