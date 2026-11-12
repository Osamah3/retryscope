namespace RetryScope.Model;

/// <summary>The maximum number of attempts one logical call can make, and how far it can be trusted.</summary>
/// <param name="MaxAttempts">Upper bound on attempts per call (saturates at <see cref="int.MaxValue"/>).</param>
/// <param name="IsExact">False when any contributing value is an assumption or a handler is not modelled.</param>
/// <param name="Caveats">Human-readable reasons why the bound may be off.</param>
public sealed record AttemptBound(int MaxAttempts, bool IsExact, IReadOnlyList<string> Caveats);

/// <summary>
/// Derived quantities used by labelling, RQ1 prevalence counting and the simulator. Kept as pure functions over
/// the IR so that the human labelling guide and the code compute "effective attempts" the same way.
/// </summary>
public static class EffectiveConfiguration
{
    /// <summary>
    /// Maximum attempts per logical call for <paramref name="httpMethod"/>. Stacked handlers multiply: an outer
    /// handler's retry re-runs the inner handler's whole pipeline, so attempts are the product of (retries + 1)
    /// over every retry strategy that applies to the method.
    /// </summary>
    public static AttemptBound MaxAttemptsPerCall(ClientBinding binding, string httpMethod = "GET")
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);

        long attempts = 1;
        var exact = true;
        var caveats = new List<string>();

        foreach (var handler in binding.Handlers)
        {
            switch (handler.Kind)
            {
                case HandlerKind.Hedging:
                    exact = false;
                    caveats.Add("Hedging handler present: parallel hedged attempts are not bounded in V1.");
                    break;
                case HandlerKind.PollyV7:
                    caveats.Add("Polly v7 policy: counted from literal parameters only; not simulated in V1.");
                    break;
            }

            foreach (var retry in handler.Strategies.OfType<RetryStrategy>())
            {
                if (retry.DisabledForMethods.Contains(httpMethod, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var retries = Math.Max(0, retry.MaxRetryAttempts.Effective);
                attempts = Math.Min(attempts * (retries + 1L), int.MaxValue);

                if (retry.MaxRetryAttempts.IsAssumption)
                {
                    exact = false;
                    caveats.Add($"Retry count is {retry.MaxRetryAttempts.Provenance}: {retry.MaxRetryAttempts.Note ?? "default used"}.");
                }

                if (retry.ShouldHandle.Provenance == Provenance.Opaque)
                {
                    caveats.Add("Custom retry predicate: default transient classification assumed.");
                }
            }
        }

        return new AttemptBound((int)attempts, exact, caveats);
    }

    /// <summary>
    /// True when a call can make more than one attempt for safe (GET) or unsafe (POST) methods.
    /// This is the positive class for extractor evaluation.
    /// </summary>
    public static bool IsRetrying(ClientBinding binding)
        => MaxAttemptsPerCall(binding, "GET").MaxAttempts > 1
        || MaxAttemptsPerCall(binding, "POST").MaxAttempts > 1;

    /// <summary>Number of handlers whose pipelines contain a retry (2 or more means stacking).</summary>
    public static int RetryingHandlerCount(ClientBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.Handlers.Count(h => h.Strategies.OfType<RetryStrategy>().Any(r => r.MaxRetryAttempts.Effective > 0));
    }
}
