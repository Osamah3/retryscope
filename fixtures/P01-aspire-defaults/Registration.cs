using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P01;

/// <summary>P1: Aspire ServiceDefaults adds the standard resilience handler to every IHttpClientFactory client.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"));
    }
}
