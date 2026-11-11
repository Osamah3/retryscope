namespace RetryScope.Fixtures.V01;

/// <summary>Creates a raw HttpClient, so neither defaults nor handlers from IHttpClientFactory apply.</summary>
public static class ReportJob
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://reports") };
        return await client.GetStringAsync(new Uri("/daily", UriKind.Relative), cancellationToken).ConfigureAwait(false);
    }
}
