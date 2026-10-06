using System.Collections.Concurrent;
using System.Diagnostics;
using Inventory.Application;
using Inventory.Application.Abstractions;
using Inventory.Application.Reservations;
using Inventory.Domain.Enums;
using Inventory.Infrastructure;
using Inventory.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit.Abstractions;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// Fires hundreds of reservation attempts at one product at the same moment against real MySQL.
/// Each attempt runs in its own DI scope (own DbContext and pooled connection), and attempts are spread across
/// two independently built service providers with separate connection pools, standing in for two API instances.
/// </summary>
[Collection(MySqlLoadCollection.Name)]
public class ReservationConcurrencyTests(MySqlDatabaseFixture database, ITestOutputHelper output)
    : IClassFixture<MySqlDatabaseFixture>
{
    private const int ApiInstances = 2;
    private const int ConcurrentRequests = 500;

    [Fact]
    public Task SingleUnit_500ConcurrentRequests_ExactlyOneSucceeds() =>
        AssertNoOversell(totalQuantity: 1, expectedSuccesses: 1);

    [Fact]
    public Task HundredUnits_500ConcurrentRequests_ExactlyHundredSucceed() =>
        AssertNoOversell(totalQuantity: 100, expectedSuccesses: 100);

    private async Task AssertNoOversell(int totalQuantity, int expectedSuccesses)
    {
        var product = await database.SeedProductAsync(totalQuantity);
        Assert.Equal(0, product.ReservedQuantity);
        Assert.Equal(0, product.SoldQuantity);

        var poolSize = await PoolSizePerInstanceAsync();
        var instances = Enumerable.Range(0, ApiInstances).Select(i => BuildApiInstance(i, poolSize)).ToArray();
        try
        {
            var result = await RunConcurrentlyAsync(instances, product.Id);

            output.WriteLine(
                $"stock={totalQuantity} requests={ConcurrentRequests} succeeded={result.Succeeded} " +
                $"insufficient={result.InsufficientStock} other={result.Unexpected.Count} " +
                $"peakInFlight={result.PeakInFlight} peakDbConnections={result.PeakDbConnections} " +
                $"poolPerInstance={poolSize} elapsed={result.Elapsed.TotalMilliseconds:F0}ms");

            Assert.Empty(result.Unexpected);
            Assert.Equal(expectedSuccesses, result.Succeeded);
            Assert.Equal(ConcurrentRequests - expectedSuccesses, result.InsufficientStock);

            // Guards against the test silently degrading into sequential execution.
            Assert.True(result.PeakDbConnections > 1, "Requests were not executed concurrently.");
        }
        finally
        {
            foreach (var instance in instances)
                await instance.DisposeAsync();
        }

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(totalQuantity, stored.TotalQuantity);
        Assert.Equal(expectedSuccesses, stored.ReservedQuantity);
        Assert.Equal(0, stored.SoldQuantity);
        Assert.Equal(totalQuantity - expectedSuccesses, stored.AvailableQuantity);

        var reservations = await database.GetReservationsAsync(product.Id);
        Assert.Equal(expectedSuccesses, reservations.Count(r => r.Status == ReservationStatus.Active));
        Assert.Equal(expectedSuccesses, reservations.Count);
        Assert.Equal(expectedSuccesses, reservations.Sum(r => r.Quantity));
    }

    private async Task<RunResult> RunConcurrentlyAsync(ServiceProvider[] instances, Guid productId)
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new ConcurrentBag<Exception>();
        int succeeded = 0, insufficient = 0, inFlight = 0, peakInFlight = 0;

        var attempts = Enumerable.Range(0, ConcurrentRequests).Select(i => Task.Run(async () =>
        {
            await startGate.Task;

            // A fresh scope per request, exactly like ASP.NET Core does per HTTP request.
            await using var scope = instances[i % instances.Length].CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IReservationService>();

            var current = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peakInFlight, current);
            try
            {
                await service.CreateAsync(new CreateReservationRequest(productId, 1), CancellationToken.None);
                Interlocked.Increment(ref succeeded);
            }
            catch (InsufficientStockException)
            {
                Interlocked.Increment(ref insufficient);
            }
            catch (Exception ex)
            {
                unexpected.Add(ex);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        })).ToArray();

        using var sampler = new CancellationTokenSource();
        var connectionSampling = SamplePeakDbConnectionsAsync(sampler.Token);

        var stopwatch = Stopwatch.StartNew();
        startGate.SetResult();
        await Task.WhenAll(attempts);
        stopwatch.Stop();

        await sampler.CancelAsync();
        var peakDbConnections = await connectionSampling;

        foreach (var ex in unexpected.Take(3))
            output.WriteLine($"Unexpected: {ex.GetType().Name}: {ex.Message}");

        return new RunResult(succeeded, insufficient, [.. unexpected], peakInFlight, peakDbConnections, stopwatch.Elapsed);
    }

    /// <summary>Peak number of server-side connections to the test database while the attempts run.</summary>
    private async Task<int> SamplePeakDbConnectionsAsync(CancellationToken cancellationToken)
    {
        var databaseName = new MySqlConnectionStringBuilder(database.ConnectionString).Database;
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE DB = @db", connection);
        command.Parameters.AddWithValue("@db", databaseName);

        var peak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            peak = Math.Max(peak, Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None)) - 1);
            try { await Task.Delay(5, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        return peak;
    }

    /// <summary>Connections each simulated instance may open, kept within the server's max_connections.</summary>
    private async Task<int> PoolSizePerInstanceAsync()
    {
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT @@max_connections", connection);
        var maxConnections = Convert.ToInt32(await command.ExecuteScalarAsync());

        // Leave headroom for the sampler, the fixture and other sessions.
        return Math.Clamp((maxConnections - 30) / ApiInstances, 10, ConcurrentRequests / ApiInstances);
    }

    private ServiceProvider BuildApiInstance(int index, int poolSize)
    {
        var connectionString = new MySqlConnectionStringBuilder(database.ConnectionString)
        {
            MaximumPoolSize = (uint)poolSize,
            // Distinct application names keep each instance's connection pool separate.
            ApplicationName = $"inventory-api-{index}",
            // Correctness test, not a latency test: 500 requests queue on one row lock, and on a busy machine the last ones
            // can wait longer than the 30 s default (see "hot products" in the known limitations).
            DefaultCommandTimeout = 120,
            ConnectionTimeout = 60
        }.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{Inventory.Infrastructure.DependencyInjection.ConnectionStringName}"] = connectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddApplication().AddInfrastructure(configuration);
        services.AddScoped<ICurrentUser>(_ => new TestUser($"load-user-{index}"));
        services.AddLogging();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private sealed record RunResult(
        int Succeeded,
        int InsufficientStock,
        IReadOnlyList<Exception> Unexpected,
        int PeakInFlight,
        int PeakDbConnections,
        TimeSpan Elapsed);

    private sealed class TestUser(string userId) : ICurrentUser
    {
        public string UserId => userId;

        public bool IsInRole(Inventory.Domain.Enums.UserRole role) => false;
    }
}
