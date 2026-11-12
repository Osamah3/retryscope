using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// The settings of one Polly options object (e.g. <c>new HttpRetryStrategyOptions { MaxRetryAttempts = 2 }</c>):
/// each property starts at its package default and object-initializer assignments overwrite it.
/// </summary>
internal sealed class OptionBag
{
    private readonly Dictionary<string, (SettingKind Kind, Value<object>? Value)> _items = new(StringComparer.Ordinal);

    /// <summary>Declares a property; a null default means "absent unless set".</summary>
    public OptionBag Define(string name, SettingKind kind, object? defaultValue, string version)
    {
        _items[name] = (kind, defaultValue is null ? null : new Value<object>(defaultValue, Provenance.PackageDefault, null, version));
        return this;
    }

    /// <summary>
    /// Reads an options argument. An object creation with an initializer is interpreted; anything else (a variable, a
    /// factory call) leaves every setting unresolved.
    /// </summary>
    public void Read(IOperation? options, ExtractionContext context)
    {
        if (OperationHelpers.Unwrap(options) is not IObjectCreationOperation create)
        {
            if (options is not null)
            {
                MarkAllUnresolved($"options not created inline: {OperationHelpers.Shorten(options.Syntax)}", context.LocationOf(options.Syntax));
                context.Warn(DiagnosticCodes.UnresolvedValue, $"Options '{OperationHelpers.Shorten(options.Syntax)}' are not created inline; settings unresolved.", options.Syntax);
            }

            return;
        }

        foreach (var initializer in create.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
        {
            if (initializer is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation property } assignment)
            {
                context.Warn(DiagnosticCodes.UnresolvedValue, $"Initializer not interpreted: {OperationHelpers.Shorten(initializer.Syntax)}", initializer.Syntax);
                continue;
            }

            var name = property.Property.Name;
            if (!_items.TryGetValue(name, out var item))
            {
                context.Info(DiagnosticCodes.UnresolvedValue, $"Option '{name}' is not modelled; ignored.", assignment.Syntax);
                continue;
            }

            if (context.Evaluate(assignment.Value, item.Kind, item.Value?.Effective, context.LocationOf(assignment.Syntax)) is { } value)
            {
                _items[name] = (item.Kind, value);
            }
        }
    }

    /// <summary>The current value of a property, or null if it has none.</summary>
    public object? Current(string name) => _items[name].Value?.Effective;

    /// <summary>Replaces a property's value (for options passed as plain arguments rather than an options object).</summary>
    public void Override(string name, Value<object> value) => _items[name] = (_items[name].Kind, value);

    public Value<T> Get<T>(string name) where T : notnull =>
        GetOptional<T>(name) ?? throw new InvalidOperationException($"Option '{name}' has no value.");

    public Value<T>? GetOptional<T>(string name) where T : notnull =>
        _items[name].Value is { } value ? new Value<T>((T)value.Effective, value.Provenance, value.Location, value.Note) : null;

    private void MarkAllUnresolved(string reason, SourceLocation location)
    {
        foreach (var name in _items.Keys.ToList())
        {
            var (kind, value) = _items[name];
            if (value is not null)
            {
                _items[name] = (kind, Values.Unresolved<object>(value.Effective, reason, location));
            }
        }
    }
}
