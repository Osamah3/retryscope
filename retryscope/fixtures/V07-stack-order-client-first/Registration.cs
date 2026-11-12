using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V07;

/// <summary>Stack-order probe: the per-client handler is registered BEFORE AddServiceDefaults() is called.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("orders", c => c.BaseAddress = new Uri("http://orders"))
            .AddStandardResilienceHandler(o =>
            {
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(1);
                o.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(500);
            });
        builder.AddServiceDefaults();
    }
}
