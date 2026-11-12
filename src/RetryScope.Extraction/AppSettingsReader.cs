using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// Reads a project's appsettings.json and appsettings.{Environment}.json with the runtime's own configuration library,
/// so key matching (case-insensitive, <c>:</c>-separated) and override order are those of the real host.
/// </summary>
/// <remarks>
/// Only these two files are read. Environment variables, command-line arguments, user secrets and other providers
/// are not visible to static analysis; a value supplied only by them is reported as its package default, and the
/// limitation is stated in every config-bound value's note.
/// </remarks>
internal sealed class AppSettingsReader : IDisposable
{
    private readonly IConfigurationRoot _root;

    public AppSettingsReader(string projectDirectory, string environment)
    {
        _root = new ConfigurationBuilder()
            .SetBasePath(projectDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .Build();
    }

    /// <summary>
    /// Overwrites every modelled setting present under <paramref name="sectionPath"/> (e.g. <c>Resilience:Inventory</c>).
    /// Values that do not parse are recorded as unresolved, keeping the previous value as fallback.
    /// </summary>
    public void Apply(string sectionPath, StandardHandlerSettings settings, SourceLocation? bindingLocation)
    {
        foreach (var (path, kind) in StandardHandlerSettings.Paths)
        {
            var key = $"{sectionPath}:{path.Replace('.', ':')}";
            if (Lookup(key) is not { } hit)
            {
                continue;
            }

            var (raw, file) = hit;
            var source = $"{key} in {file} (appsettings files only; other configuration providers not read)";
            if (Parse(raw, kind) is { } parsed)
            {
                settings.Set(path, Values.FromConfig<object>(parsed, source, bindingLocation));
            }
            else if (settings.Current(path) is { } fallback)
            {
                settings.Set(path, Values.Unresolved<object>(fallback, $"{source}: '{raw}' could not be parsed as {kind}", bindingLocation));
            }
        }
    }

    public void Dispose() => (_root as IDisposable)?.Dispose();

    /// <summary>The winning value for a key and the file it came from (later providers override earlier ones).</summary>
    private (string Raw, string File)? Lookup(string key)
    {
        foreach (var provider in _root.Providers.Reverse())
        {
            if (provider.TryGet(key, out var raw) && raw is not null)
            {
                var file = provider is JsonConfigurationProvider json ? json.Source.Path ?? "appsettings" : "appsettings";
                return (raw, file);
            }
        }

        return null;
    }

    private static object? Parse(string raw, SettingKind kind) => kind switch
    {
        SettingKind.Int when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        SettingKind.Double when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        SettingKind.Bool when bool.TryParse(raw, out var b) => b,
        SettingKind.TimeSpan when TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var t) => t,
        SettingKind.Backoff when Enum.TryParse<BackoffType>(raw, ignoreCase: true, out var e) => e,
        _ => null, // predicates cannot come from configuration
    };
}
