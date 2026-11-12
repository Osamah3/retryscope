using RetryScope.Fixtures.OracleTests;
using RetryScope.Model;

namespace RetryScope.Cli.Tests;

public class CliTests
{
    [Fact]
    public void Arguments_are_parsed()
    {
        var (options, error) = CliOptions.Parse(
            ["extract", "app.csproj", "--out", "g.json", "--entry", "A.B.C", "--entry", "D.E.F", "--environment", "Staging", "--summary"]);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("app.csproj", options.ProjectPath);
        Assert.Equal("g.json", options.OutputPath);
        Assert.Equal(new[] { "A.B.C", "D.E.F" }, options.EntryMethods);
        Assert.Equal("Staging", options.Environment);
        Assert.True(options.Summary);
    }

    [Theory]
    [InlineData(new[] { "extract" }, "Missing <project.csproj>.")]
    [InlineData(new[] { "build", "x.csproj" }, "Unknown command 'build'.")]
    [InlineData(new[] { "extract", "x.csproj", "--bogus", "1" }, "Unknown option '--bogus'.")]
    [InlineData(new[] { "extract", "x.csproj", "--out" }, "Option '--out' needs a value.")]
    [InlineData(new[] { "extract", "a.csproj", "b.csproj" }, "Unexpected argument 'b.csproj'; only one project can be extracted per run.")]
    public void Invalid_arguments_are_reported(string[] args, string expected)
    {
        var (options, error) = CliOptions.Parse(args);

        Assert.Null(options);
        Assert.Equal(expected, error);
    }

    [Fact]
    public async Task Missing_project_is_a_usage_error()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exitCode = await CliApp.RunAsync(["extract", "does-not-exist.csproj"], stdout, stderr, TestContext.Current.CancellationToken);

        Assert.Equal(CliApp.UsageError, exitCode);
        Assert.Contains("Project not found", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_writes_a_graph_that_round_trips_and_a_summary()
    {
        var directory = FixturePaths.Directory("P02-per-client-standard");
        var project = Directory.GetFiles(directory, "*.csproj").Single();
        var output = Path.Combine(Path.GetTempPath(), $"retryscope-{Guid.NewGuid():N}.json");
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        try
        {
            var exitCode = await CliApp.RunAsync(
                ["extract", project, "--root", Path.GetFullPath(Path.Combine(directory, "..", "..")), "--service", "api",
                    "--entry", "RetryScope.Fixtures.P02.Registration.Register", "--out", output, "--summary"],
                stdout,
                stderr,
                TestContext.Current.CancellationToken);

            Assert.Equal(CliApp.Success, exitCode);

            var graph = ResilienceGraphJson.Deserialize(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            var binding = Assert.Single(graph.Bindings);
            Assert.Equal("catalog", binding.ClientName);
            Assert.Equal(4, BindingLabels.From(binding).AttemptBoundGet);

            // Paths are relative to the repository root, so the output does not depend on the machine.
            Assert.Equal("fixtures/P02-per-client-standard/Fixture.P02.csproj", Assert.Single(graph.Services).ProjectPath);

            var summary = stderr.ToString();
            TestContext.Current.TestOutputHelper?.WriteLine(summary);
            Assert.Contains("catalog", summary, StringComparison.Ordinal);
            Assert.Empty(stdout.ToString());
        }
        finally
        {
            File.Delete(output);
        }
    }
}
