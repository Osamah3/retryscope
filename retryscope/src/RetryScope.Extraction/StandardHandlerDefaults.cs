using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// The standard resilience handler with every option at its package default, in pipeline order (outermost first):
/// rate limiter, total timeout, retry, circuit breaker, attempt timeout.
/// Values were read at the release tags (dotnet/extensions v10.11.0, Polly 8.4.2); see docs/verified-defaults.md.
/// They are only claimed for <see cref="VerifiedVersion"/>; any other version gets diagnostic RS009.
/// </summary>
internal static class StandardHandlerDefaults
{
    /// <summary>Microsoft.Extensions.Http.Resilience assembly version the defaults were verified against.</summary>
    public static readonly Version VerifiedVersion = new(10, 11, 0, 0);

    public static Handler Create(HandlerOrigin origin, string packageVersion, SourceLocation? location)
    {
        Value<T> D<T>(T value) where T : notnull => Values.PackageDefault(value, packageVersion);

        return new Handler(
            HandlerKind.Standard,
            origin,
            [
                new RateLimiterStrategy(D(1000), D(0)),
                new TimeoutStrategy(TimeoutRole.Total, D(TimeSpan.FromSeconds(30))),
                new RetryStrategy(
                    D(3),
                    D(TimeSpan.FromSeconds(2)),
                    D(BackoffType.Exponential),
                    D(true),
                    D(true),
                    D(PredicateKind.DefaultTransient),
                    Array.Empty<string>()),
                new CircuitBreakerStrategy(
                    D(0.1),
                    D(100),
                    D(TimeSpan.FromSeconds(30)),
                    D(TimeSpan.FromSeconds(5)),
                    D(PredicateKind.DefaultTransient)),
                new TimeoutStrategy(TimeoutRole.Attempt, D(TimeSpan.FromSeconds(10))),
            ],
            Location: location);
    }
}
