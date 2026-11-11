using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;

namespace RetryScope.Fixtures.P10;

/// <summary>P10: a keyed Polly pipeline executed by a typed client around its HttpClient calls (no resilience handler on the client).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddResiliencePipeline<string, HttpResponseMessage>(CatalogClient.PipelineKey, b =>
            b.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>().HandleResult(r => (int)r.StatusCode >= 500),
            }));

        builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog"));
    }
}
