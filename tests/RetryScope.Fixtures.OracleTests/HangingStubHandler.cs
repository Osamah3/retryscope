namespace RetryScope.Fixtures.OracleTests;

/// <summary>A stub that never answers: every attempt ends only when a timeout cancels it.</summary>
internal sealed class HangingStubHandler : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
    }
}
