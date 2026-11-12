using Microsoft.CodeAnalysis;

namespace RetryScope.Extraction;

/// <summary>
/// A library method identified by assembly, containing type and name. Calls are matched on the method the compiler
/// resolved, never on the text of the call, so a user's own method called <c>AddStandardResilienceHandler</c> does not
/// match. Comparing by name (instead of symbol equality) is deliberate: the same library method seen from two projects
/// is two different symbol instances.
/// </summary>
internal sealed record KnownMethod(string Assembly, string Type, string Name)
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
            && declared.Name == Name
            && type.ContainingAssembly?.Name == Assembly
            && $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}" == Type;
    }
}

/// <summary>The library APIs the extractor understands. Each was checked against the library source (see comments).</summary>
internal static class KnownApis
{
    // dotnet/runtime v10.0.0, src/libraries/Microsoft.Extensions.Http/src/DependencyInjection/
    // HttpClientFactoryServiceCollectionExtensions.cs
    private const string HttpAssembly = "Microsoft.Extensions.Http";
    private const string HttpFactoryExtensions = "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions";

    // dotnet/extensions v10.11.0 (25f07af), src/Libraries/Microsoft.Extensions.Http.Resilience/Resilience/
    // ResilienceHttpClientBuilderExtensions.StandardResilience.cs
    private const string ResilienceAssembly = "Microsoft.Extensions.Http.Resilience";
    private const string ResilienceBuilderExtensions = "Microsoft.Extensions.DependencyInjection.ResilienceHttpClientBuilderExtensions";

    public static readonly KnownMethod AddHttpClient = new(HttpAssembly, HttpFactoryExtensions, "AddHttpClient");
    public static readonly KnownMethod ConfigureHttpClientDefaults = new(HttpAssembly, HttpFactoryExtensions, "ConfigureHttpClientDefaults");
    public static readonly KnownMethod AddStandardResilienceHandler = new(ResilienceAssembly, ResilienceBuilderExtensions, "AddStandardResilienceHandler");

    // Same release, Resilience/HttpStandardResiliencePipelineBuilderExtensions.cs. The lambda and section overloads of
    // AddStandardResilienceHandler are implemented as AddStandardResilienceHandler().Configure(...).
    public static readonly KnownMethod ConfigureStandardHandler = new(
        ResilienceAssembly, "Microsoft.Extensions.Http.Resilience.HttpStandardResiliencePipelineBuilderExtensions", "Configure");

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

    /// <summary>Metadata name of the builder interface every handler registration extends.</summary>
    public const string HttpClientBuilderInterface = "Microsoft.Extensions.DependencyInjection.IHttpClientBuilder";
}
