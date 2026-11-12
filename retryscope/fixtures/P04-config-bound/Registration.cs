using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P04;

/// <summary>P2 + P4: standard handler options bound from appsettings.json.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new Uri("http://inventory"))
            .AddStandardResilienceHandler(builder.Configuration.GetSection("Resilience:Inventory"));
    }
}
