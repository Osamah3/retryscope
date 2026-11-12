using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V08;

/// <summary>Variant of P2/P3: typed clients registered through generic helpers with constant arguments.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddServiceClient<CatalogClient>("http://catalog");
        builder.Services.AddResilientServiceClient<OrdersClient>("http://orders", retries: 1);
    }
}
