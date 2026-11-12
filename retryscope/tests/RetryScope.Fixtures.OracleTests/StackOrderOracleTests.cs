using System.Diagnostics;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>
/// Settles which of two stacked handlers is outermost. The probe fixtures give the per-client handler a 1 s total
/// timeout while the defaults handler keeps 30 s, and the stub never answers:
/// <list type="bullet">
/// <item>per-client outermost: its 1 s total timeout ends the whole call after about 1 s;</item>
/// <item>defaults outermost: each outer attempt runs the inner 1 s pipeline, then the outer handler backs off and
/// retries, so the call lasts several seconds (up to the outer 30 s total timeout).</item>
/// </list>
/// </summary>
public class StackOrderOracleTests
{
    private static readonly TimeSpan PerClientOuterMax = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultsOuterMin = TimeSpan.FromSeconds(3);

    [Theory]
    [MemberData(nameof(FixtureCatalog.StackOrderIds), MemberType = typeof(FixtureCatalog))]
    public async Task Observed_stack_order_matches_the_expected_order(string fixtureId)
    {
        var binding = ExpectedFixture.Load(fixtureId).Bindings.Single(b => b.StackOrder is { Count: > 1 });
        var expectedOuter = binding.StackOrder![0];

        var stub = new HangingStubHandler();
        using var host = OracleHost.Build(fixtureId, stub);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await OracleHost.SendAsync(host, fixtureId, binding.Client, HttpMethod.Get, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            // Expected: the call ends with a timeout exception from the outermost handler.
        }

        stopwatch.Stop();

        var observedOuter = stopwatch.Elapsed <= PerClientOuterMax ? "perClient"
            : stopwatch.Elapsed >= DefaultsOuterMin ? "defaults"
            : "inconclusive";

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{fixtureId}: call lasted {stopwatch.Elapsed.TotalSeconds:F2} s over {stub.Calls} attempts; outermost handler looks like '{observedOuter}'.");

        Assert.True(expectedOuter == observedOuter,
            $"{fixtureId}: expected '{expectedOuter}' outermost, observed '{observedOuter}' ({stopwatch.Elapsed.TotalSeconds:F2} s, {stub.Calls} attempts).");
    }
}
