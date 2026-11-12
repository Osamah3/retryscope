using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// The standard resilience handler with every option at its package default. The values live in
/// <see cref="StandardHandlerSettings"/>; they were read at the release tags (dotnet/extensions v10.11.0,
/// Polly 8.4.2), see docs/verified-defaults.md. They are only claimed for <see cref="VerifiedVersions"/>; any other
/// version gets diagnostic RS009.
/// </summary>
internal static class StandardHandlerDefaults
{
    /// <summary>
    /// Microsoft.Extensions.Http.Resilience assembly versions the defaults are verified for. 10.10.0 is included because
    /// its library source is identical to 10.11.0 (dev-set finding D2; docs/verified-defaults.md).
    /// </summary>
    public static readonly IReadOnlySet<Version> VerifiedVersions = new HashSet<Version> { new(10, 10, 0, 0), new(10, 11, 0, 0) };

    public static Handler Create(HandlerOrigin origin, string packageVersion, SourceLocation? location) =>
        new StandardHandlerSettings(packageVersion).Build(origin, location);
}
