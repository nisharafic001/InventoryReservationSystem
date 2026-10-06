using System.Net.Http.Json;
using Inventory.Api.BackgroundJobs;
using Inventory.Application;
using Inventory.Application.Abstractions;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence.Repositories;
using Inventory.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// Automatic expiry against real MySQL with a controllable clock (no real two-minute waits).
/// </summary>
public class ReservationExpiryMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>
{
    private const int BatchSize = 100;

    // A fixed anchor keeps the test clock independent of the machine clock.
    private static readonly DateTime T0 = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly MutableTimeProvider _clock = new(T0);

    /// <summary>The application as one API instance would compose it, with the test clock injected.</summary>
    private ServiceProvider BuildInstance(string name = "inventory-api", TimeProvider? clock = null)
    {
        var connectionString = new MySqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = name // separate connection pool per simulated instance
        }.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{Inventory.Infrastructure.DependencyInjection.ConnectionStringName}"] = connectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(clock ?? _clock); // registered first; AddApplication only TryAdds the system clock
        services.AddApplication().AddInfrastructure(configuration);
        services.AddScoped<ICurrentUser>(_ => new TestUser());
        services.AddLogging();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<ExpiryBatchResult> ExpireDueAsync(ServiceProvider instance)
    {
        await using var scope = instance.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReservationExpiryService>()
            .ExpireDueAsync(BatchSize, CancellationToken.None);
    }

    /// <summary>Reserves stock exactly as the API does, with the reservation created at <paramref name="createdAtUtc"/>.</summary>
    private async Task<Reservation> SeedReservationAsync(Product product, int quantity, DateTime createdAtUtc)
    {
        var reservation = Reservation.Create(product.Id, "user-1", quantity, createdAtUtc);
        await using var db = database.CreateDbContext();
        Assert.Equal(
            Application.Abstractions.Persistence.StockReservationOutcome.Reserved,
            await new ReservationRepository(db).ReserveStockAndAddAsync(reservation, default));
        return reservation;
    }

    private async Task<Reservation> GetReservationAsync(Guid productId, Guid reservationId) =>
        (await database.GetReservationsAsync(productId)).Single(r => r.Id == reservationId);

    [Fact]
    public async Task ExpiredActiveReservation_ReleasesStock()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = await SeedReservationAsync(product, 3, T0);
        await using var instance = BuildInstance();

        _clock.UtcNow = T0 + Reservation.Lifetime; // exactly at expiry: ExpiresAtUtc <= now
        var result = await ExpireDueAsync(instance);

        Assert.Equal(new ExpiryBatchResult(Found: 1, Expired: 1), result);
        Assert.Equal(ReservationStatus.Expired, (await GetReservationAsync(product.Id, reservation.Id)).Status);
        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(0, stored.ReservedQuantity);
        Assert.Equal(0, stored.SoldQuantity);
        Assert.Equal(10, stored.AvailableQuantity);
    }

    [Fact]
    public async Task FutureReservation_RemainsActive()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = await SeedReservationAsync(product, 3, T0);
        await using var instance = BuildInstance();

        _clock.UtcNow = T0 + Reservation.Lifetime - TimeSpan.FromMilliseconds(1);
        var result = await ExpireDueAsync(instance);

        Assert.Equal(new ExpiryBatchResult(0, 0), result);
        Assert.Equal(ReservationStatus.Active, (await GetReservationAsync(product.Id, reservation.Id)).Status);
        Assert.Equal(3, (await database.GetProductAsync(product.Id)).ReservedQuantity);
    }

    [Fact]
    public async Task ConfirmedReservation_NotExpired()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = await SeedReservationAsync(product, 3, T0);
        await using var instance = BuildInstance();

        _clock.UtcNow = T0.AddMinutes(1);
        await using (var scope = instance.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IReservationService>().ConfirmAsync(reservation.Id, default);

        _clock.UtcNow = T0.AddMinutes(10);
        var result = await ExpireDueAsync(instance);

        Assert.Equal(new ExpiryBatchResult(0, 0), result);
        Assert.Equal(ReservationStatus.Confirmed, (await GetReservationAsync(product.Id, reservation.Id)).Status);
        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(0, stored.ReservedQuantity);
        Assert.Equal(3, stored.SoldQuantity);
    }

    [Fact]
    public async Task CancelledReservation_NotExpired()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = await SeedReservationAsync(product, 3, T0);
        // Keeps ReservedQuantity > 0 so a wrongful second release would be visible.
        await SeedReservationAsync(product, 4, T0.AddMinutes(30));
        await using var instance = BuildInstance();

        _clock.UtcNow = T0.AddMinutes(1);
        await using (var scope = instance.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IReservationService>().CancelAsync(reservation.Id, default);

        _clock.UtcNow = T0.AddMinutes(10);
        var result = await ExpireDueAsync(instance);

        Assert.Equal(new ExpiryBatchResult(0, 0), result);
        Assert.Equal(ReservationStatus.Cancelled, (await GetReservationAsync(product.Id, reservation.Id)).Status);
        Assert.Equal(4, (await database.GetProductAsync(product.Id)).ReservedQuantity);
    }

    [Fact]
    public async Task ConcurrentExpiry_DoesNotDoubleRelease()
    {
        const int dueReservations = 40;
        const int sweepers = 8;

        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 200);
        var due = new List<Reservation>();
        for (var i = 0; i < dueReservations; i++)
            due.Add(await SeedReservationAsync(product, 2, T0.AddSeconds(i)));
        // Not yet due; its 5 units must stay reserved, so any double release shows up in ReservedQuantity.
        var notDue = await SeedReservationAsync(product, 5, T0.AddMinutes(30));

        // Each sweeper is a separate "API instance" with its own container and connection pool.
        var instances = Enumerable.Range(0, sweepers).Select(i => BuildInstance($"inventory-api-{i}")).ToArray();
        try
        {
            _clock.UtcNow = T0.AddMinutes(10);
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = instances.Select(instance => Task.Run(async () =>
            {
                await startGate.Task;
                return await ExpireDueAsync(instance);
            })).ToArray();

            startGate.SetResult();
            var results = await Task.WhenAll(runs);

            // Every sweeper may see every due reservation, but each is expired exactly once overall.
            Assert.Equal(dueReservations, results.Sum(r => r.Expired));
            Assert.True(results.Count(r => r.Found > 0) > 1, "Sweepers did not overlap.");
        }
        finally
        {
            foreach (var instance in instances)
                await instance.DisposeAsync();
        }

        var reservations = await database.GetReservationsAsync(product.Id);
        Assert.All(reservations.Where(r => r.Id != notDue.Id), r => Assert.Equal(ReservationStatus.Expired, r.Status));
        Assert.Equal(ReservationStatus.Active, reservations.Single(r => r.Id == notDue.Id).Status);

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(5, stored.ReservedQuantity);
        Assert.Equal(0, stored.SoldQuantity);
        Assert.Equal(195, stored.AvailableQuantity);
    }

    [Fact]
    public async Task ExpiryVsConfirm_Race_ExactlyOneTransitionWins()
    {
        await database.ResetAsync();
        // Two instances whose clocks disagree (as real servers can): to the confirmer the reservation is still
        // valid, to the sweeper it is due. Both are eligible, so they genuinely contend for the row.
        var confirmerClock = new MutableTimeProvider(T0.AddMinutes(1));
        _clock.UtcNow = T0.AddMinutes(5);
        await using var confirmer = BuildInstance("confirmer", confirmerClock);
        await using var sweeper = BuildInstance("sweeper");

        for (var round = 0; round < 10; round++)
        {
            var product = await database.SeedProductAsync(totalQuantity: 10);
            var reservation = await SeedReservationAsync(product, 3, T0);

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirm = Task.Run(async () =>
            {
                await startGate.Task;
                await using var scope = confirmer.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IReservationService>().ConfirmAsync(reservation.Id, default);
                    return true;
                }
                catch (ReservationConcurrencyException) { return false; }
                catch (Domain.Exceptions.InvalidReservationTransitionException) { return false; }
                catch (Domain.Exceptions.ReservationExpiredException) { return false; }
            });
            var expire = Task.Run(async () =>
            {
                await startGate.Task;
                return (await ExpireDueAsync(sweeper)).Expired == 1;
            });

            startGate.SetResult();
            var (confirmed, expired) = (await confirm, await expire);

            Assert.True(confirmed ^ expired, $"Round {round}: exactly one transition must win.");
            var stored = await database.GetProductAsync(product.Id);
            var status = (await GetReservationAsync(product.Id, reservation.Id)).Status;
            if (confirmed)
            {
                Assert.Equal(ReservationStatus.Confirmed, status);
                Assert.Equal((0, 3), (stored.ReservedQuantity, stored.SoldQuantity));
            }
            else
            {
                Assert.Equal(ReservationStatus.Expired, status);
                Assert.Equal((0, 0), (stored.ReservedQuantity, stored.SoldQuantity));
            }
        }
    }

    [Fact]
    public async Task PoisonReservation_DoesNotBlockOtherExpiries_AndIsRolledBack()
    {
        await database.ResetAsync();
        var corrupted = await database.SeedProductAsync(totalQuantity: 10);
        var healthy = await database.SeedProductAsync(totalQuantity: 10);
        var poison = await SeedReservationAsync(corrupted, 3, T0);               // oldest: read first
        var normal = await SeedReservationAsync(healthy, 2, T0.AddSeconds(10));
        await using (var db = database.CreateDbContext())
            // Simulate inconsistent counters: the product no longer holds the 3 units its reservation claims.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Products SET ReservedQuantity = 0 WHERE Id = {corrupted.Id}");
        await using var instance = BuildInstance();

        _clock.UtcNow = T0.AddMinutes(10);
        var result = await ExpireDueAsync(instance);

        Assert.Equal(new ExpiryBatchResult(Found: 2, Expired: 1, Failed: 1), result);
        Assert.Equal(ReservationStatus.Expired, (await GetReservationAsync(healthy.Id, normal.Id)).Status);
        Assert.Equal(0, (await database.GetProductAsync(healthy.Id)).ReservedQuantity);
        // The failed transition was rolled back as a whole: still Active, counters untouched.
        Assert.Equal(ReservationStatus.Active, (await GetReservationAsync(corrupted.Id, poison.Id)).Status);
        Assert.Equal(0, (await database.GetProductAsync(corrupted.Id)).ReservedQuantity);
    }

    [Fact]
    public async Task CancelVsExpiry_Race_ReleasesStockExactlyOnce()
    {
        await database.ResetAsync();
        await using var canceller = BuildInstance("canceller");
        await using var sweeper = BuildInstance("sweeper");
        _clock.UtcNow = T0.AddMinutes(5);

        for (var round = 0; round < 10; round++)
        {
            var product = await database.SeedProductAsync(totalQuantity: 10);
            var reservation = await SeedReservationAsync(product, 3, T0);
            // Not due; keeps ReservedQuantity at 4 afterwards, so a double release would show as 1 (or a failure).
            await SeedReservationAsync(product, 4, T0.AddHours(1));

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = Task.Run(async () =>
            {
                await startGate.Task;
                await using var scope = canceller.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IReservationService>().CancelAsync(reservation.Id, default);
                    return true;
                }
                catch (ReservationConcurrencyException) { return false; }
                catch (Domain.Exceptions.InvalidReservationTransitionException) { return false; }
                catch (Domain.Exceptions.ReservationExpiredException) { return false; }
            });
            var expire = Task.Run(async () =>
            {
                await startGate.Task;
                return (await ExpireDueAsync(sweeper)).Expired == 1;
            });

            startGate.SetResult();
            var (cancelled, expired) = (await cancel, await expire);

            Assert.True(cancelled ^ expired, $"Round {round}: exactly one transition must win.");
            Assert.Equal(cancelled ? ReservationStatus.Cancelled : ReservationStatus.Expired,
                (await GetReservationAsync(product.Id, reservation.Id)).Status);
            var stored = await database.GetProductAsync(product.Id);
            Assert.Equal((4, 0, 6), (stored.ReservedQuantity, stored.SoldQuantity, stored.AvailableQuantity));
        }
    }

    [Fact]
    public async Task ExpiredReservation_IsVisibleThroughApi_AndCannotBeConfirmed()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var now = DateTime.UtcNow;
        var reservation = Reservation.Create(product.Id, database.DefaultUser.Id.ToString(), 3, now.AddMinutes(-5));
        await using (var db = database.CreateDbContext())
            await new ReservationRepository(db).ReserveStockAndAddAsync(reservation, default);

        _clock.UtcNow = now;
        await using var instance = BuildInstance();
        Assert.Equal(1, (await ExpireDueAsync(instance)).Expired);

        using var client = database.CreateClient();
        var stock = await (await client.GetAsync($"/api/v1/products/{product.Id}")).Content
            .ReadFromJsonAsync<Application.Products.ProductResponse>();
        Assert.Equal((0, 10), (stock!.ReservedQuantity, stock.AvailableQuantity));

        var confirm = await client.PostAsync($"/api/v1/reservations/{reservation.Id}/confirm", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Contains("RESERVATION_EXPIRED", await confirm.Content.ReadAsStringAsync());
        Assert.Equal(0, (await database.GetProductAsync(product.Id)).SoldQuantity);
    }

    [Fact]
    public async Task Worker_ExpiresDueReservations_AndStopsGracefully()
    {
        await database.ResetAsync();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var reservation = await SeedReservationAsync(product, 3, T0);
        _clock.UtcNow = T0.AddMinutes(5);

        await using var instance = BuildInstance();
        var worker = new ReservationExpiryWorker(
            instance.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReservationExpiryOptions { PollingInterval = TimeSpan.FromMilliseconds(50), BatchSize = 10 }),
            _clock,
            NullLogger<ReservationExpiryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await GetReservationAsync(product.Id, reservation.Id)).Status != ReservationStatus.Expired)
            {
                Assert.True(DateTime.UtcNow < deadline, "Worker did not expire the reservation in time.");
                await Task.Delay(50);
            }
        }
        finally
        {
            var stop = worker.StopAsync(CancellationToken.None);
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
            await stop;
        }

        Assert.Equal(0, (await database.GetProductAsync(product.Id)).ReservedQuantity);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Worker_StopsPromptly_WhileWaitingForNextTick()
    {
        await using var instance = BuildInstance();
        var worker = new ReservationExpiryWorker(
            instance.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReservationExpiryOptions { PollingInterval = TimeSpan.FromHours(1) }),
            _clock,
            NullLogger<ReservationExpiryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200); // let the first sweep finish and the worker park on the timer

        var stop = worker.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        await stop;
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class TestUser : ICurrentUser
    {
        public string UserId => "user-1";

        public bool IsInRole(Inventory.Domain.Enums.UserRole role) => false;
    }
}
