using Microsoft.CodeAnalysis;

namespace RetryScope.Extraction;

/// <summary>
/// A library method identified by assembly, containing type and name. Calls are matched on the method the compiler
/// resolved, never on the text of the call, so a user's own method called <c>AddStandardResilienceHandler</c> does not
/// match. Comparing by name (instead of symbol equality) is deliberate: the same library method seen from two projects
/// is two different symbol instances. <paramref name="Type"/> null matches any type in the assembly (used for Polly v7,
/// whose many syntax classes all live in assembly <c>Polly</c>).
/// </summary>
internal sealed record KnownMethod(string Assembly, string? Type, params string[] Names)
{
    public bool Matches(IMethodSymbol? method)
    {
        if (method is null)
        {
            return false;
        }

        // Extension calls are "reduced" (receiver.M()); compare the declared, unconstructed method.
        var declared = (method.ReducedFrom ?? method).OriginalDefinition;
        var type = declared.ContainingType;
        return type is not null
            && Names.Contains(declared.Name, StringComparer.Ordinal)
            && type.ContainingAssembly?.Name == Assembly
            && (Type is null || $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}" == Type);
    }
}

/// <summary>The library APIs the extractor understands. Each was checked against the library source (see comments).</summary>
internal static class KnownApis
{
    // dotnet/runtime v10.0.0, src/libraries/Microsoft.Extensions.Http/src/DependencyInjection/
    // HttpClientFactoryServiceCollectionExtensions.cs
    private const string HttpAssembly = "Microsoft.Extensions.Http";
    private const string HttpFactoryExtensions = "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions";

    // dotnet/extensions v10.11.0 (25f07af), src/Libraries/Microsoft.Extensions.Http.Resilience/
    private const string ResilienceAssembly = "Microsoft.Extensions.Http.Resilience";
    private const string ResilienceBuilderExtensions = "Microsoft.Extensions.DependencyInjection.ResilienceHttpClientBuilderExtensions";

    // App-vNext/Polly 8.4.2, src/Polly.Core and src/Polly.RateLimiting
    private const string PollyCore = "Polly.Core";

    public static readonly KnownMethod AddHttpClient = new(HttpAssembly, HttpFactoryExtensions, "AddHttpClient");
    public static readonly KnownMethod ConfigureHttpClientDefaults = new(HttpAssembly, HttpFactoryExtensions, "ConfigureHttpClientDefaults");

    // Resilience/ResilienceHttpClientBuilderExtensions.StandardResilience.cs
    public static readonly KnownMethod AddStandardResilienceHandler = new(ResilienceAssembly, ResilienceBuilderExtensions, "AddStandardResilienceHandler");

    // Resilience/HttpStandardResiliencePipelineBuilderExtensions.cs. The lambda and section overloads of
    // AddStandardResilienceHandler are implemented as AddStandardResilienceHandler().Configure(...).
    public static readonly KnownMethod ConfigureStandardHandler = new(
        ResilienceAssembly, "Microsoft.Extensions.Http.Resilience.HttpStandardResiliencePipelineBuilderExtensions", "Configure");

    // Resilience/ResilienceHttpClientBuilderExtensions.Resilience.cs
    public static readonly KnownMethod AddResilienceHandler = new(ResilienceAssembly, ResilienceBuilderExtensions, "AddResilienceHandler");

    /// <summary>Removes every Polly v8 <c>ResilienceHandler</c> added to the client before it, defaults included.</summary>
    public static readonly KnownMethod RemoveAllResilienceHandlers = new(ResilienceAssembly, ResilienceBuilderExtensions, "RemoveAllResilienceHandlers");

    // Hedging/ResilienceHttpClientBuilderExtensions.Hedging.cs
    public static readonly KnownMethod AddStandardHedgingHandler = new(ResilienceAssembly, ResilienceBuilderExtensions, "AddStandardHedgingHandler");

    // Polly/HttpRetryStrategyOptionsExtensions.cs: DisableForUnsafeHttpMethods = DisableFor(DELETE, POST, PUT, CONNECT, PATCH).
    public static readonly KnownMethod DisableForUnsafeHttpMethods = new(
        ResilienceAssembly, "Microsoft.Extensions.Http.Resilience.HttpRetryStrategyOptionsExtensions", "DisableForUnsafeHttpMethods");

    public static readonly KnownMethod DisableFor = new(
        ResilienceAssembly, "Microsoft.Extensions.Http.Resilience.HttpRetryStrategyOptionsExtensions", "DisableFor");

    public static readonly string[] UnsafeHttpMethods = ["CONNECT", "DELETE", "PATCH", "POST", "PUT"];

    // Polly v8 pipeline builder extensions (namespace Polly).
    public static readonly KnownMethod AddRetry = new(PollyCore, "Polly.RetryResiliencePipelineBuilderExtensions", "AddRetry");
    public static readonly KnownMethod AddTimeout = new(PollyCore, "Polly.TimeoutResiliencePipelineBuilderExtensions", "AddTimeout");
    public static readonly KnownMethod AddCircuitBreaker = new(PollyCore, "Polly.CircuitBreakerResiliencePipelineBuilderExtensions", "AddCircuitBreaker");
    public static readonly KnownMethod AddConcurrencyLimiter = new("Polly.RateLimiting", "Polly.RateLimiterResiliencePipelineBuilderExtensions", "AddConcurrencyLimiter");

    /// <summary>Keyed pipelines in DI (Polly.Extensions); executions are not linked to clients in V1 (RS006).</summary>
    public static readonly KnownMethod AddResiliencePipeline = new("Polly.Extensions", null, "AddResiliencePipeline", "AddResiliencePipelines");

    // Polly v7 through Microsoft.Extensions.Http.Polly (dotnet/aspnetcore): recorded, not simulated in V1.
    public static readonly KnownMethod AddPollyV7Policy = new("Microsoft.Extensions.Http.Polly", null,
        "AddTransientHttpErrorPolicy", "AddPolicyHandler", "AddPolicyHandlerFromRegistry");

    /// <summary>Polly v7 retry policies whose first argument after the builder is <c>int retryCount</c>.</summary>
    public static readonly KnownMethod PollyV7Retry = new("Polly", null, "RetryAsync", "WaitAndRetryAsync", "Retry", "WaitAndRetry");

    /// <summary>Polly v7 policies that retry without limit.</summary>
    public static readonly KnownMethod PollyV7RetryForever = new("Polly", null,
        "RetryForeverAsync", "WaitAndRetryForeverAsync", "RetryForever", "WaitAndRetryForever");

    // dotnet/runtime v10.0.0, src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/HostEnvironmentEnvExtensions.cs
    public static readonly KnownMethod HostEnvironmentCheck = new("Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions", "IsDevelopment", "IsStaging", "IsProduction", "IsEnvironment");

    /// <summary>Metadata name of the builder interface every handler registration extends.</summary>
    public const string HttpClientBuilderInterface = "Microsoft.Extensions.DependencyInjection.IHttpClientBuilder";

    /// <summary>Options types whose defaults are the HTTP ones (exponential, jitter, Retry-After, transient predicate).</summary>
    public const string HttpRetryOptions = "Microsoft.Extensions.Http.Resilience.HttpRetryStrategyOptions";

    public const string HttpCircuitBreakerOptions = "Microsoft.Extensions.Http.Resilience.HttpCircuitBreakerStrategyOptions";

    public const string HttpClientType = "System.Net.Http.HttpClient";

    /// <summary><c>IConfiguration.GetSection(string key)</c>; matched by containing interface.</summary>
    public const string ConfigurationInterface = "Microsoft.Extensions.Configuration.IConfiguration";

    /// <summary>Static types that denote the root configuration (not a section).</summary>
    public static readonly IReadOnlySet<string> RootConfigurationTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Configuration.IConfiguration",
        "Microsoft.Extensions.Configuration.IConfigurationManager",
        "Microsoft.Extensions.Configuration.IConfigurationRoot",
        "Microsoft.Extensions.Configuration.ConfigurationManager",
    };
}
