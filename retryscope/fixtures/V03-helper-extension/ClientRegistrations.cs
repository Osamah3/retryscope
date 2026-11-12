using Microsoft.Extensions.DependencyInjection;

namespace RetryScope.Fixtures.V03;

/// <summary>Helper extension, as real projects often use. The extractor must follow it.</summary>
public static class ClientRegistrations
{
    private const int CatalogRetries = 4;

    public static IServiceCollection AddCatalogClients(this IServiceCollection services)
    {
        services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"))
            .AddStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = CatalogRetries);

        return services;
    }
}
