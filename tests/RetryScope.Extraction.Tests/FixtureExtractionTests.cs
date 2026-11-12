using RetryScope.Fixtures.OracleTests;
using RetryScope.Model;

namespace RetryScope.Extraction.Tests;

/// <summary>
/// Regression check required before the real-repository evaluation: running the extractor on a fixture must reproduce
/// that fixture's expected.json, field by field. The expected labels are themselves confirmed by the behaviour oracle.
/// </summary>
public class FixtureExtractionTests
{
    /// <summary>Fixtures the extractor covers so far. Each slice adds to this list; none is ever removed.</summary>
    public static TheoryData<string> Supported => new()
    {
        "P01-aspire-defaults",
        "P02-per-client-standard",
        "P03-lambda-options",
        "P04-config-bound",
        "P06-stacked-defaults-and-client",
        "P09-custom-predicate",
        "V03-helper-extension",
        "V05-production-override",
    };

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task Extractor_output_matches_expected_labels(string fixtureId)
    {
        var expected = ExpectedFixture.Load(fixtureId);
        var graph = await ExtractAsync(fixtureId, expected.Service);

        foreach (var diagnostic in graph.Diagnostics)
        {
            Log($"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message} ({diagnostic.Location?.File}:{diagnostic.Location?.Line})");
        }

        Assert.DoesNotContain(graph.Diagnostics, d => d.Severity == Severity.Error);

        var mismatches = new List<string>();
        var actualByClient = graph.Bindings.ToDictionary(b => b.ClientName, BindingLabels.From, StringComparer.Ordinal);

        foreach (var want in expected.Bindings)
        {
            if (!actualByClient.Remove(want.Client, out var got))
            {
                mismatches.Add($"{want.Client}: expected binding not extracted.");
                continue;
            }

            Log($"{fixtureId}/{want.Client}: {got}");
            Compare(mismatches, want.Client, "clientKind", want.ClientKind, got.ClientKind);
            Compare(mismatches, want.Client, "target", want.Target, got.Target);
            Compare(mismatches, want.Client, "attemptBoundGet", want.AttemptBoundGet, got.AttemptBoundGet);
            Compare(mismatches, want.Client, "attemptBoundPost", want.AttemptBoundPost, got.AttemptBoundPost);
            Compare(mismatches, want.Client, "retryCountSource", want.RetryCountSource, got.RetryCountSource);
            Compare(mismatches, want.Client, "attemptTimeoutSeconds", want.AttemptTimeoutSeconds, got.AttemptTimeoutSeconds);
            Compare(mismatches, want.Client, "totalTimeoutSeconds", want.TotalTimeoutSeconds, got.TotalTimeoutSeconds);
            Compare(mismatches, want.Client, "breaker", want.Breaker, got.Breaker);
            Compare(mismatches, want.Client, "retryHandlers", want.RetryHandlers, got.RetryHandlers);
            Compare(mismatches, want.Client, "conditional", want.Conditional, got.Conditional);
        }

        mismatches.AddRange(actualByClient.Keys.Select(extra => $"{extra}: extracted but not in expected.json."));

        Assert.True(mismatches.Count == 0, $"{fixtureId}:\n  " + string.Join("\n  ", mismatches));
    }

    private static async Task<ResilienceGraph> ExtractAsync(string fixtureId, string serviceId)
    {
        var directory = FixturePaths.Directory(fixtureId);
        var projectPath = Directory.GetFiles(directory, "*.csproj").Single();
        var code = fixtureId.Split('-')[0];

        var options = new ExtractionOptions(
            ServiceId: serviceId,
            RootDirectory: Path.GetFullPath(Path.Combine(directory, "..", "..")),
            RootMethods: [$"RetryScope.Fixtures.{code}.Registration.Register"]);

        return await Extractor.ExtractProjectAsync(projectPath, options, TestContext.Current.CancellationToken);
    }

    private static void Compare<T>(List<string> mismatches, string client, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            mismatches.Add($"{client}.{field}: expected {expected?.ToString() ?? "null"}, extracted {actual?.ToString() ?? "null"}.");
        }
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
