namespace Inventory.IntegrationTests.Support;

/// <summary>
/// The 500-way load tests. Each still fires all of its requests simultaneously, but xUnit runs these classes
/// on their own, after the parallel tests, so two ~500-connection bursts (plus the rest of the suite) never
/// compete for the same MySQL server and turn a correctness test into a measure of the machine's capacity.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MySqlLoadCollection
{
    public const string Name = "MySQL load tests";
}
