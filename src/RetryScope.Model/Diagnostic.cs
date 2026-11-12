namespace RetryScope.Model;

/// <summary>Severity of an extraction diagnostic.</summary>
public enum Severity
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Something the analysis could not fully model.</summary>
    Warning,

    /// <summary>Something that makes the result unreliable.</summary>
    Error,
}

/// <summary>An unsupported or ambiguous pattern found during extraction.</summary>
/// <param name="Code">Stable code from <see cref="DiagnosticCodes"/>.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Location">Where it was found.</param>
public sealed record Diagnostic(string Code, Severity Severity, string Message, SourceLocation? Location = null);

/// <summary>Stable diagnostic codes. Never renumber: they appear in published datasets.</summary>
public static class DiagnosticCodes
{
    /// <summary>A hedging handler was found; it is not simulated in V1.</summary>
    public const string HedgingNotSimulated = "RS001";

    /// <summary>A Polly v7 policy was found; it is counted but not simulated in V1.</summary>
    public const string PollyV7NotSimulated = "RS002";

    /// <summary>An HttpClient created with <c>new HttpClient()</c>: no resilience handler applies.</summary>
    public const string DirectHttpClient = "RS003";

    /// <summary>A value could not be resolved statically; a default was used.</summary>
    public const string UnresolvedValue = "RS004";

    /// <summary>A custom predicate or delegate; default behaviour was assumed.</summary>
    public const string OpaqueDelegate = "RS005";

    /// <summary>A keyed pipeline could not be linked to an HttpClient call.</summary>
    public const string UnlinkedPipeline = "RS006";

    /// <summary>The configuration would fail the standard handler's startup validation.</summary>
    public const string InvalidStandardOptions = "RS007";

    /// <summary>The project could not be fully loaded or compiled; results may be incomplete.</summary>
    public const string WorkspaceLoad = "RS008";

    /// <summary>Package defaults were verified for a different library version than the one the project references.</summary>
    public const string DefaultsVersionUnverified = "RS009";

    /// <summary>A call on the registration path did not bind (usually compiler errors); anything it registers is missed.</summary>
    public const string UnboundCall = "RS010";

    /// <summary>A client outside V1 scope (e.g. gRPC) was registered; it is reported, not modelled.</summary>
    public const string OutOfScopeClient = "RS011";
}
