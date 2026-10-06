using System.Net;
using System.Net.Http.Json;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Persistence.Repositories;
using Inventory.IntegrationTests.Support;

namespace Inventory.IntegrationTests.Reservations;

/// <summary>
/// Confirm/cancel through the real API, EF Core and MySQL, including concurrent races.
/// Skipped unless INVENTORY_TEST_MYSQL is set.
/// </summary>
public class ReservationLifecycleMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>
{
    private const string BaseUrl = "/api/v1/reservations";
    private const int RacingRequests = 20;

    private static Task<HttpResponseMessage> Confirm(HttpClient client, Guid id) =>
        client.PostAsync($"{BaseUrl}/{id}/confirm", content: null);

    private static Task<HttpResponseMessage> Cancel(HttpClient client, Guid id) =>
        client.PostAsync($"{BaseUrl}/{id}/cancel", content: null);

    /// <summary>Seeds a product with <paramref name="totalQuantity"/> and reserves <paramref name="quantity"/> of it.</summary>
    private async Task<(Product Product, ReservationResponse Reservation)> SeedActiveReservationAsync(
        HttpClient client, int totalQuantity = 10, int quantity = 3)
    {
        var product = await database.SeedProductAsync(totalQuantity);
        var response = await client.PostAsJsonAsync(BaseUrl, new { productId = product.Id, quantity });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (product, (await response.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options))!);
    }

    private async Task AssertStockAsync(Guid productId, int reserved, int sold)
    {
        var product = await database.GetProductAsync(productId);
        Assert.Equal(reserved, product.ReservedQuantity);
        Assert.Equal(sold, product.SoldQuantity);
        Assert.Equal(product.TotalQuantity - reserved - sold, product.AvailableQuantity);
    }

    private async Task<Reservation> GetStoredReservationAsync(Guid productId) =>
        Assert.Single(await database.GetReservationsAsync(productId));

    /// <summary>Releases all requests at once so they genuinely race.</summary>
    private static async Task<HttpResponseMessage[]> RaceAsync(IEnumerable<Func<Task<HttpResponseMessage>>> requests)
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = requests.Select(send => Task.Run(async () =>
        {
            await startGate.Task;
            return await send();
        })).ToArray();

        startGate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [MySqlFact]
    public async Task Confirm_Active_Succeeds()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);

        var response = await Confirm(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options);
        Assert.Equal(ReservationStatus.Confirmed, body!.Status);
        Assert.NotNull(body.ConfirmedAtUtc);
        Assert.Null(body.CancelledAtUtc);

        await AssertStockAsync(product.Id, reserved: 0, sold: 3);
        var stored = await GetStoredReservationAsync(product.Id);
        Assert.Equal(ReservationStatus.Confirmed, stored.Status);
        Assert.Equal(body.ConfirmedAtUtc, stored.ConfirmedAtUtc);
    }

    [MySqlFact]
    public async Task Confirm_Twice_DoesNotDoubleSell()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, quantity: 3);

        Assert.Equal(HttpStatusCode.OK, (await Confirm(client, reservation.ReservationId)).StatusCode);
        var second = await Confirm(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertStockAsync(product.Id, reserved: 0, sold: 3);
    }

    [MySqlFact]
    public async Task Cancel_Active_ReleasesInventory()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);
        await AssertStockAsync(product.Id, reserved: 3, sold: 0);

        var response = await Cancel(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options);
        Assert.Equal(ReservationStatus.Cancelled, body!.Status);
        Assert.NotNull(body.CancelledAtUtc);
        Assert.Null(body.ConfirmedAtUtc);

        await AssertStockAsync(product.Id, reserved: 0, sold: 0);
        Assert.Equal(10, (await database.GetProductAsync(product.Id)).AvailableQuantity);
        Assert.Equal(ReservationStatus.Cancelled, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task Cancel_Twice_DoesNotDoubleRelease()
    {
        using var client = database.CreateClient();
        // A second reservation keeps ReservedQuantity > 0, so a double release would be visible rather than
        // blocked by the non-negative guard.
        var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);
        await client.PostAsJsonAsync(BaseUrl, new { productId = product.Id, quantity = 4 });

        Assert.Equal(HttpStatusCode.OK, (await Cancel(client, reservation.ReservationId)).StatusCode);
        var second = await Cancel(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertStockAsync(product.Id, reserved: 4, sold: 0);
    }

    [MySqlFact]
    public async Task Confirm_Cancelled_Fails()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, quantity: 3);
        await Cancel(client, reservation.ReservationId);

        var response = await Confirm(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertStockAsync(product.Id, reserved: 0, sold: 0);
        Assert.Equal(ReservationStatus.Cancelled, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task Cancel_Confirmed_Fails()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, quantity: 3);
        await Confirm(client, reservation.ReservationId);

        var response = await Cancel(client, reservation.ReservationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertStockAsync(product.Id, reserved: 0, sold: 3);
        Assert.Equal(ReservationStatus.Confirmed, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task Confirm_Expired_FailsAndKeepsStockReserved()
    {
        using var client = database.CreateClient();
        var product = await database.SeedProductAsync(totalQuantity: 10);
        var expired = Reservation.Create(product.Id, database.DefaultUser.Id.ToString(), 2, DateTime.UtcNow.AddMinutes(-5));
        await using (var db = database.CreateDbContext())
            await new ReservationRepository(db).ReserveStockAndAddAsync(expired, default);

        var response = await Confirm(client, expired.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertStockAsync(product.Id, reserved: 2, sold: 0);
        Assert.Equal(ReservationStatus.Active, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task ConfirmAndCancel_UnknownReservation_Returns404()
    {
        using var client = database.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await Confirm(client, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cancel(client, Guid.NewGuid())).StatusCode);
    }

    [MySqlFact]
    public async Task ConfirmAndCancel_OtherUsersReservation_Returns403AndChangesNothing()
    {
        using var owner = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(owner, quantity: 3);
        using var intruder = database.CreateClient(TestUsers.Create(UserRole.User));

        Assert.Equal(HttpStatusCode.Forbidden, (await Confirm(intruder, reservation.ReservationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Cancel(intruder, reservation.ReservationId)).StatusCode);

        await AssertStockAsync(product.Id, reserved: 3, sold: 0);
        Assert.Equal(ReservationStatus.Active, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task Admin_CanCancelAnyUsersReservation()
    {
        using var owner = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(owner, quantity: 3);
        using var admin = database.CreateClient(database.AdminUser);

        Assert.Equal(HttpStatusCode.OK, (await Cancel(admin, reservation.ReservationId)).StatusCode);
        await AssertStockAsync(product.Id, reserved: 0, sold: 0);
    }

    [MySqlFact]
    public async Task ConcurrentConfirm_OnlyOneSucceeds()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);

        var responses = await RaceAsync(
            Enumerable.Range(0, RacingRequests).Select(_ => (Func<Task<HttpResponseMessage>>)(() => Confirm(client, reservation.ReservationId))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(RacingRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await AssertStockAsync(product.Id, reserved: 0, sold: 3);
        Assert.Equal(ReservationStatus.Confirmed, (await GetStoredReservationAsync(product.Id)).Status);
    }

    [MySqlFact]
    public async Task ConcurrentCancel_OnlyOneSucceeds()
    {
        using var client = database.CreateClient();
        var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);
        await client.PostAsJsonAsync(BaseUrl, new { productId = product.Id, quantity = 4 });

        var responses = await RaceAsync(
            Enumerable.Range(0, RacingRequests).Select(_ => (Func<Task<HttpResponseMessage>>)(() => Cancel(client, reservation.ReservationId))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(RacingRequests - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await AssertStockAsync(product.Id, reserved: 4, sold: 0);
    }

    [MySqlFact]
    public async Task ConcurrentConfirmVsCancel_LeavesConsistentState()
    {
        using var client = database.CreateClient();
        var winners = new Dictionary<ReservationStatus, int>();

        // Repeated so both orderings get exercised.
        for (var round = 0; round < 10; round++)
        {
            var (product, reservation) = await SeedActiveReservationAsync(client, totalQuantity: 10, quantity: 3);
            var id = reservation.ReservationId;

            var requests = Enumerable.Range(0, RacingRequests).Select(i =>
                (Func<Task<HttpResponseMessage>>)(i % 2 == 0 ? () => Confirm(client, id) : () => Cancel(client, id)));
            var responses = await RaceAsync(requests);

            var succeeded = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.All(responses.Where(r => r != succeeded), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

            var winner = (await succeeded.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options))!.Status;
            var stored = await GetStoredReservationAsync(product.Id);
            Assert.Equal(winner, stored.Status);

            if (winner == ReservationStatus.Confirmed)
            {
                await AssertStockAsync(product.Id, reserved: 0, sold: 3);
                Assert.NotNull(stored.ConfirmedAtUtc);
                Assert.Null(stored.CancelledAtUtc);
            }
            else
            {
                Assert.Equal(ReservationStatus.Cancelled, winner);
                await AssertStockAsync(product.Id, reserved: 0, sold: 0);
                Assert.NotNull(stored.CancelledAtUtc);
                Assert.Null(stored.ConfirmedAtUtc);
            }

            winners[winner] = winners.GetValueOrDefault(winner) + 1;
        }

        Assert.Equal(10, winners.Values.Sum());
    }

    [MySqlFact]
    public async Task ConcurrentConfirm_DifferentReservationsOfSameProduct_NoLostUpdates()
    {
        using var client = database.CreateClient();
        var product = await database.SeedProductAsync(totalQuantity: 50);
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var created = await client.PostAsJsonAsync(BaseUrl, new { productId = product.Id, quantity = 2 });
            ids.Add((await created.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options))!.ReservationId);
        }

        var responses = await RaceAsync(ids.Select(id => (Func<Task<HttpResponseMessage>>)(() => Confirm(client, id))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await AssertStockAsync(product.Id, reserved: 0, sold: 50);
    }
}
