using RetryScope.Model;

namespace RetryScope.Model.Tests;

public class ValidatorTests
{
    [Fact]
    public void Default_standard_handler_graph_is_valid()
    {
        var graph = TestData.Graph(TestData.Binding("b1", TestData.StandardHandler()));

        Assert.Empty(ResilienceGraphValidator.Validate(graph));
    }

    [Fact]
    public void Attempt_timeout_above_total_timeout_is_reported()
    {
        var handler = TestData.StandardHandler(HandlerOrigin.PerClient);
        var strategies = handler.Strategies
            .Select(s => s is TimeoutStrategy { Role: TimeoutRole.Attempt } t
                ? t with { Timeout = Values.Explicit(TimeSpan.FromSeconds(40)) }
                : s)
            .ToList();
        var graph = TestData.Graph(TestData.Binding("b1", handler with { Strategies = strategies }));

        var issues = ResilienceGraphValidator.Validate(graph);

        Assert.Contains(issues, i => i.Code == DiagnosticCodes.InvalidStandardOptions && i.Message.Contains("exceeds total", StringComparison.Ordinal));
    }

    [Fact]
    public void Sampling_duration_below_twice_attempt_timeout_is_reported()
    {
        var handler = TestData.StandardHandler(HandlerOrigin.PerClient);
        var strategies = handler.Strategies
            .Select(s => s is CircuitBreakerStrategy cb
                ? cb with { SamplingDuration = Values.Explicit(TimeSpan.FromSeconds(15)) }
                : s)
            .ToList();
        var graph = TestData.Graph(TestData.Binding("b1", handler with { Strategies = strategies }));

        var issues = ResilienceGraphValidator.Validate(graph);

        Assert.Contains(issues, i => i.Code == DiagnosticCodes.InvalidStandardOptions && i.Message.Contains("sampling duration", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_owner_and_duplicate_binding_are_reported()
    {
        var orphan = TestData.Binding("b1", TestData.StandardHandler()) with { OwnerService = "ghost" };
        var graph = TestData.Graph(orphan, TestData.Binding("b1"));

        var codes = ResilienceGraphValidator.Validate(graph).Select(i => i.Code).ToList();

        Assert.Contains("UnknownOwner", codes);
        Assert.Contains("DuplicateBinding", codes);
    }

    [Fact]
    public void Edge_to_unknown_service_is_reported()
    {
        var graph = TestData.Graph() with { Edges = [new Edge("api", "nowhere", EdgeSource.Trace)] };

        Assert.Contains(ResilienceGraphValidator.Validate(graph), i => i.Code == "UnknownEdgeEndpoint");
    }
}
