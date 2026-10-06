using Microsoft.Extensions.Logging.Abstractions;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;

namespace Inventory.UnitTests.Application;

public class ReservationExpiryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 10, 0, TimeSpan.Zero);

    private readonly StubRepository _repository = new();
    private readonly ReservationExpiryService _service;

    public ReservationExpiryServiceTests()
    {
        _service = new ReservationExpiryService(_repository, new FixedTimeProvider(Now), NullLogger<ReservationExpiryService>.Instance);
    }

    private static Reservation DueReservation() =>
        Reservation.Create(Guid.NewGuid(), "user-1", 1, Now.UtcDateTime.AddMinutes(-5));

    [Fact]
    public async Task ExpireDueAsync_ExpiresEachDueReservationWithCurrentTime()
    {
        _repository.Due.AddRange([DueReservation(), DueReservation()]);

        var result = await _service.ExpireDueAsync(10, default);

        Assert.Equal(new ExpiryBatchResult(2, 2), result);
        Assert.All(_repository.Written, r => Assert.Equal(ReservationStatus.Expired, r.Status));
        Assert.Equal(Now.UtcDateTime, _repository.QueriedAt);
        Assert.Equal(10, _repository.QueriedMaxCount);
    }

    [Fact]
    public async Task ExpireDueAsync_LostRaces_AreNotCountedAsExpired()
    {
        _repository.Due.AddRange([DueReservation(), DueReservation(), DueReservation()]);
        _repository.WinsRemaining = 1;

        var result = await _service.ExpireDueAsync(10, default);

        Assert.Equal(new ExpiryBatchResult(3, 1), result);
    }

    [Fact]
    public async Task ExpireDueAsync_Cancelled_StopsBetweenReservations()
    {
        _repository.Due.AddRange([DueReservation(), DueReservation()]);
        using var cts = new CancellationTokenSource();
        _repository.OnWrite = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExpireDueAsync(10, cts.Token));
        Assert.Single(_repository.Written);
    }

    [Fact]
    public async Task ExpireDueAsync_OneFailingReservation_DoesNotBlockTheOthers()
    {
        var poison = DueReservation();
        var healthy1 = DueReservation();
        var healthy2 = DueReservation();
        _repository.Due.AddRange([poison, healthy1, healthy2]); // the poison row comes first, as the oldest would
        _repository.FailFor.Add(poison.Id);

        var result = await _service.ExpireDueAsync(10, default);

        Assert.Equal(new ExpiryBatchResult(Found: 3, Expired: 2, Failed: 1), result);
        Assert.Equal([healthy1.Id, healthy2.Id], _repository.Written.Select(r => r.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExpireDueAsync_NonPositiveBatchSize_Throws(int batchSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.ExpireDueAsync(batchSize, default));
    }

    private sealed class StubRepository : IReservationRepository
    {
        public List<Reservation> Due { get; } = [];
        public List<Reservation> Written { get; } = [];
        public int WinsRemaining { get; set; } = int.MaxValue;
        public Action? OnWrite { get; set; }
        public DateTime QueriedAt { get; private set; }
        public int QueriedMaxCount { get; private set; }

        public Task<IReadOnlyList<Reservation>> GetDueForExpiryAsync(DateTime nowUtc, int maxCount, CancellationToken cancellationToken)
        {
            (QueriedAt, QueriedMaxCount) = (nowUtc, maxCount);
            return Task.FromResult<IReadOnlyList<Reservation>>(Due);
        }

        /// <summary>Reservations whose write throws, simulating a poison row.</summary>
        public HashSet<Guid> FailFor { get; } = [];

        public Task<bool> ExpireAsync(Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(reservation.Id))
                throw new InvalidOperationException("Product counters are inconsistent for this reservation.");
            Written.Add(reservation);
            OnWrite?.Invoke();
            return Task.FromResult(WinsRemaining-- > 0);
        }

        public Task<StockReservationOutcome> ReserveStockAndAddAsync(Reservation reservation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ConfirmAsync(Reservation reservation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CancelAsync(Reservation reservation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
