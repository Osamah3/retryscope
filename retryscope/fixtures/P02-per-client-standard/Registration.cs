using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P02;

/// <summary>P2: the standard resilience handler on one named client, all defaults.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"))
            .AddStandardResilienceHandler();
    }
}
