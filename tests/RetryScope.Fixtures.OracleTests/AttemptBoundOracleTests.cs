using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>
/// Behavioural ground truth for the attempt bound: run each fixture's real registration code, send one GET and one
/// POST through the configured HttpClient to a stub that always fails, and count the attempts that arrive.
/// If a test fails, the expected labels (or the labelling guide) are wrong, not the libraries.
/// </summary>
public class AttemptBoundOracleTests
{
    [Theory]
    [MemberData(nameof(FixtureCatalog.Ids), MemberType = typeof(FixtureCatalog))]
    public async Task Observed_attempts_equal_the_expected_attempt_bound(string fixtureId)
    {
        var expected = ExpectedFixture.Load(fixtureId);
        Assert.NotEmpty(expected.Bindings);

        foreach (var binding in expected.Bindings)
        {
            var observed = await ObserveAttemptsAsync(fixtureId, binding.Client);

            Assert.True(binding.AttemptBoundGet == observed.Get,
                $"{fixtureId}/{binding.Client} GET: expected {binding.AttemptBoundGet} attempts, observed {observed.Get}.");
            Assert.True(binding.AttemptBoundPost == observed.Post,
                $"{fixtureId}/{binding.Client} POST: expected {binding.AttemptBoundPost} attempts, observed {observed.Post}.");
        }
    }

    private static async Task<(int Get, int Post)> ObserveAttemptsAsync(string fixtureId, string clientName)
    {
        var stub = new CountingStubHandler();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = FixturePaths.Directory(fixtureId),
            Args = [],
        });

        FixtureCatalog.Get(fixtureId)(builder);

        // Swap only the network: the primary handler sits below every resilience handler.
        builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => stub));

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        var cancellationToken = TestContext.Current.CancellationToken;

        using (await client.GetAsync(new Uri("/probe", UriKind.Relative), cancellationToken))
        {
        }

        var get = stub.Calls;
        stub.Reset();

        using (var content = new StringContent("{}"))
        using (await client.PostAsync(new Uri("/probe", UriKind.Relative), content, cancellationToken))
        {
        }

        return (get, stub.Calls);
    }
}
