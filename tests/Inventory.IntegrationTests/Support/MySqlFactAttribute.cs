namespace Inventory.IntegrationTests.Support;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless a MySQL test server is configured via
/// the <c>INVENTORY_TEST_MYSQL</c> environment variable (a connection string whose user may create databases).
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "INVENTORY_TEST_MYSQL";

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"Set {EnvironmentVariable} to run MySQL-backed tests.";
    }
}
