using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P06;

/// <summary>P1 + P2 + P6: Aspire defaults plus a second standard handler on the same client (stacking).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("orders", c => c.BaseAddress = new Uri("http://orders"))
            .AddStandardResilienceHandler();
    }
}
