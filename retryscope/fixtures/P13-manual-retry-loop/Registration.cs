using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.P13;

/// <summary>P13: a hand-written retry loop in a typed client (no Polly). A known blind spot for V1 extraction.</summary>
public static class Registration
{
    public static void Register(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient<ReportClient>(c => c.BaseAddress = new Uri("http://reports"));
    }
}
