namespace RetryScope.Fixtures.P13;

/// <summary>Typed client with its own retry loop: up to three attempts per call.</summary>
public sealed class ReportClient(HttpClient http)
{
    private const int MaxAttempts = 3;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri("/probe", UriKind.Relative));
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("{}");
            }

            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || attempt == MaxAttempts)
            {
                return response;
            }

            response.Dispose();
        }
    }
}
