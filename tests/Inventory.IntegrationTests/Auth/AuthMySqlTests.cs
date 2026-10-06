using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Inventory.Application.Auth;
using Inventory.Application.Products;
using Inventory.Application.Reservations;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inventory.IntegrationTests.Auth;

/// <summary>
/// Users, the startup seeder and login against real MySQL, then the issued tokens used end to end.
/// Skipped unless INVENTORY_TEST_MYSQL is set.
/// </summary>
public sealed class AuthMySqlTests(MySqlDatabaseFixture database) : IClassFixture<MySqlDatabaseFixture>, IAsyncLifetime
{
    // One set per test class: the class shares one database, and the seeder (correctly) never overwrites existing users.
    private static readonly string AdminPassword = "A-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static readonly string UserPassword = "U-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private InventoryApiFactory? _baseFactory;
    private WebApplicationFactory<Program>? _api;

    /// <summary>Starts the real API with bootstrap users supplied through configuration, as an operator would.</summary>
    private WebApplicationFactory<Program> StartApi()
    {
        _baseFactory ??= new InventoryApiFactory();
        return _baseFactory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:DefaultConnection", database.ConnectionString)
            .UseSetting("SeedUsers:0:Username", "db-admin")
            .UseSetting("SeedUsers:0:Password", AdminPassword)
            .UseSetting("SeedUsers:0:Role", "Admin")
            .UseSetting("SeedUsers:1:Username", "db-user")
            .UseSetting("SeedUsers:1:Password", UserPassword)
            .UseSetting("SeedUsers:1:Role", "User"));
    }

    public Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            _api = StartApi();
            _ = _api.Server; // forces startup, which runs the seeder
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_baseFactory is not null)
            await _baseFactory.DisposeAsync();
    }

    private async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await _api!.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username, password });

    private async Task<HttpClient> ClientForAsync(string username, string password)
    {
        var response = await LoginAsync(username, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var client = _api!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [MySqlFact]
    public async Task SeededUsers_AreStoredWithHashedPasswords()
    {
        await using var db = database.CreateDbContext();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();

        Assert.Equal(["db-admin", "db-user"], users.Select(u => u.Username));
        Assert.Equal([UserRole.Admin, UserRole.User], users.Select(u => u.Role));
        Assert.All(users, u => Assert.StartsWith("PBKDF2-SHA256.", u.PasswordHash));
        Assert.DoesNotContain(users, u => u.PasswordHash.Contains(AdminPassword) || u.PasswordHash.Contains(UserPassword));
    }

    [MySqlFact]
    public async Task Seeder_IsIdempotent_AcrossRestarts()
    {
        await using var secondStart = new InventoryApiFactory();
        _ = secondStart.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:DefaultConnection", database.ConnectionString)
            .UseSetting("SeedUsers:0:Username", "db-admin")
            .UseSetting("SeedUsers:0:Password", "a-different-password")
            .UseSetting("SeedUsers:0:Role", "Admin")).Server;

        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Username == "db-admin"));
        // The existing account was left untouched: the original password still works.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync("db-admin", AdminPassword)).StatusCode);
    }

    [MySqlFact]
    public async Task Login_AgainstMySql_ReturnsJwt_AndRejectsWrongPassword()
    {
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync("db-user", UserPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("db-user", AdminPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("missing-user", UserPassword)).StatusCode);
    }

    [MySqlFact]
    public async Task Login_UsernameIsCaseInsensitive()
    {
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync("DB-USER", UserPassword)).StatusCode);
    }

    [MySqlFact]
    public async Task Username_IsUniqueIgnoringCase_InTheDatabase()
    {
        await using var db = database.CreateDbContext();
        db.Users.Add(User.Create("DB-Admin", "PBKDF2-SHA256.1.AA==.AA==", UserRole.User));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task IssuedTokens_EnforceRoles_AndOwnership_EndToEnd()
    {
        var admin = await ClientForAsync("db-admin", AdminPassword);
        var user = await ClientForAsync("db-user", UserPassword);

        var product = new { sku = $"AUTH-{Guid.NewGuid():N}"[..20], name = "E2E", totalQuantity = 3 };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/products", product)).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/v1/products", product);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var productId = (await created.Content.ReadFromJsonAsync<ProductResponse>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/v1/products/{productId}")).StatusCode);

        var reserved = await user.PostAsJsonAsync("/api/v1/reservations", new { productId, quantity = 2 });
        Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
        var reservation = (await reserved.Content.ReadFromJsonAsync<ReservationResponse>(TestJson.Options))!;

        await using (var db = database.CreateDbContext())
        {
            var dbUser = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "db-user");
            var stored = await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == reservation.ReservationId);
            Assert.Equal(dbUser.Id.ToString(), stored.UserId); // owner taken from the token's sub claim
        }

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/v1/reservations/{reservation.ReservationId}/confirm", null)).StatusCode);
        var stock = (await (await user.GetAsync($"/api/v1/products/{productId}")).Content.ReadFromJsonAsync<ProductResponse>())!;
        Assert.Equal((0, 2, 1), (stock.ReservedQuantity, stock.SoldQuantity, stock.AvailableQuantity));
    }
}
