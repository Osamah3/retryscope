using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using RetryScope.Model;

namespace RetryScope.Extraction;

/// <summary>
/// Evaluates option values the compiler can prove constant: literals, <c>const</c> fields, enum members,
/// <c>TimeSpan.FromX(...)</c>, <c>new TimeSpan(...)</c> and <c>TimeSpan.Zero</c>. Anything else returns false and is
/// reported as unresolved by the caller; nothing is guessed.
/// </summary>
internal static class ConstantEvaluator
{
    private const string TimeSpanType = "System.TimeSpan";
    private const string PollyBackoffType = "Polly.DelayBackoffType";

    // Ticks per unit, keyed by the parameter names TimeSpan uses in its factories and constructors.
    private static readonly Dictionary<string, decimal> TicksPerUnit = new(StringComparer.Ordinal)
    {
        ["days"] = TimeSpan.TicksPerDay,
        ["hours"] = TimeSpan.TicksPerHour,
        ["minutes"] = TimeSpan.TicksPerMinute,
        ["seconds"] = TimeSpan.TicksPerSecond,
        ["milliseconds"] = TimeSpan.TicksPerMillisecond,
        ["microseconds"] = TimeSpan.TicksPerMicrosecond,
        ["ticks"] = 1,
    };

    public static bool TryInt(IOperation? operation, out int value)
    {
        value = 0;
        if (operation?.ConstantValue is not { HasValue: true, Value: { } constant } || !IsIntegral(constant))
        {
            return false;
        }

        var wide = Convert.ToInt64(constant, CultureInfo.InvariantCulture);
        if (wide is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (int)wide;
        return true;
    }

    public static bool TryDouble(IOperation? operation, out double value)
    {
        value = 0;
        if (operation?.ConstantValue is not { HasValue: true, Value: { } constant }
            || !(IsIntegral(constant) || constant is double or float or decimal))
        {
            return false;
        }

        value = Convert.ToDouble(constant, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryBool(IOperation? operation, out bool value)
    {
        value = false;
        if (operation?.ConstantValue is { HasValue: true, Value: bool constant })
        {
            value = constant;
            return true;
        }

        return false;
    }

    public static bool TryBackoff(IOperation? operation, out BackoffType value)
    {
        value = default;
        return Unwrap(operation) is IFieldReferenceOperation field
            && field.Field.ContainingType?.ToDisplayString() == PollyBackoffType
            && Enum.TryParse(field.Field.Name, ignoreCase: false, out value);
    }

    public static bool TryTimeSpan(IOperation? operation, out TimeSpan value)
    {
        value = default;
        switch (Unwrap(operation))
        {
            case IFieldReferenceOperation { Field.Name: "Zero" } field when IsTimeSpan(field.Field.ContainingType):
                value = TimeSpan.Zero;
                return true;

            case IFieldReferenceOperation { Field.Name: "InfiniteTimeSpan" } field
                when field.Field.ContainingType?.ToDisplayString() == "System.Threading.Timeout":
                value = Timeout.InfiniteTimeSpan;
                return true;

            // TimeSpan.FromSeconds(5), FromMilliseconds(500), FromMinutes(1, 30), ...
            case IInvocationOperation call when call.TargetMethod.IsStatic
                && IsTimeSpan(call.TargetMethod.ContainingType)
                && call.TargetMethod.Name.StartsWith("From", StringComparison.Ordinal):
                return TrySum(call.Arguments, UnitOf(call.TargetMethod.Name["From".Length..]), out value);

            // new TimeSpan(0, 0, 5), new TimeSpan(ticks)
            case IObjectCreationOperation create when IsTimeSpan(create.Type):
                return TrySum(create.Arguments, unitForValueParameter: null, out value);

            default:
                return false;
        }
    }

    /// <summary>Sums constant arguments, each scaled by the unit its parameter name denotes.</summary>
    private static bool TrySum(IEnumerable<IArgumentOperation> arguments, string? unitForValueParameter, out TimeSpan value)
    {
        value = default;
        decimal ticks = 0;

        foreach (var argument in arguments)
        {
            // FromSeconds(double value) names its parameter "value"; the unit comes from the method name.
            var unit = argument.Parameter?.Name == "value" ? unitForValueParameter : argument.Parameter?.Name;
            if (unit is null || !TicksPerUnit.TryGetValue(unit, out var scale)
                || argument.Value.ConstantValue is not { HasValue: true, Value: { } constant }
                || !(IsIntegral(constant) || constant is double or float))
            {
                return false;
            }

            ticks += Convert.ToDecimal(constant, CultureInfo.InvariantCulture) * scale;
        }

        if (ticks is < long.MinValue or > long.MaxValue)
        {
            return false;
        }

        value = TimeSpan.FromTicks((long)decimal.Round(ticks));
        return true;
    }

    private static string? UnitOf(string suffix) => suffix switch
    {
        "Days" => "days",
        "Hours" => "hours",
        "Minutes" => "minutes",
        "Seconds" => "seconds",
        "Milliseconds" => "milliseconds",
        "Microseconds" => "microseconds",
        "Ticks" => "ticks",
        _ => null,
    };

    private static bool IsTimeSpan(ITypeSymbol? type) => type?.ToDisplayString() == TimeSpanType;

    private static bool IsIntegral(object constant) => constant is int or long or short or byte or sbyte or uint or ushort;

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
