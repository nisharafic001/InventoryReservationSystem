using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inventory.IntegrationTests.Support;

/// <summary>Matches the API's JSON settings (web defaults + enums as strings).</summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
