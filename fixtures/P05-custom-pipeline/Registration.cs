using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Microsoft.Extensions.Http.Resilience;

namespace RetryScope.Fixtures.P05;

/// <summary>P5: a custom pipeline. The timeout is added after the retry, so it bounds each attempt.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new Uri("http://inventory"))
            .AddResilienceHandler("inventory-pipeline", b =>
            {
                b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 2 });
                b.AddTimeout(TimeSpan.FromSeconds(3));
            });
    }
}
