using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V03;

/// <summary>Variant of P3: registration hidden in a helper extension method, retry count from a constant.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCatalogClients();
    }
}
