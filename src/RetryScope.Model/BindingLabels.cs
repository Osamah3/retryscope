namespace RetryScope.Model;

/// <summary>
/// One binding expressed in the labelling guide's fields (docs/labelling-guide.md §3–4). Extractor output and human
/// labels are compared through this view, so both sides are measured with the same definitions.
/// </summary>
/// <param name="Client">Client name (typed clients: the short type name).</param>
/// <param name="ClientKind">named, typed or default.</param>
/// <param name="Target">Target service (host of a constant BaseAddress), or null.</param>
/// <param name="AttemptBoundGet">Attempt bound for GET; null when not determinable (hedging).</param>
/// <param name="AttemptBoundPost">Attempt bound for POST; null when not determinable.</param>
/// <param name="RetryCountSource">none, default, explicit, config, unresolved or opaque (guide §4.4).</param>
/// <param name="AttemptTimeoutSeconds">Innermost attempt timeout (guide §4.6), or null.</param>
/// <param name="TotalTimeoutSeconds">Outermost total timeout (guide §4.6), or null.</param>
/// <param name="Breaker">Whether any handler has a circuit breaker.</param>
/// <param name="RetryHandlers">Number of handlers containing a retry.</param>
/// <param name="Conditional">Whether the registration is conditional.</param>
public sealed record BindingLabels(
    string Client,
    string ClientKind,
    string? Target,
    int? AttemptBoundGet,
    int? AttemptBoundPost,
    string RetryCountSource,
    double? AttemptTimeoutSeconds,
    double? TotalTimeoutSeconds,
    bool Breaker,
    int RetryHandlers,
    bool Conditional)
{
    /// <summary>Projects a binding onto the labelling guide's fields.</summary>
    public static BindingLabels From(ClientBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // Hedging makes the bound undeterminable in V1; the guide labels it "?".
        var hedged = binding.Handlers.Any(h => h.Kind == HandlerKind.Hedging);
        var retries = binding.Handlers.SelectMany(h => h.Strategies.OfType<RetryStrategy>()).ToList();

        // Handlers are listed outermost first. Attempt timeout: innermost handler that has one.
        // Total timeout: outermost handler that has one.
        var attemptTimeout = binding.Handlers.Reverse()
            .SelectMany(h => h.Strategies.OfType<TimeoutStrategy>().Where(t => t.Role == TimeoutRole.Attempt))
            .Select(t => (double?)t.Timeout.Effective.TotalSeconds)
            .FirstOrDefault();
        var totalTimeout = binding.Handlers
            .SelectMany(h => h.Strategies.OfType<TimeoutStrategy>().Where(t => t.Role == TimeoutRole.Total))
            .Select(t => (double?)t.Timeout.Effective.TotalSeconds)
            .FirstOrDefault();

        return new BindingLabels(
            binding.ClientName,
            binding.Kind.ToString().ToLowerInvariant(),
            binding.TargetService,
            hedged ? null : EffectiveConfiguration.MaxAttemptsPerCall(binding, "GET").MaxAttempts,
            hedged ? null : EffectiveConfiguration.MaxAttemptsPerCall(binding, "POST").MaxAttempts,
            RetryCountSourceOf(retries),
            attemptTimeout,
            totalTimeout,
            binding.Handlers.Any(h => h.Strategies.OfType<CircuitBreakerStrategy>().Any()),
            EffectiveConfiguration.RetryingHandlerCount(binding),
            binding.Conditional);
    }

    /// <summary>
    /// Guide §4.4. With several retry strategies, the least certain source wins:
    /// unresolved, then opaque, then config, then explicit, then default.
    /// </summary>
    private static string RetryCountSourceOf(IReadOnlyList<RetryStrategy> retries)
    {
        if (retries.Count == 0)
        {
            return "none";
        }

        if (retries.Any(r => r.MaxRetryAttempts.Provenance is Provenance.Unresolved or Provenance.Assumed))
        {
            return "unresolved";
        }

        if (retries.Any(r => r.ShouldHandle.Provenance == Provenance.Opaque || r.MaxRetryAttempts.Provenance == Provenance.Opaque))
        {
            return "opaque";
        }

        if (retries.Any(r => r.MaxRetryAttempts.Provenance == Provenance.Config))
        {
            return "config";
        }

        return retries.Any(r => r.MaxRetryAttempts.Provenance == Provenance.Explicit) ? "explicit" : "default";
    }
}
