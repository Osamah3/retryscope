namespace RetryScope.Model;

/// <summary>Where a value in the IR came from. Drives how much a result can be trusted.</summary>
public enum Provenance
{
    /// <summary>A literal in source code, e.g. <c>MaxRetryAttempts = 5</c>.</summary>
    Explicit,

    /// <summary>Read from appsettings.json or appsettings.{Environment}.json.</summary>
    Config,

    /// <summary>Not set; resolved from the defaults of the exact referenced package version.</summary>
    PackageDefault,

    /// <summary>Not set and the package version could not be resolved; the latest known default is used.</summary>
    Assumed,

    /// <summary>A non-constant expression, environment variable or secret store; a default is used instead.</summary>
    Unresolved,

    /// <summary>A custom delegate (e.g. a retry predicate) whose behaviour cannot be read statically.</summary>
    Opaque,
}

/// <summary>A location in the analysed source, so labellers and reviewers can check every extracted value.</summary>
public sealed record SourceLocation(string File, int Line, int Column);

/// <summary>
/// A value together with its provenance. <see cref="Effective"/> is always the value the analysis uses:
/// for <see cref="Provenance.Unresolved"/>, <see cref="Provenance.Assumed"/> and <see cref="Provenance.Opaque"/>
/// it is a fallback, and <see cref="IsAssumption"/> is true so results can be flagged.
/// </summary>
/// <param name="Effective">The value used by analysis and simulation.</param>
/// <param name="Provenance">Where the value came from.</param>
/// <param name="Location">Source location, when the value appears in code or configuration.</param>
/// <param name="Note">Extra context: the unresolved expression, the configuration key, or the package version.</param>
public sealed record Value<T>(T Effective, Provenance Provenance, SourceLocation? Location = null, string? Note = null)
    where T : notnull
{
    /// <summary>True when the effective value is a fallback rather than something read from the system.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAssumption => Provenance is Provenance.Assumed or Provenance.Unresolved or Provenance.Opaque;
}

/// <summary>Factory methods for <see cref="Value{T}"/>, one per provenance.</summary>
public static class Values
{
    /// <summary>A literal found in code.</summary>
    public static Value<T> Explicit<T>(T value, SourceLocation? location = null) where T : notnull
        => new(value, Provenance.Explicit, location);

    /// <summary>A value read from a configuration file; <paramref name="key"/> is the configuration path.</summary>
    public static Value<T> FromConfig<T>(T value, string key, SourceLocation? location = null) where T : notnull
        => new(value, Provenance.Config, location, key);

    /// <summary>A package default, resolved for a known package version.</summary>
    public static Value<T> PackageDefault<T>(T value, string packageVersion) where T : notnull
        => new(value, Provenance.PackageDefault, null, packageVersion);

    /// <summary>A default assumed because the package version is unknown.</summary>
    public static Value<T> Assumed<T>(T value, string reason) where T : notnull
        => new(value, Provenance.Assumed, null, reason);

    /// <summary>A value that could not be read statically; <paramref name="fallback"/> is used instead.</summary>
    public static Value<T> Unresolved<T>(T fallback, string expression, SourceLocation? location = null) where T : notnull
        => new(fallback, Provenance.Unresolved, location, expression);

    /// <summary>Behaviour defined by a custom delegate; <paramref name="assumed"/> is used instead.</summary>
    public static Value<T> Opaque<T>(T assumed, string description, SourceLocation? location = null) where T : notnull
        => new(assumed, Provenance.Opaque, location, description);
}
