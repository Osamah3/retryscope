using Microsoft.CodeAnalysis.Operations;
using static RetryScope.Extraction.OperationHelpers;

namespace RetryScope.Extraction;

/// <summary>
/// Decides branch conditions that depend only on the host environment, for the environment being extracted
/// (labelling guide hard case 1: label the Production behaviour). Returns null when the condition depends on anything
/// else; the caller then treats the registration as conditional and keeps both branches, with a warning.
/// </summary>
internal static class ConditionEvaluator
{
    public static bool? Evaluate(Microsoft.CodeAnalysis.IOperation? condition, string environment)
    {
        switch (Unwrap(condition))
        {
            // env.IsProduction(), env.IsDevelopment(), env.IsStaging(), env.IsEnvironment("X"): case-insensitive.
            case IInvocationOperation call when KnownApis.HostEnvironmentCheck.Matches(call.TargetMethod):
                var name = call.TargetMethod.Name switch
                {
                    "IsProduction" => "Production",
                    "IsDevelopment" => "Development",
                    "IsStaging" => "Staging",
                    _ => Argument(call, "environmentName")?.ConstantValue is { HasValue: true, Value: string constant } ? constant : null,
                };
                return name is null ? null : string.Equals(name, environment, StringComparison.OrdinalIgnoreCase);

            // env.EnvironmentName == "Production" (string equality is ordinal).
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } comparison
                when EnvironmentNameComparedTo(comparison) is { } other:
                var equal = string.Equals(other, environment, StringComparison.Ordinal);
                return comparison.OperatorKind == BinaryOperatorKind.Equals ? equal : !equal;

            case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } negation:
                return Evaluate(negation.Operand, environment) is { } inner ? !inner : null;

            // Three-valued logic: false && unknown is false; true || unknown is true.
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } conjunction:
                var (l1, r1) = (Evaluate(conjunction.LeftOperand, environment), Evaluate(conjunction.RightOperand, environment));
                return l1 == false || r1 == false ? false : l1 == true && r1 == true ? true : null;

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } disjunction:
                var (l2, r2) = (Evaluate(disjunction.LeftOperand, environment), Evaluate(disjunction.RightOperand, environment));
                return l2 == true || r2 == true ? true : l2 == false && r2 == false ? false : null;

            case { ConstantValue: { HasValue: true, Value: bool literal } }:
                return literal;

            default:
                return null;
        }
    }

    /// <summary>For <c>x.EnvironmentName == "Name"</c> (either order), returns "Name".</summary>
    private static string? EnvironmentNameComparedTo(IBinaryOperation comparison)
    {
        static bool IsEnvironmentName(Microsoft.CodeAnalysis.IOperation? operation) =>
            Unwrap(operation) is IPropertyReferenceOperation { Property.Name: "EnvironmentName" };

        static string? Constant(Microsoft.CodeAnalysis.IOperation? operation) =>
            operation?.ConstantValue is { HasValue: true, Value: string text } ? text : null;

        return IsEnvironmentName(comparison.LeftOperand) ? Constant(comparison.RightOperand)
            : IsEnvironmentName(comparison.RightOperand) ? Constant(comparison.LeftOperand)
            : null;
    }
}
