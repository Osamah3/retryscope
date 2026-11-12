namespace RetryScope.Cli;

/// <summary>Parsed arguments of <c>retryscope extract</c>.</summary>
internal sealed record CliOptions(
    string ProjectPath,
    string? OutputPath,
    string? ServiceId,
    string? RootDirectory,
    IReadOnlyList<string> EntryMethods,
    string Environment,
    string? RepositoryUrl,
    string? CommitSha,
    bool Summary)
{
    public const string Usage = """
        Usage: retryscope extract <project.csproj> [options]

        Extracts the HttpClient resilience configuration of a .NET project as canonical JSON.

        Options:
          --out <file>            Write the graph to a file (default: standard output)
          --service <id>          Service id (default: the project file name)
          --root <dir>            Repository root; paths are reported relative to it
                                  (default: nearest parent folder containing .git, else the project folder)
          --entry <Type.Method>   Method where registration starts, e.g. MyApp.Startup.Configure (repeatable;
                                  default: the program entry point)
          --environment <name>    Host environment for appsettings and IsProduction() checks (default: Production)
          --repo-url <url>        Repository URL recorded in the graph
          --commit <sha>          Commit recorded in the graph (default: read from .git when present)
          --summary               Print a table of clients and diagnostics to standard error

        Exit codes: 0 success; 1 usage error; 2 extraction finished with error diagnostics; 3 extraction failed.
        """;

    /// <summary>Parses arguments; returns null and an error message when they are invalid.</summary>
    public static (CliOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            return (null, null);
        }

        if (args[0] != "extract")
        {
            return (null, $"Unknown command '{args[0]}'.");
        }

        string? project = null, output = null, service = null, root = null, url = null, commit = null;
        var environment = "Production";
        var entries = new List<string>();
        var summary = false;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--summary")
            {
                summary = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return (null, $"Option '{arg}' needs a value.");
                }

                var value = args[++i];
                switch (arg)
                {
                    case "--out": output = value; break;
                    case "--service": service = value; break;
                    case "--root": root = value; break;
                    case "--entry": entries.Add(value); break;
                    case "--environment": environment = value; break;
                    case "--repo-url": url = value; break;
                    case "--commit": commit = value; break;
                    default: return (null, $"Unknown option '{arg}'.");
                }

                continue;
            }

            if (project is not null)
            {
                return (null, $"Unexpected argument '{arg}'; only one project can be extracted per run.");
            }

            project = arg;
        }

        if (project is null)
        {
            return (null, "Missing <project.csproj>.");
        }

        return (new CliOptions(project, output, service, root, entries, environment, url, commit, summary), null);
    }
}
