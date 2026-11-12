using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>Sends one request of the given method through a fixture's client and returns the final response.</summary>
public delegate Task<HttpResponseMessage> InvokeClient(IServiceProvider services, string clientName, HttpMethod method, CancellationToken cancellationToken);

/// <summary>A fixture's registration entry point and, when calls don't go straight through IHttpClientFactory, how to make one.</summary>
public sealed record FixtureEntry(Action<IHostApplicationBuilder> Register, InvokeClient? Invoke = null);

/// <summary>Every fixture, keyed by its folder name.</summary>
public static class FixtureCatalog
{
    private static readonly Dictionary<string, FixtureEntry> Entries = new(StringComparer.Ordinal)
    {
        ["P01-aspire-defaults"] = new(P01.Registration.Register),
        ["P02-per-client-standard"] = new(P02.Registration.Register),
        ["P03-lambda-options"] = new(P03.Registration.Register),
        ["P04-config-bound"] = new(P04.Registration.Register),
        ["P05-custom-pipeline"] = new(P05.Registration.Register),
        ["P06-stacked-defaults-and-client"] = new(P06.Registration.Register),
        ["P07-remove-then-custom"] = new(P07.Registration.Register),
        ["P08-unsafe-methods-disabled"] = new(P08.Registration.Register),
        ["P09-custom-predicate"] = new(P09.Registration.Register),
        ["P10-keyed-pipeline"] = new(P10.Registration.Register,
            (sp, _, method, ct) => sp.GetRequiredService<P10.CatalogClient>().SendAsync(method, ct)),
        ["P11-hedging"] = new(P11.Registration.Register),
        ["P12-polly-v7"] = new(P12.Registration.Register),
        ["P13-manual-retry-loop"] = new(P13.Registration.Register,
            (sp, _, method, ct) => sp.GetRequiredService<P13.ReportClient>().SendAsync(method, ct)),
        ["V01-direct-httpclient"] = new(V01.Registration.Register),
        ["V02-typed-client"] = new(V02.Registration.Register),
        ["V03-helper-extension"] = new(V03.Registration.Register),
        ["V04-conditional-environment"] = new(V04.Registration.Register),
        ["V05-production-override"] = new(V05.Registration.Register),
        ["V06-stack-order-defaults-first"] = new(V06.Registration.Register),
        ["V07-stack-order-client-first"] = new(V07.Registration.Register),
        ["V08-generic-helper"] = new(V08.Registration.Register),
        ["V09-local-base-address"] = new(V09.Registration.Register),
    };

    public static TheoryData<string> Ids => new(Entries.Keys.Order(StringComparer.Ordinal));

    public static TheoryData<string> StackOrderIds => new(
        Entries.Keys
            .Where(id => ExpectedFixture.Load(id).Bindings.Any(b => b.StackOrder is { Count: > 1 }))
            .Order(StringComparer.Ordinal));

    public static FixtureEntry Get(string fixtureId) => Entries[fixtureId];

    /// <summary>Default invocation: one request through the named client from IHttpClientFactory.</summary>
    public static async Task<HttpResponseMessage> SendThroughFactoryAsync(
        IServiceProvider services, string clientName, HttpMethod method, CancellationToken cancellationToken)
    {
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var request = new HttpRequestMessage(method, new Uri("/probe", UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}");
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
