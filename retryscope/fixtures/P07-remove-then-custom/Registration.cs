using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Microsoft.Extensions.Http.Resilience;

namespace RetryScope.Fixtures.P07;

/// <summary>P1 + P5 + P7: Aspire defaults removed from one client, replaced by a custom pipeline.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new Uri("http://inventory"))
            .RemoveAllResilienceHandlers()
            .AddResilienceHandler("inventory-pipeline", b =>
            {
                b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 2 });
                b.AddTimeout(TimeSpan.FromSeconds(3));
            });
    }
}
