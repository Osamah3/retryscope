using System.Text.Json;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>One fixture's <c>expected.json</c>: the labelling guide's fields, per binding.</summary>
internal sealed record ExpectedFixture(string Fixture, string Description, string Service, IReadOnlyList<ExpectedBinding> Bindings)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ExpectedFixture Load(string fixtureId)
    {
        var path = Path.Combine(FixturePaths.Directory(fixtureId), "expected.json");
        return JsonSerializer.Deserialize<ExpectedFixture>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }
}

/// <summary>Expected labels for one HttpClient binding.</summary>
internal sealed record ExpectedBinding(
    string Client,
    string ClientKind,
    string? Target,
    IReadOnlyList<string> PatternCodes,
    int AttemptBoundGet,
    int AttemptBoundPost,
    string RetryCountSource,
    double? AttemptTimeoutSeconds,
    double? TotalTimeoutSeconds,
    bool Breaker,
    int RetryHandlers,
    bool Conditional);

/// <summary>Finds fixture folders relative to the repository root (the folder holding RetryScope.slnx).</summary>
internal static class FixturePaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string Directory(string fixtureId) => Path.Combine(Root.Value, "fixtures", fixtureId);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetryScope.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (RetryScope.slnx).");
    }
}
