using System.Text.Json.Serialization;

namespace RetryScope.Model;

/// <summary>Mirrors Polly's <c>DelayBackoffType</c>.</summary>
public enum BackoffType
{
    /// <summary>Same delay before every retry.</summary>
    Constant,

    /// <summary>Delay grows linearly with the attempt number.</summary>
    Linear,

    /// <summary>Delay doubles with each attempt (with jitter: Polly's decorrelated jitter V2).</summary>
    Exponential,
}

/// <summary>Whether a timeout bounds the whole call (including retries) or a single attempt.</summary>
public enum TimeoutRole
{
    /// <summary>Bounds the whole call, including every retry.</summary>
    Total,

    /// <summary>Bounds one attempt.</summary>
    Attempt,
}

/// <summary>Which outcomes a retry or circuit breaker treats as failures.</summary>
public enum PredicateKind
{
    /// <summary>The default HTTP transient predicate: HttpRequestException, TimeoutRejectedException, 5xx, 408, 429.</summary>
    DefaultTransient,

    /// <summary>A custom delegate; see the value's provenance (normally <see cref="Provenance.Opaque"/>).</summary>
    Custom,
}

/// <summary>One resilience strategy inside a handler's pipeline. Order in the pipeline is outermost first.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RateLimiterStrategy), "rateLimiter")]
[JsonDerivedType(typeof(TimeoutStrategy), "timeout")]
[JsonDerivedType(typeof(RetryStrategy), "retry")]
[JsonDerivedType(typeof(CircuitBreakerStrategy), "circuitBreaker")]
public abstract record Strategy;

/// <summary>A concurrency rate limiter (the standard handler's outermost strategy).</summary>
public sealed record RateLimiterStrategy(Value<int> PermitLimit, Value<int> QueueLimit) : Strategy;

/// <summary>A timeout, either for the whole call or for one attempt.</summary>
public sealed record TimeoutStrategy(TimeoutRole Role, Value<TimeSpan> Timeout) : Strategy;

/// <summary>A retry strategy.</summary>
/// <param name="MaxRetryAttempts">Retries after the first attempt; total attempts are this plus one.</param>
/// <param name="Delay">Base delay before the first retry.</param>
/// <param name="Backoff">How the delay grows.</param>
/// <param name="UseJitter">Whether delays are randomised.</param>
/// <param name="ShouldRetryAfterHeader">Whether a Retry-After response header overrides the computed delay.</param>
/// <param name="ShouldHandle">Which outcomes are retried.</param>
/// <param name="DisabledForMethods">HTTP methods (upper case) for which retries are disabled, e.g. via DisableForUnsafeHttpMethods.</param>
/// <param name="MaxDelay">Upper bound on a single delay, when set.</param>
public sealed record RetryStrategy(
    Value<int> MaxRetryAttempts,
    Value<TimeSpan> Delay,
    Value<BackoffType> Backoff,
    Value<bool> UseJitter,
    Value<bool> ShouldRetryAfterHeader,
    Value<PredicateKind> ShouldHandle,
    IReadOnlyList<string> DisabledForMethods,
    Value<TimeSpan>? MaxDelay = null) : Strategy;

/// <summary>A circuit breaker using Polly's failure-ratio model.</summary>
public sealed record CircuitBreakerStrategy(
    Value<double> FailureRatio,
    Value<int> MinimumThroughput,
    Value<TimeSpan> SamplingDuration,
    Value<TimeSpan> BreakDuration,
    Value<PredicateKind> ShouldHandle) : Strategy;
