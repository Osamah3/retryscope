using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V01;

/// <summary>Variant: an HttpClient created with new HttpClient(). No resilience handler can apply.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Nothing is registered: ReportJob creates its own HttpClient.
        _ = builder;
    }
}
