using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetryScope.Model;

/// <summary>
/// The one place IR JSON is read and written. Output is canonical (stable list order, "\n" line endings,
/// camelCase names, enums as strings) so that the same system yields byte-identical files on every OS.
/// </summary>
public static class ResilienceGraphJson
{
    /// <summary>Serializer options shared by every RetryScope component.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Writes the graph as canonical JSON.</summary>
    public static string Serialize(ResilienceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph.Canonicalize(), Options) + "\n";
    }

    /// <summary>Reads a graph and rejects unknown schema versions.</summary>
    public static ResilienceGraph Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var graph = JsonSerializer.Deserialize<ResilienceGraph>(json, Options)
            ?? throw new JsonException("The document is empty or null.");

        if (graph.SchemaVersion != ResilienceGraph.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported IR schema version '{graph.SchemaVersion}'; this build reads {ResilienceGraph.CurrentSchemaVersion}.");
        }

        return graph;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NewLine = "\n",
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
