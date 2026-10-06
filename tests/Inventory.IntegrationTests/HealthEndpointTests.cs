using System.Net;
using System.Net.Http.Json;
using Inventory.Api.BackgroundJobs;
using Inventory.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inventory.IntegrationTests;

public class HealthEndpointTests(InventoryApiFactory factory)
    : IClassFixture<InventoryApiFactory>
{
    [Fact]
    public async Task GetHealth_ReturnsOk()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task Readiness_WithoutDatabase_Returns503_WithoutLeakingDetails()
    {
        // This factory has no connection string, so the database check cannot succeed.
        var response = await factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", body);
    }

    [Fact]
    public async Task Liveness_StaysUp_WhenDatabaseIsUnavailable()
    {
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void ExpiryWorker_IsRegistered_ButDisabledForApiTests()
    {
        Assert.Contains(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is ReservationExpiryWorker);
        Assert.False(factory.Services.GetRequiredService<IOptions<ReservationExpiryOptions>>().Value.Enabled);
    }
}
