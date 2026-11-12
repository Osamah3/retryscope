namespace RetryScope.Fixtures.OracleTests;

/// <summary>
/// Behavioural ground truth for the attempt bound: run each fixture's real registration code, send one GET and one
/// POST to a stub that always fails with <c>Retry-After: 0</c>, and count the attempts that arrive.
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
            if (!binding.Oracle)
            {
                Log($"{fixtureId}/{binding.Client}: not observable through IHttpClientFactory ({binding.ClientKind}); static label only.");
                continue;
            }

            var get = await CountAttemptsAsync(fixtureId, binding.Client, HttpMethod.Get);
            var post = await CountAttemptsAsync(fixtureId, binding.Client, HttpMethod.Post);
            Log($"{fixtureId}/{binding.Client}: observed GET={get}, POST={post}.");

            if (binding.AttemptBoundGet is { } expectedGet)
            {
                Assert.True(expectedGet == get, $"{fixtureId}/{binding.Client} GET: expected {expectedGet} attempts, observed {get}.");
            }

            if (binding.AttemptBoundPost is { } expectedPost)
            {
                Assert.True(expectedPost == post, $"{fixtureId}/{binding.Client} POST: expected {expectedPost} attempts, observed {post}.");
            }
        }
    }

    private static async Task<int> CountAttemptsAsync(string fixtureId, string clientName, HttpMethod method)
    {
        var stub = new CountingStubHandler();
        using var host = OracleHost.Build(fixtureId, stub);

        using (await OracleHost.SendAsync(host, fixtureId, clientName, method, TestContext.Current.CancellationToken))
        {
        }

        return stub.Calls;
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
