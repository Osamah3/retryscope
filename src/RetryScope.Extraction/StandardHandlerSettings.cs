using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>The value types a standard-handler setting can have.</summary>
internal enum SettingKind
{
    Int,
    Double,
    Bool,
    TimeSpan,
    Backoff,
    Predicate,
}

/// <summary>
/// The settings of one standard resilience handler, keyed by their path on <c>HttpStandardResilienceOptions</c>
/// (e.g. <c>Retry.MaxRetryAttempts</c>). Every setting starts at its package default (docs/verified-defaults.md); the
/// options lambda and configuration readers overwrite individual settings, keeping provenance per value.
/// </summary>
internal sealed class StandardHandlerSettings
{
    private static readonly (string Path, SettingKind Kind, object Default)[] Table =
    [
        ("RateLimiter.DefaultRateLimiterOptions.PermitLimit", SettingKind.Int, 1000),
        ("RateLimiter.DefaultRateLimiterOptions.QueueLimit", SettingKind.Int, 0),
        ("TotalRequestTimeout.Timeout", SettingKind.TimeSpan, TimeSpan.FromSeconds(30)),
        ("Retry.MaxRetryAttempts", SettingKind.Int, 3),
        ("Retry.Delay", SettingKind.TimeSpan, TimeSpan.FromSeconds(2)),
        ("Retry.BackoffType", SettingKind.Backoff, BackoffType.Exponential),
        ("Retry.UseJitter", SettingKind.Bool, true),
        ("Retry.ShouldRetryAfterHeader", SettingKind.Bool, true),
        ("Retry.ShouldHandle", SettingKind.Predicate, PredicateKind.DefaultTransient),
        ("CircuitBreaker.FailureRatio", SettingKind.Double, 0.1),
        ("CircuitBreaker.MinimumThroughput", SettingKind.Int, 100),
        ("CircuitBreaker.SamplingDuration", SettingKind.TimeSpan, TimeSpan.FromSeconds(30)),
        ("CircuitBreaker.BreakDuration", SettingKind.TimeSpan, TimeSpan.FromSeconds(5)),
        ("CircuitBreaker.ShouldHandle", SettingKind.Predicate, PredicateKind.DefaultTransient),
        ("AttemptTimeout.Timeout", SettingKind.TimeSpan, TimeSpan.FromSeconds(10)),
    ];

    /// <summary>Optional settings: no default value (absent unless set).</summary>
    private static readonly (string Path, SettingKind Kind)[] Optional =
    [
        ("Retry.MaxDelay", SettingKind.TimeSpan),
    ];

    private readonly Dictionary<string, Value<object>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HTTP methods for which retries are disabled (<c>o.Retry.DisableFor...</c>), upper case.</summary>
    public SortedSet<string> DisabledForMethods { get; } = new(StringComparer.Ordinal);

    public StandardHandlerSettings(string packageVersion)
    {
        foreach (var (path, _, value) in Table)
        {
            _values[path] = new Value<object>(value, Provenance.PackageDefault, null, packageVersion);
        }
    }

    /// <summary>Every modelled path, with its kind (used by the configuration reader).</summary>
    public static IEnumerable<(string Path, SettingKind Kind)> Paths =>
        Table.Select(t => (t.Path, t.Kind)).Concat(Optional);

    /// <summary>The kind of a modelled path; false for paths the model does not represent.</summary>
    public static bool TryGetKind(string path, out SettingKind kind)
    {
        foreach (var (p, k) in Paths)
        {
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            {
                kind = k;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>The current value of a path, so an unresolved override can fall back to it.</summary>
    public object? Current(string path) => _values.TryGetValue(path, out var value) ? value.Effective : null;

    public void Set(string path, Value<object> value) => _values[path] = value;

    /// <summary>Builds the handler in pipeline order, outermost first (verified-defaults.md).</summary>
    public Handler Build(HandlerOrigin origin, SourceLocation? location) => new(
        HandlerKind.Standard,
        origin,
        [
            new RateLimiterStrategy(
                Get<int>("RateLimiter.DefaultRateLimiterOptions.PermitLimit"),
                Get<int>("RateLimiter.DefaultRateLimiterOptions.QueueLimit")),
            new TimeoutStrategy(TimeoutRole.Total, Get<TimeSpan>("TotalRequestTimeout.Timeout")),
            new RetryStrategy(
                Get<int>("Retry.MaxRetryAttempts"),
                Get<TimeSpan>("Retry.Delay"),
                Get<BackoffType>("Retry.BackoffType"),
                Get<bool>("Retry.UseJitter"),
                Get<bool>("Retry.ShouldRetryAfterHeader"),
                Get<PredicateKind>("Retry.ShouldHandle"),
                [.. DisabledForMethods],
                _values.ContainsKey("Retry.MaxDelay") ? Get<TimeSpan>("Retry.MaxDelay") : null),
            new CircuitBreakerStrategy(
                Get<double>("CircuitBreaker.FailureRatio"),
                Get<int>("CircuitBreaker.MinimumThroughput"),
                Get<TimeSpan>("CircuitBreaker.SamplingDuration"),
                Get<TimeSpan>("CircuitBreaker.BreakDuration"),
                Get<PredicateKind>("CircuitBreaker.ShouldHandle")),
            new TimeoutStrategy(TimeoutRole.Attempt, Get<TimeSpan>("AttemptTimeout.Timeout")),
        ],
        Location: location);

    private Value<T> Get<T>(string path) where T : notnull
    {
        var value = _values[path];
        return new Value<T>((T)value.Effective, value.Provenance, value.Location, value.Note);
    }
}
