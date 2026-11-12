using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetryScope.Extraction;

/// <summary>Small helpers over Roslyn's operation tree.</summary>
internal static class OperationHelpers
{
    /// <summary>Strips implicit and explicit conversions.</summary>
    public static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    public static IOperation? Argument(IInvocationOperation invocation, string parameterName) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName)?.Value;

    /// <summary>Argument by parameter position; ordinal 0 is the receiver of an extension method.</summary>
    public static IOperation? Argument(IInvocationOperation invocation, int ordinal) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal)?.Value;

    /// <summary>The lambda passed as a delegate argument, or null for method groups and variables.</summary>
    public static IAnonymousFunctionOperation? Lambda(IOperation? operation) =>
        Unwrap(operation) is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } ? lambda : null;

    /// <summary>The statements of a lambda body, without the implicit trailing return.</summary>
    public static IEnumerable<IOperation> Statements(IAnonymousFunctionOperation lambda) =>
        lambda.Body.Operations.Where(o => o is not IReturnOperation { ReturnedValue: null });

    /// <summary>The expression a statement evaluates (expression statements and expression-bodied returns).</summary>
    public static IOperation? ExpressionOf(IOperation statement) => statement switch
    {
        IExpressionStatementOperation expression => expression.Operation,
        IReturnOperation { ReturnedValue: { } returned } => returned,
        _ => null,
    };

    /// <summary>
    /// For <c>root.A(...).B(...)</c>, the calls in the order they run (A then B), provided the chain starts at
    /// <paramref name="root"/>; otherwise null.
    /// </summary>
    public static List<IInvocationOperation>? ChainFrom(IOperation? operation, IParameterSymbol root)
    {
        var chain = new List<IInvocationOperation>();
        var current = Unwrap(operation);
        while (current is IInvocationOperation call && call.TargetMethod.IsExtensionMethod)
        {
            chain.Add(call);
            current = Unwrap(Argument(call, ordinal: 0));
        }

        if (current is IParameterReferenceOperation parameter
            && SymbolEqualityComparer.Default.Equals(parameter.Parameter, root)
            && chain.Count > 0)
        {
            chain.Reverse();
            return chain;
        }

        return null;
    }

    /// <summary>For <c>o.A.B</c> rooted at <paramref name="root"/>, returns <c>A.B</c>; otherwise null.</summary>
    public static string? PathFrom(IOperation? target, IParameterSymbol root)
    {
        var names = new Stack<string>();
        var current = Unwrap(target);
        while (current is IPropertyReferenceOperation property)
        {
            names.Push(property.Property.Name);
            current = Unwrap(property.Instance);
        }

        return names.Count > 0
            && current is IParameterReferenceOperation parameter
            && SymbolEqualityComparer.Default.Equals(parameter.Parameter, root)
            ? string.Join('.', names)
            : null;
    }

    /// <summary>For <c>a.B(...)</c>, the <c>B</c> token, so chained calls are located at the method, not the chain start.</summary>
    public static SyntaxNode MethodNameSyntax(SyntaxNode invocation) =>
        invocation is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ? access.Name : invocation;

    /// <summary>One-line, length-limited source text for notes and diagnostics.</summary>
    public static string Shorten(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}
