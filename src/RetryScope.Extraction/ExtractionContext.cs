using Microsoft.CodeAnalysis;
using RetryScope.Model;
using RsDiagnostic = RetryScope.Model.Diagnostic;

namespace RetryScope.Extraction;

/// <summary>State shared by the walker and the option readers for one extraction run.</summary>
/// <param name="rootDirectory">Repository root; reported paths are relative to it.</param>
/// <param name="resilienceVersion">Provenance note for Microsoft.Extensions.Http.Resilience defaults.</param>
/// <param name="pollyVersion">Provenance note for plain Polly v8 option defaults.</param>
/// <param name="environment">Host environment the configuration is evaluated for.</param>
internal sealed class ExtractionContext(string rootDirectory, string resilienceVersion, string pollyVersion, string environment)
{
    public string ResilienceVersion { get; } = resilienceVersion;

    public string PollyVersion { get; } = pollyVersion;

    public string Environment { get; } = environment;

    public List<RsDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Resolves an operation through the current call frame (parameters to the caller's arguments, single-assignment
    /// locals to their initializer). Set by the walker while it visits a method.
    /// </summary>
    public Func<IOperation?, IOperation?> Resolve { get; set; } = operation => operation;

    public void Warn(string code, string message, SyntaxNode? node = null) => Add(code, Severity.Warning, message, node);

    public void Info(string code, string message, SyntaxNode? node = null) => Add(code, Severity.Info, message, node);

    public SourceLocation LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var file = Path.GetRelativePath(rootDirectory, span.Path).Replace('\\', '/');
        return new SourceLocation(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    /// <summary>
    /// A literal value becomes Explicit; a predicate delegate becomes Opaque; anything else becomes Unresolved, keeping
    /// <paramref name="fallback"/>. Returns null (with a warning) when there is nothing to fall back to.
    /// </summary>
    public Value<object>? Evaluate(IOperation value, SettingKind kind, object? fallback, SourceLocation location)
    {
        if (kind == SettingKind.Predicate)
        {
            return Values.Opaque<object>(PredicateKind.Custom, $"custom predicate: {OperationHelpers.Shorten(value.Syntax)}", location);
        }

        var resolved = Resolve(value) ?? value;
        object? result = kind switch
        {
            SettingKind.Int when ConstantEvaluator.TryInt(resolved, out var i) => i,
            SettingKind.Double when ConstantEvaluator.TryDouble(resolved, out var d) => d,
            SettingKind.Bool when ConstantEvaluator.TryBool(resolved, out var b) => b,
            SettingKind.TimeSpan when ConstantEvaluator.TryTimeSpan(resolved, out var t) => t,
            SettingKind.Backoff when ConstantEvaluator.TryBackoff(resolved, out var e) => e,
            _ => null,
        };

        if (result is not null)
        {
            return Values.Explicit<object>(result, location);
        }

        if (fallback is null)
        {
            Warn(DiagnosticCodes.UnresolvedValue, $"Value '{OperationHelpers.Shorten(value.Syntax)}' could not be evaluated and has no default; ignored.", value.Syntax);
            return null;
        }

        return Values.Unresolved<object>(fallback, OperationHelpers.Shorten(value.Syntax), location);
    }

    private void Add(string code, Severity severity, string message, SyntaxNode? node) =>
        Diagnostics.Add(new RsDiagnostic(code, severity, message, node is null ? null : LocationOf(node)));
}
