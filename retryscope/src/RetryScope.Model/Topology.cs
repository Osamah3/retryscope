namespace RetryScope.Model;

/// <summary>A service (one deployable project).</summary>
/// <param name="Id">Identifier, e.g. the Aspire resource name.</param>
/// <param name="ProjectPath">Path of the project file, relative to the repository root.</param>
/// <param name="ConcurrencyLimit">Request permits, when known (testbed services set this explicitly).</param>
/// <param name="QueueLimit">Queue length before requests are rejected, when known.</param>
public sealed record Service(string Id, string ProjectPath, int? ConcurrencyLimit = null, int? QueueLimit = null);

/// <summary>Where a topology edge was learned from.</summary>
public enum EdgeSource
{
    /// <summary>An Aspire AppHost <c>WithReference</c> call.</summary>
    AppHost,

    /// <summary>Observed in OpenTelemetry traces.</summary>
    Trace,

    /// <summary>Inferred from a client binding's base address.</summary>
    ClientBinding,
}

/// <summary>Whether downstream calls from one request run one after another or concurrently.</summary>
public enum CallMode
{
    /// <summary>One after another.</summary>
    Sequential,

    /// <summary>Concurrently (fan-out).</summary>
    Parallel,
}

/// <summary>A caller-to-callee dependency between services.</summary>
/// <param name="From">Calling service id.</param>
/// <param name="To">Called service id.</param>
/// <param name="Source">Evidence for the edge.</param>
/// <param name="CallsPerRequest">Mean downstream calls per incoming request (must be positive).</param>
/// <param name="Mode">Sequential or parallel.</param>
public sealed record Edge(
    string From,
    string To,
    EdgeSource Source,
    double CallsPerRequest = 1.0,
    CallMode Mode = CallMode.Sequential);
