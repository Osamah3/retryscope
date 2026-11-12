using System.Text.Json;
using RetryScope.Model;

namespace RetryScope.Model.Tests;

public class JsonTests
{
    [Fact]
    public void Round_trip_preserves_the_graph()
    {
        var graph = TestData.Graph(
            TestData.Binding("b1", TestData.StandardHandler(), TestData.StandardHandler(HandlerOrigin.PerClient)),
            TestData.Binding("b2"));

        var json = ResilienceGraphJson.Serialize(graph);
        var reread = ResilienceGraphJson.Deserialize(json);

        // Records holding lists compare lists by reference, so compare canonical JSON instead.
        Assert.Equal(json, ResilienceGraphJson.Serialize(reread));
        Assert.Equal(16, EffectiveConfiguration.MaxAttemptsPerCall(reread.Bindings.Single(b => b.Id == "b1")).MaxAttempts);
    }

    [Fact]
    public void Output_is_canonical_regardless_of_input_order()
    {
        var a = TestData.Graph(TestData.Binding("b1"), TestData.Binding("b2"));
        var b = a with { Bindings = a.Bindings.Reverse().ToList(), Services = a.Services.Reverse().ToList() };

        Assert.Equal(ResilienceGraphJson.Serialize(a), ResilienceGraphJson.Serialize(b));
    }

    [Fact]
    public void Output_uses_readable_enums_discriminators_and_unix_newlines()
    {
        var json = ResilienceGraphJson.Serialize(TestData.Graph(TestData.Binding("b1", TestData.StandardHandler())));

        Assert.Contains("\"kind\": \"retry\"", json, StringComparison.Ordinal);
        Assert.Contains("\"effective\": \"exponential\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provenance\": \"packageDefault\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isAssumption", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        var json = ResilienceGraphJson.Serialize(TestData.Graph())
            .Replace(ResilienceGraph.CurrentSchemaVersion, "9.9.9", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ResilienceGraphJson.Deserialize(json));
    }

    [Fact]
    public void Value_provenance_survives_round_trip()
    {
        var retry = TestData.DefaultRetry(Values.Unresolved(3, "Environment.GetEnvironmentVariable(\"RETRIES\")",
            new SourceLocation("src/Api/Program.cs", 42, 9)));
        var graph = TestData.Graph(TestData.Binding("b1", TestData.StandardHandler(HandlerOrigin.PerClient, retry)));

        var reread = ResilienceGraphJson.Deserialize(ResilienceGraphJson.Serialize(graph));
        var value = reread.Bindings[0].Handlers[0].Strategies.OfType<RetryStrategy>().Single().MaxRetryAttempts;

        Assert.Equal(Provenance.Unresolved, value.Provenance);
        Assert.Equal(42, value.Location?.Line);
        Assert.True(value.IsAssumption);
    }
}
