using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Inventory.Application.Abstractions.Authentication;
using Inventory.Application.Abstractions.Persistence;
using Inventory.Application.Auth;
using Inventory.Application.Products;
using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.IntegrationTests.Auth;

/// <summary>
/// Login, token validation and role-based authorization over HTTP. Persistence is in-memory; hashing and JWT are real.
/// </summary>
public class AuthApiTests : IClassFixture<InventoryApiFactory>
{
    private const string LoginUrl = "/api/v1/auth/login";
    private const string ProductsUrl = "/api/v1/products";
    private const string AdminPassword = "Adm1n-Passw0rd!";
    private const string UserPassword = "Us3r-Passw0rd!";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly InventoryApiFactory _baseFactory;

    public AuthApiTests(InventoryApiFactory factory)
    {
        _baseFactory = factory;
        var users = new InMemoryUserRepository();
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IUserRepository>(users);
            services.AddSingleton<IProductRepository, InMemoryProductRepository>();
        }));

        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        users.AddAsync(User.Create("admin", hasher.Hash(AdminPassword), UserRole.Admin), default).Wait();
        users.AddAsync(User.Create("alice", hasher.Hash(UserPassword), UserRole.User), default).Wait();
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(LoginUrl, new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object NewProduct() => new { sku = $"SKU-{Guid.NewGuid():N}"[..20], name = "Widget", totalQuantity = 5 };

    [Fact]
    public async Task ValidLogin_ReturnsJwt()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(LoginUrl, new { username = "alice", password = UserPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.Equal("Bearer", body.TokenType);
        Assert.InRange(body.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(61));

        var jwt = new JsonWebToken(body.AccessToken);
        Assert.Equal("inventory-api", jwt.Issuer);
        Assert.Equal(["inventory-clients"], jwt.Audiences);
        Assert.True(Guid.TryParse(jwt.Subject, out _));
        Assert.Equal("alice", jwt.GetClaim("username").Value);
        Assert.Equal("User", jwt.GetClaim("role").Value);
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Alg);
    }

    [Fact]
    public async Task InvalidPassword_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(LoginUrl, new { username = "alice", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid username or password.", body);
        Assert.DoesNotContain("accessToken", body);
    }

    [Fact]
    public async Task UnknownUser_Returns401_IndistinguishableFromWrongPassword()
    {
        var unknown = await _factory.CreateClient().PostAsJsonAsync(LoginUrl, new { username = "nobody", password = "wrong" });
        var wrongPassword = await _factory.CreateClient().PostAsJsonAsync(LoginUrl, new { username = "alice", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            (await wrongPassword.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Detail,
            (await unknown.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Detail);
    }

    [Theory]
    [InlineData("""{ "password": "x" }""")]
    [InlineData("""{ "username": "alice" }""")]
    public async Task Login_MissingField_Returns400(string body)
    {
        var response = await _factory.CreateClient().PostAsync(LoginUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/v1/products/3f2b8f8e-0000-4000-8000-000000000001")]
    [InlineData("POST", "/api/v1/products")]
    [InlineData("POST", "/api/v1/reservations")]
    [InlineData("POST", "/api/v1/reservations/3f2b8f8e-0000-4000-8000-000000000001/confirm")]
    [InlineData("POST", "/api/v1/reservations/3f2b8f8e-0000-4000-8000-000000000001/cancel")]
    public async Task MissingToken_Returns401(string method, string url)
    {
        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = JsonContent.Create(new { })
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidToken_AllowsAccess()
    {
        var adminToken = await LoginAsync("admin", AdminPassword);
        var created = await (await ClientWithToken(adminToken).PostAsJsonAsync(ProductsUrl, NewProduct()))
            .Content.ReadFromJsonAsync<ProductResponse>();

        var userToken = await LoginAsync("alice", UserPassword);
        var response = await ClientWithToken(userToken).GetAsync($"{ProductsUrl}/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NormalUser_AdminEndpoint_Returns403()
    {
        var userToken = await LoginAsync("alice", UserPassword);

        var response = await ClientWithToken(userToken).PostAsJsonAsync(ProductsUrl, NewProduct());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_AdminEndpoint_Succeeds()
    {
        var adminToken = await LoginAsync("admin", AdminPassword);

        var response = await ClientWithToken(adminToken).PostAsJsonAsync(ProductsUrl, NewProduct());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithOtherKey_Returns401()
    {
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "inventory-api",
            Audience = "inventory-clients",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = "Admin" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(48)), SecurityAlgorithms.HmacSha256)
        });

        var response = await ClientWithToken(forged).PostAsJsonAsync(ProductsUrl, NewProduct());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("inventory-api", "some-other-api")]
    [InlineData("someone-else", "inventory-clients")]
    public async Task TokenForWrongIssuerOrAudience_Returns401(string issuer, string audience)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = "Admin" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_baseFactory.SigningKey)), SecurityAlgorithms.HmacSha256)
        });

        var response = await ClientWithToken(token).GetAsync($"{ProductsUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var expired = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "inventory-api",
            Audience = "inventory-clients",
            IssuedAt = DateTime.UtcNow.AddHours(-2),
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = "Admin" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_baseFactory.SigningKey)), SecurityAlgorithms.HmacSha256)
        });

        var response = await ClientWithToken(expired).GetAsync($"{ProductsUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health")).StatusCode);
    }
}
