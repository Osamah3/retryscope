using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V09;

/// <summary>Variant of P2: the base address is held in a local variable (as in aspire-samples health-checks-ui, dev-set D5).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var apiServiceUri = new Uri("http://weather");
        builder.Services.AddHttpClient<WeatherClient>(client => client.BaseAddress = apiServiceUri)
            .AddStandardResilienceHandler();
    }
}
