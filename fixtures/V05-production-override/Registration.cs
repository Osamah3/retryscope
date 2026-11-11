using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V05;

/// <summary>Variant of P4: appsettings.Production.json overrides the base appsettings.json.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new Uri("http://inventory"))
            .AddStandardResilienceHandler(builder.Configuration.GetSection("Resilience:Inventory"));
    }
}
