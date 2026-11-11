using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>Builds a fixture's real DI container in the Production environment, with the network replaced by a stub.</summary>
internal static class OracleHost
{
    public static IHost Build(string fixtureId, HttpMessageHandler stub)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = FixturePaths.Directory(fixtureId),
            Args = [],
        });

        FixtureCatalog.Get(fixtureId).Register(builder);

        // Swap only the network: the primary handler sits below every resilience handler.
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => stub));

        return builder.Build();
    }

    public static Task<HttpResponseMessage> SendAsync(IHost host, string fixtureId, string clientName, HttpMethod method, CancellationToken cancellationToken)
    {
        var invoke = FixtureCatalog.Get(fixtureId).Invoke ?? FixtureCatalog.SendThroughFactoryAsync;
        return invoke(host.Services, clientName, method, cancellationToken);
    }
}
