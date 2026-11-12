using Microsoft.Extensions.DependencyInjection;

namespace RetryScope.Fixtures.V08;

/// <summary>
/// Generic registration helpers, as in aspire-samples' AddHttpServiceReference (dev-set finding D4): the client type,
/// address and retry count arrive as type and value arguments from the caller.
/// </summary>
public static class ServiceClientExtensions
{
    public static IHttpClientBuilder AddServiceClient<TClient>(this IServiceCollection services, string baseAddress)
        where TClient : class
        => services.AddHttpClient<TClient>(c => c.BaseAddress = new(baseAddress));

    public static IHttpClientBuilder AddResilientServiceClient<TClient>(this IServiceCollection services, string baseAddress, int retries)
        where TClient : class
    {
        var uri = new Uri(baseAddress);
        var builder = services.AddHttpClient<TClient>(c => c.BaseAddress = uri);
        builder.AddStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = retries);
        return builder;
    }
}
