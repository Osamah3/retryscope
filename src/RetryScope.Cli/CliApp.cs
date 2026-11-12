using System.Globalization;
using RetryScope.Extraction;
using RetryScope.Model;

namespace RetryScope.Cli;

/// <summary>The CLI logic, separate from <c>Program</c> so tests can run it with in-memory writers.</summary>
internal static class CliApp
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int ExtractionErrors = 2;
    public const int Failure = 3;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (options, error) = CliOptions.Parse(args);
        if (options is null)
        {
            await (error is null ? stdout : stderr).WriteLineAsync(error is null ? CliOptions.Usage : $"{error}\n\n{CliOptions.Usage}").ConfigureAwait(false);
            return error is null ? Success : UsageError;
        }

        var projectPath = Path.GetFullPath(options.ProjectPath);
        if (!File.Exists(projectPath))
        {
            await stderr.WriteLineAsync($"Project not found: {projectPath}").ConfigureAwait(false);
            return UsageError;
        }

        var root = Path.GetFullPath(options.RootDirectory ?? FindRepositoryRoot(projectPath) ?? Path.GetDirectoryName(projectPath)!);
        var extraction = new ExtractionOptions(
            ServiceId: options.ServiceId ?? Path.GetFileNameWithoutExtension(projectPath),
            RootDirectory: root,
            RootMethods: options.EntryMethods,
            Source: new SourceInfo(options.RepositoryUrl, options.CommitSha ?? ReadHeadCommit(root)),
            Environment: options.Environment);

        ResilienceGraph graph;
        try
        {
            graph = await Extractor.ExtractProjectAsync(projectPath, extraction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            await stderr.WriteLineAsync($"Extraction failed: {exception.Message}").ConfigureAwait(false);
            return Failure;
        }

        var json = ResilienceGraphJson.Serialize(graph);
        if (options.OutputPath is { } output)
        {
            await File.WriteAllTextAsync(output, json, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteAsync(json).ConfigureAwait(false);
        }

        if (options.Summary)
        {
            await stderr.WriteAsync(Summarize(graph)).ConfigureAwait(false);
        }

        return graph.Diagnostics.Any(d => d.Severity == Severity.Error) ? ExtractionErrors : Success;
    }

    /// <summary>One row per client in the labelling guide's terms, then the diagnostics.</summary>
    internal static string Summarize(ResilienceGraph graph)
    {
        static string Cell(object? value) => value switch
        {
            null => "-",
            bool b => b ? "Y" : "N",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-",
        };

        var rows = new List<string[]> { new[] { "client", "kind", "target", "GET", "POST", "source", "attempt s", "total s", "breaker", "retry handlers", "conditional" } };
        rows.AddRange(graph.Bindings.Select(BindingLabels.From).Select(l => new[]
        {
            l.Client.Length == 0 ? "(default)" : l.Client, l.ClientKind, Cell(l.Target), Cell(l.AttemptBoundGet), Cell(l.AttemptBoundPost), l.RetryCountSource,
            Cell(l.AttemptTimeoutSeconds), Cell(l.TotalTimeoutSeconds), Cell(l.Breaker), Cell(l.RetryHandlers), Cell(l.Conditional),
        }));

        var widths = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            text.AppendLine(string.Join("  ", row.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd());
        }

        if (graph.Bindings.Count == 0)
        {
            text.AppendLine("(no HttpClient registrations found)");
        }

        foreach (var diagnostic in graph.Diagnostics)
        {
            var where = diagnostic.Location is { } l ? $" ({l.File}:{l.Line})" : string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture, $"{diagnostic.Code} {diagnostic.Severity}: {diagnostic.Message}{where}");
        }

        return text.ToString();
    }

    private static string? FindRepositoryRoot(string projectPath)
    {
        for (var directory = Path.GetDirectoryName(projectPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>Best effort: the commit HEAD points to, from .git/HEAD, a loose ref or packed-refs. Null if unknown.</summary>
    private static string? ReadHeadCommit(string root)
    {
        try
        {
            var gitDirectory = Path.Combine(root, ".git");
            var head = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(head))
            {
                return null;
            }

            var content = File.ReadAllText(head).Trim();
            if (!content.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return content; // detached HEAD
            }

            var reference = content["ref: ".Length..];
            var loose = Path.Combine(gitDirectory, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
            {
                return File.ReadAllText(loose).Trim();
            }

            var packed = Path.Combine(gitDirectory, "packed-refs");
            return File.Exists(packed)
                ? File.ReadLines(packed).Select(line => line.Split(' ')).FirstOrDefault(parts => parts.Length == 2 && parts[1] == reference)?[0]
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
