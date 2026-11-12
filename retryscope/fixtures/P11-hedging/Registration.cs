using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P11;

/// <summary>P11: the standard hedging handler. Detected but not simulated in V1, so the attempt bound is left unknown.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"))
            .AddStandardHedgingHandler();
    }
}
