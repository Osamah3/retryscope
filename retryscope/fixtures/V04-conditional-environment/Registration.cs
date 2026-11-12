using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.V04;

/// <summary>Variant: different resilience settings per environment. Labelled for Production; marked conditional.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var search = builder.Services.AddHttpClient("search", c => c.BaseAddress = new Uri("http://search"));

        if (builder.Environment.IsProduction())
        {
            search.AddStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = 1);
        }
        else
        {
            search.AddStandardResilienceHandler();
        }
    }
}
