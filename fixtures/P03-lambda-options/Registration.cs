using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace RetryScope.Fixtures.P03;

/// <summary>P2 + P3: the standard handler with literal option assignments in a lambda.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("payments", c => c.BaseAddress = new Uri("http://payments"))
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 5;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            });
    }
}
