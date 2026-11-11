using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;

namespace RetryScope.Fixtures.P12;

/// <summary>P12: legacy Polly v7 transient-error policy (detected and counted, not simulated in V1).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("shipping", c => c.BaseAddress = new Uri("http://shipping"))
            .AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(100 * i)));
    }
}
