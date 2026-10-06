using Inventory.Application.Abstractions;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Common.Exceptions;
using Inventory.Application.Products;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.UnitTests.Application;

public class ReservationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductId = Guid.NewGuid();

    private readonly StubReservationRepository _repository = new();
    private readonly ReservationService _service;

    public ReservationServiceTests()
    {
        _service = new ReservationService(
            _repository,
            new StubCurrentUser("user-42"),
            new FixedTimeProvider(Now),
            NullLogger<ReservationService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_Reserved_ReturnsActiveReservationExpiringInTwoMinutes()
    {
        var response = await _service.CreateAsync(new CreateReservationRequest(ProductId, 2), default);

        Assert.Equal(ProductId, response.ProductId);
        Assert.Equal(2, response.Quantity);
        Assert.Equal(ReservationStatus.Active, response.Status);
        Assert.Equal(Now.UtcDateTime, response.CreatedAtUtc);
        Assert.Equal(Now.UtcDateTime.AddMinutes(2), response.ExpiresAtUtc);

        var passed = Assert.Single(_repository.Received);
        Assert.Equal(response.ReservationId, passed.Id);
        Assert.Equal("user-42", passed.UserId);
    }

    [Fact]
    public async Task CreateAsync_ProductNotFound_ThrowsProductNotFoundException()
    {
        _repository.Outcome = StockReservationOutcome.ProductNotFound;

        var ex = await Assert.ThrowsAsync<ProductNotFoundException>(
            () => _service.CreateAsync(new CreateReservationRequest(ProductId, 1), default));

        Assert.Equal(ProductId, ex.ProductId);
    }

    [Fact]
    public async Task CreateAsync_InsufficientStock_ThrowsInsufficientStockException()
    {
        _repository.Outcome = StockReservationOutcome.InsufficientStock;

        var ex = await Assert.ThrowsAsync<InsufficientStockException>(
            () => _service.CreateAsync(new CreateReservationRequest(ProductId, 5), default));

        Assert.Equal(5, ex.RequestedQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(null)]
    public async Task CreateAsync_InvalidQuantity_ThrowsValidationWithoutReachingRepository(int? quantity)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateAsync(new CreateReservationRequest(ProductId, quantity), default));

        Assert.Equal(["Quantity"], ex.Errors.Keys);
        Assert.Empty(_repository.Received);
    }

    [Fact]
    public async Task CreateAsync_MissingProductId_ThrowsValidation()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateAsync(new CreateReservationRequest(Guid.Empty, 1), default));

        Assert.Equal(["ProductId"], ex.Errors.Keys);
        Assert.Empty(_repository.Received);
    }

    private Reservation StoreActiveReservation(DateTimeOffset createdAt)
    {
        var reservation = Reservation.Create(ProductId, "user-42", 2, createdAt.UtcDateTime);
        _repository.Stored = reservation;
        return reservation;
    }

    [Fact]
    public async Task ConfirmAsync_Active_PersistsConfirmedReservation()
    {
        var reservation = StoreActiveReservation(Now.AddMinutes(-1));

        var response = await _service.ConfirmAsync(reservation.Id, default);

        Assert.Equal(ReservationStatus.Confirmed, response.Status);
        Assert.Equal(Now.UtcDateTime, response.ConfirmedAtUtc);
        Assert.Null(response.CancelledAtUtc);
        Assert.Equal(ReservationStatus.Confirmed, Assert.Single(_repository.Written).Status);
    }

    [Fact]
    public async Task CancelAsync_Active_PersistsCancelledReservation()
    {
        var reservation = StoreActiveReservation(Now.AddMinutes(-1));

        var response = await _service.CancelAsync(reservation.Id, default);

        Assert.Equal(ReservationStatus.Cancelled, response.Status);
        Assert.Equal(Now.UtcDateTime, response.CancelledAtUtc);
        Assert.Equal(ReservationStatus.Cancelled, Assert.Single(_repository.Written).Status);
    }

    [Fact]
    public async Task ConfirmAsync_Unknown_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.ConfirmAsync(Guid.NewGuid(), default));
        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.CancelAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ConfirmAsync_Expired_ThrowsWithoutWriting()
    {
        var reservation = StoreActiveReservation(Now.AddMinutes(-3));

        await Assert.ThrowsAsync<ReservationExpiredException>(() => _service.ConfirmAsync(reservation.Id, default));
        Assert.Empty(_repository.Written);
    }

    [Fact]
    public async Task ConfirmAsync_NotActive_ThrowsWithoutWriting()
    {
        var reservation = StoreActiveReservation(Now.AddMinutes(-1));
        reservation.Cancel(Now.UtcDateTime);

        await Assert.ThrowsAsync<InvalidReservationTransitionException>(() => _service.ConfirmAsync(reservation.Id, default));
        await Assert.ThrowsAsync<InvalidReservationTransitionException>(() => _service.CancelAsync(reservation.Id, default));
        Assert.Empty(_repository.Written);
    }

    [Fact]
    public async Task ConfirmAndCancel_OtherUsersReservation_ThrowsForbiddenWithoutWriting()
    {
        _repository.Stored = Reservation.Create(ProductId, "someone-else", 1, Now.UtcDateTime);

        await Assert.ThrowsAsync<ForbiddenAccessException>(() => _service.ConfirmAsync(_repository.Stored.Id, default));
        await Assert.ThrowsAsync<ForbiddenAccessException>(() => _service.CancelAsync(_repository.Stored.Id, default));
        Assert.Empty(_repository.Written);
    }

    [Fact]
    public async Task Confirm_OtherUsersReservation_AllowedForAdmin()
    {
        var adminService = new ReservationService(
            _repository, new StubCurrentUser("admin-1", UserRole.Admin), new FixedTimeProvider(Now), NullLogger<ReservationService>.Instance);
        _repository.Stored = Reservation.Create(ProductId, "someone-else", 1, Now.UtcDateTime.AddMinutes(-1));

        var response = await adminService.ConfirmAsync(_repository.Stored.Id, default);

        Assert.Equal(ReservationStatus.Confirmed, response.Status);
        Assert.Single(_repository.Written);
    }

    [Fact]
    public async Task Cancel_OtherUsersReservation_AllowedForAdmin()
    {
        var adminService = new ReservationService(_repository, new StubCurrentUser("admin-1", UserRole.Admin), new FixedTimeProvider(Now), NullLogger<ReservationService>.Instance);
        _repository.Stored = Reservation.Create(ProductId, "someone-else", 1, Now.UtcDateTime);

        var response = await adminService.CancelAsync(_repository.Stored.Id, default);

        Assert.Equal(ReservationStatus.Cancelled, response.Status);
    }

    [Fact]
    public async Task ConfirmAndCancel_LostRace_ThrowsConcurrencyException()
    {
        var reservation = StoreActiveReservation(Now.AddMinutes(-1));
        _repository.TransitionSucceeds = false;

        await Assert.ThrowsAsync<ReservationConcurrencyException>(() => _service.ConfirmAsync(reservation.Id, default));

        // The confirm attempt mutated the stub's instance; use a fresh one for cancel.
        var other = StoreActiveReservation(Now.AddMinutes(-1));
        await Assert.ThrowsAsync<ReservationConcurrencyException>(() => _service.CancelAsync(other.Id, default));
    }

    private sealed class StubReservationRepository : IReservationRepository
    {
        public StockReservationOutcome Outcome { get; set; } = StockReservationOutcome.Reserved;
        public List<Reservation> Received { get; } = [];

        /// <summary>The reservation returned by <see cref="GetByIdAsync"/>.</summary>
        public Reservation? Stored { get; set; }
        public bool TransitionSucceeds { get; set; } = true;
        public List<Reservation> Written { get; } = [];

        public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken)
        {
            Received.Add(reservation);
            return Task.FromResult(Outcome);
        }

        public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Id == id ? Stored : null);

        public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) => Write(reservation);

        public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) => Write(reservation);

        public Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private Task<bool> Write(Reservation reservation)
        {
            if (TransitionSucceeds)
                Written.Add(reservation);
            return Task.FromResult(TransitionSucceeds);
        }
    }

    private sealed class StubCurrentUser(string userId, UserRole role = UserRole.User) : ICurrentUser
    {
        public string UserId => userId;

        public bool IsInRole(UserRole r) => r == role;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
