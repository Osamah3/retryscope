using RetryScope.Model;

namespace RetryScope.Model.Tests;

public class BindingLabelsTests
{
    [Fact]
    public void Stacked_handlers_take_innermost_attempt_timeout_and_outermost_total_timeout()
    {
        // Labelling guide §4.6: defaults handler outer, per-client handler inner (here with a 0.5 s attempt timeout).
        var perClient = TestData.StandardHandler(HandlerOrigin.PerClient) with
        {
            Strategies =
            [
                .. TestData.StandardHandler().Strategies.Where(s => s is not TimeoutStrategy { Role: TimeoutRole.Attempt }),
                new TimeoutStrategy(TimeoutRole.Attempt, Values.Explicit(TimeSpan.FromMilliseconds(500))),
            ],
        };
        var binding = TestData.Binding("b1", TestData.StandardHandler(), perClient);

        var labels = BindingLabels.From(binding);

        Assert.Equal(16, labels.AttemptBoundGet);
        Assert.Equal(0.5, labels.AttemptTimeoutSeconds);
        Assert.Equal(30, labels.TotalTimeoutSeconds);
        Assert.Equal(2, labels.RetryHandlers);
        Assert.Equal("default", labels.RetryCountSource);
        Assert.Equal("named", labels.ClientKind);
    }

    [Fact]
    public void Hedging_leaves_the_attempt_bound_unknown_with_default_source()
    {
        var hedging = new Handler(HandlerKind.Hedging, HandlerOrigin.PerClient,
            [new TimeoutStrategy(TimeoutRole.Total, Values.PackageDefault(TimeSpan.FromSeconds(30), TestData.DefaultsVersion))]);

        var labels = BindingLabels.From(TestData.Binding("b1", hedging));

        Assert.Null(labels.AttemptBoundGet);
        Assert.Null(labels.AttemptBoundPost);
        Assert.Equal("default", labels.RetryCountSource);
        Assert.Equal(30, labels.TotalTimeoutSeconds);
        Assert.False(labels.Breaker);
    }

    [Fact]
    public void No_handlers_means_one_attempt_and_source_none()
    {
        var labels = BindingLabels.From(TestData.Binding("b1"));

        Assert.Equal(1, labels.AttemptBoundGet);
        Assert.Equal("none", labels.RetryCountSource);
        Assert.Equal(0, labels.RetryHandlers);
    }
}
