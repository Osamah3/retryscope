namespace RetryScope.Model;

/// <summary>How the HttpClient is registered with IHttpClientFactory.</summary>
public enum ClientKind
{
    /// <summary><c>AddHttpClient("name")</c>.</summary>
    Named,

    /// <summary><c>AddHttpClient&lt;TClient&gt;()</c>.</summary>
    Typed,

    /// <summary>The unnamed default client (affected only by process-wide defaults).</summary>
    Default,

    /// <summary><c>new HttpClient()</c>: bypasses IHttpClientFactory, so no factory handlers apply (diagnostic RS003).</summary>
    Direct,
}

/// <summary>How the resilience pipeline instance (and so circuit-breaker state) is shared.</summary>
public enum PipelineKeying
{
    /// <summary>One pipeline instance per client name (the default).</summary>
    PerClientName,

    /// <summary>One pipeline instance per request authority (<c>SelectPipelineByAuthority</c>).</summary>
    PerAuthority,
}

/// <summary>
/// One HttpClient registration in one service: the unit of labelling and of extractor evaluation.
/// </summary>
/// <param name="Id">Stable identifier, unique within the graph.</param>
/// <param name="OwnerService">The service (project) that makes the calls.</param>
/// <param name="ClientName">Client name, or the typed client's type name.</param>
/// <param name="Kind">Named, typed or default.</param>
/// <param name="Handlers">Resilience handlers, outermost first, after any RemoveAllResilienceHandlers() is applied.</param>
/// <param name="TargetService">The service the client calls, when it can be resolved.</param>
/// <param name="BaseAddress">The configured base address, with provenance.</param>
/// <param name="Keying">How the pipeline instance is shared.</param>
/// <param name="Conditional">True when the registration sits inside a condition (e.g. an environment check).</param>
/// <param name="Location">Where the client is registered.</param>
public sealed record ClientBinding(
    string Id,
    string OwnerService,
    string ClientName,
    ClientKind Kind,
    IReadOnlyList<Handler> Handlers,
    string? TargetService = null,
    Value<string>? BaseAddress = null,
    PipelineKeying Keying = PipelineKeying.PerClientName,
    bool Conditional = false,
    SourceLocation? Location = null);
