using Testcontainers.MySql;

namespace Inventory.IntegrationTests.Support;

/// <summary>
/// The MySQL server the MySQL-backed tests run against. Uses <c>INVENTORY_TEST_MYSQL</c> when it is set (a connection
/// string whose user may create databases); otherwise starts one throwaway MySQL container for the whole test run.
/// Docker must be running in that case. The container is removed automatically when the test run ends.
/// </summary>
public static class MySqlTestServer
{
    public const string EnvironmentVariable = "INVENTORY_TEST_MYSQL";

    private static readonly Lazy<Task<string>> ConnectionString = new(StartAsync);

    /// <summary>Server connection string without a database name; each fixture adds its own throwaway database.</summary>
    public static Task<string> GetConnectionStringAsync() => ConnectionString.Value;

    private static async Task<string> StartAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // Same image and connection limit as docker-compose.yml, so the 500-request tests run truly in parallel.
        var container = new MySqlBuilder("mysql:8.0")
            .WithUsername("root")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .WithCommand("--max-connections=1000")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The MySQL-backed tests need Docker running, or {EnvironmentVariable} set to a MySQL connection string.", ex);
        }

        return container.GetConnectionString();
    }
}
