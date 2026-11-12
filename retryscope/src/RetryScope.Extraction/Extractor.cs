using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RetryScope.Model;
using RsDiagnostic = RetryScope.Model.Diagnostic;

namespace RetryScope.Extraction;

/// <summary>Options for one extraction run.</summary>
/// <param name="ServiceId">Id of the service the project represents (e.g. its Aspire resource name).</param>
/// <param name="RootDirectory">Repository root; every reported path is made relative to it.</param>
/// <param name="RootMethods">
/// Where registration starts, as <c>Namespace.Type.Method</c>. Empty means the program's entry point.
/// </param>
/// <param name="Source">Repository URL and commit, recorded in the graph.</param>
public sealed record ExtractionOptions(
    string ServiceId,
    string RootDirectory,
    IReadOnlyList<string>? RootMethods = null,
    SourceInfo? Source = null);

/// <summary>Reads one .NET project and produces the resilience configuration of its HttpClients.</summary>
public static class Extractor
{
    private const string ResilienceAssembly = "Microsoft.Extensions.Http.Resilience";

    public static async Task<ResilienceGraph> ExtractProjectAsync(
        string projectPath, ExtractionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(options);

        var rootDirectory = Path.GetFullPath(options.RootDirectory);
        var loadProblems = new ConcurrentQueue<RsDiagnostic>();

        using var workspace = MSBuildWorkspace.Create();
        using var failedHandler = workspace.RegisterWorkspaceFailedHandler(e => loadProblems.Enqueue(new RsDiagnostic(
            DiagnosticCodes.WorkspaceLoad,
            e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? Severity.Error : Severity.Warning,
            e.Diagnostic.Message)));

        var project = await workspace.OpenProjectAsync(Path.GetFullPath(projectPath), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No compilation for {projectPath}.");

        var diagnostics = new List<RsDiagnostic>();

        // Compiler errors mean some calls may not bind to a symbol; say so rather than silently under-report.
        foreach (var error in compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Take(20))
        {
            diagnostics.Add(new RsDiagnostic(DiagnosticCodes.WorkspaceLoad, Severity.Error, $"Compiler error {error.Id}: {error.GetMessage()}"));
        }

        var packageVersion = ResiliencePackageVersion(compilation, diagnostics);

        var walker = new RegistrationWalker(project.Solution, rootDirectory, packageVersion);
        foreach (var root in FindRoots(compilation, options.RootMethods))
        {
            await walker.WalkAsync(root, cancellationToken).ConfigureAwait(false);
        }

        // Defaults handlers wrap per-client handlers regardless of call order (docs/findings.md F1).
        var bindings = walker.Clients.Values.Select(client => new ClientBinding(
            Id: $"{options.ServiceId}/{client.Name}",
            OwnerService: options.ServiceId,
            ClientName: client.Name,
            Kind: client.Kind,
            Handlers: [.. walker.DefaultHandlers, .. client.Handlers],
            Location: client.Location));

        var service = new Service(
            options.ServiceId,
            Path.GetRelativePath(rootDirectory, project.FilePath ?? projectPath).Replace('\\', '/'));

        return ResilienceGraph.Create(
            options.Source ?? new SourceInfo(),
            [service],
            bindings,
            diagnostics: [.. loadProblems, .. diagnostics, .. walker.Diagnostics]).Canonicalize();
    }

    private static IEnumerable<IMethodSymbol> FindRoots(Compilation compilation, IReadOnlyList<string>? rootMethods)
    {
        if (rootMethods is null || rootMethods.Count == 0)
        {
            var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
                ?? throw new InvalidOperationException("The project has no entry point; pass RootMethods.");
            yield return entryPoint;
            yield break;
        }

        foreach (var qualified in rootMethods)
        {
            var split = qualified.LastIndexOf('.');
            var type = split > 0 ? compilation.GetTypeByMetadataName(qualified[..split]) : null;
            var methods = type?.GetMembers(qualified[(split + 1)..]).OfType<IMethodSymbol>().ToList() ?? [];
            if (methods.Count == 0)
            {
                throw new InvalidOperationException($"Root method '{qualified}' was not found.");
            }

            foreach (var method in methods)
            {
                yield return method;
            }
        }
    }

    /// <summary>Records which resilience library version the defaults are claimed for, and warns if it was not verified.</summary>
    private static string ResiliencePackageVersion(Compilation compilation, List<RsDiagnostic> diagnostics)
    {
        var identity = compilation.ReferencedAssemblyNames.FirstOrDefault(a => a.Name == ResilienceAssembly);
        if (identity is null)
        {
            return $"{ResilienceAssembly}@not-referenced";
        }

        if (identity.Version != StandardHandlerDefaults.VerifiedVersion)
        {
            diagnostics.Add(new RsDiagnostic(
                DiagnosticCodes.DefaultsVersionUnverified,
                Severity.Warning,
                $"Defaults were verified for {ResilienceAssembly} {StandardHandlerDefaults.VerifiedVersion}; " +
                $"this project references {identity.Version}. Package-default values may differ."));
        }

        return $"{ResilienceAssembly}@{identity.Version}";
    }
}
