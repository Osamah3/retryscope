namespace RetryScope.Model;

/// <summary>Where the analysed code came from.</summary>
/// <param name="RepositoryUrl">Repository URL, when known.</param>
/// <param name="CommitSha">Exact commit analysed.</param>
public sealed record SourceInfo(string? RepositoryUrl = null, string? CommitSha = null);

/// <summary>
/// The intermediate representation: every service, HttpClient binding, topology edge and diagnostic for one
/// analysed system. Extractors write it, the simulator reads it.
/// </summary>
/// <param name="SchemaVersion">IR schema version; see <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="Source">Where the analysed code came from.</param>
/// <param name="Services">Services.</param>
/// <param name="Bindings">HttpClient bindings.</param>
/// <param name="Edges">Topology edges.</param>
/// <param name="Diagnostics">Unsupported or ambiguous patterns.</param>
public sealed record ResilienceGraph(
    string SchemaVersion,
    SourceInfo Source,
    IReadOnlyList<Service> Services,
    IReadOnlyList<ClientBinding> Bindings,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The schema version this library writes. Bump on any breaking change and record it in docs/adr.</summary>
    public const string CurrentSchemaVersion = "0.1.0";

    /// <summary>Creates a graph at the current schema version.</summary>
    public static ResilienceGraph Create(
        SourceInfo source,
        IEnumerable<Service> services,
        IEnumerable<ClientBinding> bindings,
        IEnumerable<Edge>? edges = null,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bindings);

        return new ResilienceGraph(
            CurrentSchemaVersion,
            source,
            services.ToList(),
            bindings.ToList(),
            (edges ?? Enumerable.Empty<Edge>()).ToList(),
            (diagnostics ?? Enumerable.Empty<Diagnostic>()).ToList());
    }

    /// <summary>
    /// Returns a copy with every top-level list in a stable order (ordinal string comparison), so that the same
    /// system always serialises to byte-identical JSON regardless of discovery order.
    /// Handler and strategy order is semantic and is never changed.
    /// </summary>
    public ResilienceGraph Canonicalize() => this with
    {
        Services = Services.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
        Bindings = Bindings.OrderBy(b => b.Id, StringComparer.Ordinal).ToList(),
        Edges = Edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Source)
            .ToList(),
        Diagnostics = Diagnostics
            .OrderBy(d => d.Location?.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.Line ?? 0)
            .ThenBy(d => d.Location?.Column ?? 0)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList(),
    };
}
