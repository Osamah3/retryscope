using System.Net;
using System.Net.Http.Headers;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>
/// In-memory replacement for the network. Every request fails with 503 and <c>Retry-After: 0</c>, so handlers that
/// honour Retry-After retry immediately and no timeout can interfere. The number of requests that reach it is the
/// observed attempt count. Nothing leaves the process.
/// </summary>
internal sealed class CountingStubHandler : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Reset() => Interlocked.Exchange(ref _calls, 0);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return Task.FromResult(response);
    }
}
