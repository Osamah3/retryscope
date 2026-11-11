using RetryScope.Model;

namespace RetryScope.Model.Tests;

public class EffectiveConfigurationTests
{
    [Fact]
    public void Single_standard_handler_allows_four_attempts()
    {
        var binding = TestData.Binding("b1", TestData.StandardHandler());

        var bound = EffectiveConfiguration.MaxAttemptsPerCall(binding);

        Assert.Equal(4, bound.MaxAttempts);
        Assert.True(bound.IsExact);
        Assert.True(EffectiveConfiguration.IsRetrying(binding));
    }

    [Fact]
    public void Stacked_standard_handlers_multiply_attempts()
    {
        // Aspire defaults plus a second standard handler on the same client: (3+1) x (3+1).
        var binding = TestData.Binding("b1",
            TestData.StandardHandler(HandlerOrigin.Defaults),
            TestData.StandardHandler(HandlerOrigin.PerClient));

        Assert.Equal(16, EffectiveConfiguration.MaxAttemptsPerCall(binding).MaxAttempts);
        Assert.Equal(2, EffectiveConfiguration.RetryingHandlerCount(binding));
    }

    [Fact]
    public void Retries_disabled_for_unsafe_methods_apply_per_method()
    {
        var retry = TestData.DefaultRetry(disabledFor: ["POST", "PUT", "PATCH", "DELETE", "CONNECT"]);
        var binding = TestData.Binding("b1", TestData.StandardHandler(HandlerOrigin.PerClient, retry));

        Assert.Equal(4, EffectiveConfiguration.MaxAttemptsPerCall(binding, "GET").MaxAttempts);
        Assert.Equal(1, EffectiveConfiguration.MaxAttemptsPerCall(binding, "post").MaxAttempts);
        Assert.True(EffectiveConfiguration.IsRetrying(binding));
    }

    [Fact]
    public void No_handlers_means_one_attempt_and_not_retrying()
    {
        var binding = TestData.Binding("b1");

        Assert.Equal(1, EffectiveConfiguration.MaxAttemptsPerCall(binding).MaxAttempts);
        Assert.False(EffectiveConfiguration.IsRetrying(binding));
    }

    [Fact]
    public void Unresolved_retry_count_makes_bound_inexact()
    {
        var retry = TestData.DefaultRetry(Values.Unresolved(3, "config[\"Retries\"] from environment variable"));
        var binding = TestData.Binding("b1", TestData.StandardHandler(HandlerOrigin.PerClient, retry));

        var bound = EffectiveConfiguration.MaxAttemptsPerCall(binding);

        Assert.Equal(4, bound.MaxAttempts);
        Assert.False(bound.IsExact);
        Assert.Contains(bound.Caveats, c => c.Contains("Unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Hedging_handler_makes_bound_inexact()
    {
        var hedging = new Handler(HandlerKind.Hedging, HandlerOrigin.PerClient, []);
        var binding = TestData.Binding("b1", TestData.StandardHandler(), hedging);

        var bound = EffectiveConfiguration.MaxAttemptsPerCall(binding);

        Assert.False(bound.IsExact);
        Assert.False(hedging.IsSimulated);
    }

    [Fact]
    public void Huge_retry_counts_saturate_instead_of_overflowing()
    {
        var retry = TestData.DefaultRetry(Values.Explicit(int.MaxValue));
        var binding = TestData.Binding("b1",
            TestData.StandardHandler(HandlerOrigin.PerClient, retry),
            TestData.StandardHandler(HandlerOrigin.PerClient, retry));

        Assert.Equal(int.MaxValue, EffectiveConfiguration.MaxAttemptsPerCall(binding).MaxAttempts);
    }
}
