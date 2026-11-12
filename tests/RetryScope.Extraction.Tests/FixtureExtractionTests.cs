using RetryScope.Fixtures.OracleTests;
using RetryScope.Model;

namespace RetryScope.Extraction.Tests;

/// <summary>
/// Regression check required before the real-repository evaluation: running the extractor on a fixture must reproduce
/// that fixture's expected.json, field by field. The expected labels are themselves confirmed by the behaviour oracle.
/// </summary>
public class FixtureExtractionTests
{
    /// <summary>Fixtures the extractor covers. Each slice adds to this list; none is ever removed.</summary>
    private static readonly string[] SupportedIds =
    [
        "P01-aspire-defaults",
        "P02-per-client-standard",
        "P03-lambda-options",
        "P04-config-bound",
        "P05-custom-pipeline",
        "P06-stacked-defaults-and-client",
        "P07-remove-then-custom",
        "P08-unsafe-methods-disabled",
        "P09-custom-predicate",
        "P11-hedging",
        "P12-polly-v7",
        "V01-direct-httpclient",
        "V02-typed-client",
        "V03-helper-extension",
        "V04-conditional-environment",
        "V05-production-override",
        "V06-stack-order-defaults-first",
        "V07-stack-order-client-first",
        "V08-generic-helper",
        "V09-local-base-address",
    ];

    public static TheoryData<string> Supported
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in SupportedIds)
            {
                data.Add(id);
            }

            return data;
        }
    }

    /// <summary>
    /// Known gaps, reported rather than hidden (docs/extractor-scope.md). Both need data-flow analysis inside a typed
    /// client, which V1 does not do; the extractor reports these clients with no resilience.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownGaps = new Dictionary<string, string>
    {
        ["P10-keyed-pipeline"] = "keyed pipeline executed inside a typed client; not linked to the client",
        ["P13-manual-retry-loop"] = "hand-written retry loop; not recognised",
    };

    [Fact]
    public void Every_fixture_is_either_supported_or_a_documented_gap()
    {
        var all = Directory.GetDirectories(Path.Combine(FixturePaths.Directory("P01-aspire-defaults"), ".."))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[PV][0-9]{2}-"))
            .ToHashSet(StringComparer.Ordinal);
        var supported = SupportedIds.ToHashSet(StringComparer.Ordinal);

        Assert.Empty(all.Except(supported).Except(KnownGaps.Keys));
        Assert.Empty(supported.Intersect(KnownGaps.Keys));
    }

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
