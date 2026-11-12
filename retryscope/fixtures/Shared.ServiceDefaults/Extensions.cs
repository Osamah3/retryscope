using Microsoft.Extensions.DependencyInjection;

// Same namespace as the Aspire template, so fixture code reads exactly like a real Aspire service.
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Trimmed copy of the Aspire ServiceDefaults template's Extensions.cs. Only the HttpClient defaults are kept.
/// </summary>
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();
        });

        return builder;
    }
}
