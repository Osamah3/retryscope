using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace RetryScope.Fixtures.P08;

/// <summary>P2 + P3 + P8: retries disabled for unsafe HTTP methods (POST gets one attempt).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("ordering", c => c.BaseAddress = new Uri("http://ordering"))
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
    }
}
