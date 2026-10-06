using System.Net;
using System.Net.Http.Json;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using MySqlConnector;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// The full HTTP stack — JWT auth and the production rate limits included — under 500 simultaneous reservations
/// from 500 different users. Rate limiting is per client, so it must not stand between them and the database;
/// the database alone must guarantee no oversell.
/// </summary>
[Collection(MySqlLoadCollection.Name)]
public class ReservationHttpConcurrencyMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>
{
    private const int Users = 500;

    [Fact]
    public async Task FiveHundredUsers_DefaultRateLimits_ExactlyStockSucceeds_NoneThrottled()
    {
        const int stock = 100;
        var product = await database.SeedProductAsync(stock);

        var connectionString = new MySqlConnectionStringBuilder(database.ConnectionString)
        {
            MaximumPoolSize = 250,
            // Correctness test, not a latency test: 500 requests queue on one row lock, and on a busy machine the last ones
            // can wait longer than the 30 s default (see "hot products" in the known limitations).
            DefaultCommandTimeout = 120,
            ConnectionTimeout = 60
        }.ConnectionString;
        await using var factory = new StrictRateLimitApiFactory();
        var api = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString));

        var clients = Enumerable.Range(0, Users).Select(_ => api.CreateClientFor(UserRole.User)).ToArray();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = clients.Select(client => Task.Run(async () =>
        {
            await startGate.Task;
            return await client.PostAsJsonAsync("/api/v1/reservations", new { productId = product.Id, quantity = 1 });
        })).ToArray();

        startGate.SetResult();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(0, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(stock, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(Users - stock, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(stock, stored.ReservedQuantity);
        Assert.Equal(0, stored.AvailableQuantity);
        Assert.Equal(stock, (await database.GetReservationsAsync(product.Id)).Count(r => r.Status == ReservationStatus.Active));
    }
}
