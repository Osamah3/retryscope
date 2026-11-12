using RetryScope.Model;

namespace RetryScope.Model.Tests;

/// <summary>
/// Builders for test graphs. The standard-handler defaults below were read from source on 11 Nov 2026
/// at the pinned release tags (Polly 8.4.2, dotnet/extensions v10.11.0); see docs/verified-defaults.md.
/// </summary>
internal static class TestData
{
    public const string DefaultsVersion = "Microsoft.Extensions.Http.Resilience@10.11.0.0";

    public static Handler StandardHandler(HandlerOrigin origin = HandlerOrigin.Defaults, RetryStrategy? retry = null) =>
        new(HandlerKind.Standard, origin,
        [
            new RateLimiterStrategy(Values.PackageDefault(1000, DefaultsVersion), Values.PackageDefault(0, DefaultsVersion)),
            new TimeoutStrategy(TimeoutRole.Total, Values.PackageDefault(TimeSpan.FromSeconds(30), DefaultsVersion)),
            retry ?? DefaultRetry(),
            new CircuitBreakerStrategy(
                Values.PackageDefault(0.1, DefaultsVersion),
                Values.PackageDefault(100, DefaultsVersion),
                Values.PackageDefault(TimeSpan.FromSeconds(30), DefaultsVersion),
                Values.PackageDefault(TimeSpan.FromSeconds(5), DefaultsVersion),
                Values.PackageDefault(PredicateKind.DefaultTransient, DefaultsVersion)),
            new TimeoutStrategy(TimeoutRole.Attempt, Values.PackageDefault(TimeSpan.FromSeconds(10), DefaultsVersion)),
        ]);

    public static RetryStrategy DefaultRetry(Value<int>? maxRetryAttempts = null, IReadOnlyList<string>? disabledFor = null) =>
        new(
            maxRetryAttempts ?? Values.PackageDefault(3, DefaultsVersion),
            Values.PackageDefault(TimeSpan.FromSeconds(2), DefaultsVersion),
            Values.PackageDefault(BackoffType.Exponential, DefaultsVersion),
            Values.PackageDefault(true, DefaultsVersion),
            Values.PackageDefault(true, DefaultsVersion),
            Values.PackageDefault(PredicateKind.DefaultTransient, DefaultsVersion),
            disabledFor ?? Array.Empty<string>());

    public static ClientBinding Binding(string id, params Handler[] handlers) =>
        new(id, "api", "catalog", ClientKind.Named, handlers, TargetService: "catalog");

    public static ResilienceGraph Graph(params ClientBinding[] bindings) =>
        ResilienceGraph.Create(
            new SourceInfo("https://example.invalid/repo", "0000000"),
            [new Service("api", "src/Api/Api.csproj"), new Service("catalog", "src/Catalog/Catalog.csproj")],
            bindings,
            [new Edge("api", "catalog", EdgeSource.AppHost)]);
}
