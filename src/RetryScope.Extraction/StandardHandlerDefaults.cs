using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// The standard resilience handler with every option at its package default. The values live in
/// <see cref="StandardHandlerSettings"/>; they were read at the release tags (dotnet/extensions v10.11.0,
/// Polly 8.4.2), see docs/verified-defaults.md. They are only claimed for <see cref="VerifiedVersion"/>; any other
/// version gets diagnostic RS009.
/// </summary>
internal static class StandardHandlerDefaults
{
    /// <summary>Microsoft.Extensions.Http.Resilience assembly version the defaults were verified against.</summary>
    public static readonly Version VerifiedVersion = new(10, 11, 0, 0);

    public static Handler Create(HandlerOrigin origin, string packageVersion, SourceLocation? location) =>
        new StandardHandlerSettings(packageVersion).Build(origin, location);
}
