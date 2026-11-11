using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using Microsoft.Extensions.Http.Resilience;

namespace RetryScope.Fixtures.P09;

/// <summary>P2 + P3 + P9: a custom retry predicate (only 503 is retried). The count is known; the predicate is opaque to static analysis.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient("catalog", c => c.BaseAddress = new Uri("http://catalog"))
            .AddStandardResilienceHandler(o =>
                o.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result?.StatusCode == HttpStatusCode.ServiceUnavailable));
    }
}
