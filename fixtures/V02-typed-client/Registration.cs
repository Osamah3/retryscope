using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V02;

/// <summary>Variant of P2: a typed client (its client name is the type name).</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog"))
            .AddStandardResilienceHandler();
    }
}
