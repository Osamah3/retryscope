using System.Text.Json.Serialization;

namespace RetryScope.Model;

/// <summary>What kind of HttpClient message handler carries the pipeline.</summary>
public enum HandlerKind
{
    /// <summary><c>AddStandardResilienceHandler</c> (Microsoft.Extensions.Http.Resilience).</summary>
    Standard,

    /// <summary><c>AddResilienceHandler(name, builder => ...)</c> with an explicit Polly v8 pipeline.</summary>
    Custom,

    /// <summary><c>AddStandardHedgingHandler</c>: recorded but not simulated in V1.</summary>
    Hedging,

    /// <summary>Polly v7 via <c>AddPolicyHandler</c> / <c>AddTransientHttpErrorPolicy</c>: recorded, not simulated in V1.</summary>
    PollyV7,
}

/// <summary>Where the handler was registered.</summary>
public enum HandlerOrigin
{
    /// <summary>Process-wide, via <c>ConfigureHttpClientDefaults</c> (this is what Aspire ServiceDefaults does).</summary>
    Defaults,

    /// <summary>On one named or typed client.</summary>
    PerClient,
}

/// <summary>
/// One resilience handler on an HttpClient. A client can carry several (stacking); they are listed outermost first,
/// and an outer handler's retry re-runs the inner handlers' whole pipelines.
/// </summary>
/// <param name="Kind">Handler type.</param>
/// <param name="Origin">Registration origin.</param>
/// <param name="Strategies">Pipeline strategies, outermost first.</param>
/// <param name="PipelineName">The pipeline name, when one was given.</param>
/// <param name="Location">Where the handler was registered.</param>
public sealed record Handler(
    HandlerKind Kind,
    HandlerOrigin Origin,
    IReadOnlyList<Strategy> Strategies,
    string? PipelineName = null,
    SourceLocation? Location = null)
{
    /// <summary>Whether the V1 simulator models this handler's behaviour.</summary>
    [JsonIgnore]
    public bool IsSimulated => Kind is HandlerKind.Standard or HandlerKind.Custom;
}
