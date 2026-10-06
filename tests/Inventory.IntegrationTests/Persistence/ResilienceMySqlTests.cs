using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Application;
using Inventory.Domain.Enums;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence.Seeding;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Inventory.IntegrationTests.Persistence;

/// <summary>
/// Failure handling against real MySQL: lock-wait timeouts are retried and, if they persist, answered with 503;
/// concurrent startups seeding the same user do not crash. Skipped unless INVENTORY_TEST_MYSQL is set.
/// </summary>
public class ResilienceMySqlTests(MySqlDatabaseFixture database, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<MySqlDatabaseFixture>
{
    /// <summary>The API with a 1-second lock wait timeout, so a held row lock makes MySQL fail fast (error 1205).</summary>
    private WebApplicationFactory<Program> ApiWithShortLockTimeout(InventoryApiFactory factory) =>
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:DefaultConnection", database.ConnectionString)
            .UseSetting("Database:LockWaitTimeoutSeconds", "1"));

    /// <summary>
    /// Holds an exclusive row lock on the product from a separate connection until <paramref name="hold"/> elapses
    /// or <paramref name="release"/> completes, whichever is first.
    /// </summary>
    private async Task HoldProductLockAsync(Guid productId, TimeSpan hold, TaskCompletionSource locked, Task? release = null)
    {
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new MySqlCommand("SELECT Id FROM Products WHERE Id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", productId.ToString());
            await command.ExecuteScalarAsync();
        }
        locked.SetResult();
        await Task.WhenAny(Task.Delay(hold), release ?? Task.Delay(Timeout.Infinite));
        await transaction.RollbackAsync();
    }

    [MySqlFact]
    public async Task LockWaitTimeoutSetting_IsAppliedToEveryConnectionEfUses()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Inventory.Infrastructure.DependencyInjection.ConnectionStringName}"] = database.ConnectionString,
            ["Database:LockWaitTimeoutSeconds"] = "1",
        }).Build();
        var services = new ServiceCollection();
        services.AddApplication().AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();

        for (var i = 0; i < 3; i++) // fresh scopes reuse pooled connections, which MySqlConnector resets
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Inventory.Infrastructure.Persistence.AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var value = await db.Database.SqlQueryRaw<int>("SELECT @@SESSION.innodb_lock_wait_timeout AS `Value`").SingleAsync();
            Assert.Equal(1, value);
        }
    }

    [MySqlFact]
    public async Task LockWaitTimeout_IsRetried_AndTheReservationSucceeds()
    {
        var product = await database.SeedProductAsync(totalQuantity: 5);
        await using var factory = new InventoryApiFactory();
        using var client = ApiWithShortLockTimeout(factory).CreateClientFor(UserRole.User);
        // Warm up first: a cold host spends most of a second before its UPDATE reaches MySQL, which would shorten
        // the actual lock wait below the timeout and let the first attempt succeed without exercising the retry.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/products/{product.Id}")).StatusCode);

        var locked = new TaskCompletionSource();
        // Held 1.6 s against a 1 s timeout: the first attempt must time out, so only a retry can succeed.
        var holder = HoldProductLockAsync(product.Id, TimeSpan.FromMilliseconds(1600), locked);
        await locked.Task;

        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/v1/reservations", new { productId = product.Id, quantity = 2 });
        stopwatch.Stop();
        output.WriteLine($"DIAG status={(int)response.StatusCode} elapsed={stopwatch.ElapsedMilliseconds}ms");
        await holder;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(1), $"Expected to wait for the lock, took {stopwatch.Elapsed}.");
        Assert.Equal(2, (await database.GetProductAsync(product.Id)).ReservedQuantity);
    }

    [MySqlFact]
    public async Task PersistentLockContention_Returns503_AndChangesNothing()
    {
        var product = await database.SeedProductAsync(totalQuantity: 5);
        await using var factory = new InventoryApiFactory();
        using var client = ApiWithShortLockTimeout(factory).CreateClientFor(UserRole.User);

        var locked = new TaskCompletionSource();
        var responded = new TaskCompletionSource();
        // Held until the API has answered (cap 20 s): every attempt must time out. MySQL checks lock waits coarsely,
        // so a 1 s timeout can take ~2.5 s per attempt; a fixed hold could end before the last retry.
        var holder = HoldProductLockAsync(product.Id, TimeSpan.FromSeconds(20), locked, responded.Task);
        await locked.Task;

        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/v1/reservations", new { productId = product.Id, quantity = 2 });
        stopwatch.Stop();
        responded.SetResult();
        await holder;

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 503 after {stopwatch.Elapsed}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TEMPORARILY_UNAVAILABLE", body.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("Lock wait timeout", body.RootElement.GetRawText()); // no driver internals leak

        var stored = await database.GetProductAsync(product.Id);
        Assert.Equal(0, stored.ReservedQuantity);
        Assert.Empty(await database.GetReservationsAsync(product.Id));
    }

    [MySqlFact]
    public async Task ConcurrentStartups_SeedingTheSameUser_DoNotFail()
    {
        var username = $"seed-race-{Guid.NewGuid():N}"[..20];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Inventory.Infrastructure.DependencyInjection.ConnectionStringName}"] = database.ConnectionString,
            ["SeedUsers:0:Username"] = username,
            ["SeedUsers:0:Password"] = "Race-Passw0rd!",
            ["SeedUsers:0:Role"] = "Admin",
        }).Build();

        // Two "instances" with their own containers. Password hashing (~0.3 s) sits between the existence check and
        // the insert, so both see the user missing and race to insert it.
        var instances = Enumerable.Range(0, 2).Select(_ =>
        {
            var services = new ServiceCollection();
            services.AddApplication().AddInfrastructure(configuration);
            services.AddLogging();
            return services.BuildServiceProvider();
        }).ToArray();

        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seeding = instances.Select(sp => Task.Run(async () =>
            {
                await gate.Task;
                await sp.SeedConfiguredUsersAsync(configuration);
            })).ToArray();
            gate.SetResult();

            await Task.WhenAll(seeding); // neither instance may crash
        }
        finally
        {
            foreach (var sp in instances)
                await sp.DisposeAsync();
        }

        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Username == username));
    }
}
