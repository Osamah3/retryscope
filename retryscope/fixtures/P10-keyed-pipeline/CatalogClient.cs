using Polly.Registry;

namespace RetryScope.Fixtures.P10;

/// <summary>Typed client that runs a keyed pipeline around each HttpClient call.</summary>
public sealed class CatalogClient(HttpClient http, ResiliencePipelineProvider<string> pipelines)
{
    public const string PipelineKey = "catalog-pipeline";

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, CancellationToken cancellationToken)
    {
        var pipeline = pipelines.GetPipeline<HttpResponseMessage>(PipelineKey);
        return await pipeline.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(method, new Uri("/probe", UriKind.Relative));
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("{}");
            }

            return await http.SendAsync(request, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
