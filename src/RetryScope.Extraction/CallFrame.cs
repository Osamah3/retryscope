using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using static RetryScope.Extraction.OperationHelpers;

namespace RetryScope.Extraction;

/// <summary>
/// The arguments a followed method was called with (dev-set finding D4). Inside a helper such as
/// <c>AddServiceClient&lt;TClient&gt;(string baseAddress)</c>, a reference to <c>baseAddress</c> or <c>TClient</c> resolves
/// to what the caller passed, in the caller's own frame, so helpers that forward their parameters are followed through.
/// </summary>
internal sealed class CallFrame
{
    private const int MaxResolutionSteps = 16;

    private readonly IReadOnlyDictionary<int, (IOperation Argument, CallFrame Frame)> _arguments;
    private readonly IReadOnlyDictionary<int, ITypeSymbol> _typeArguments;

    private CallFrame(string? methodKey, IReadOnlyDictionary<int, (IOperation, CallFrame)> arguments, IReadOnlyDictionary<int, ITypeSymbol> typeArguments)
    {
        MethodKey = methodKey;
        _arguments = arguments;
        _typeArguments = typeArguments;
    }

    /// <summary>The frame of a root method: nothing is known about its parameters.</summary>
    public static CallFrame Root { get; } = new(null, new Dictionary<int, (IOperation, CallFrame)>(), new Dictionary<int, ITypeSymbol>());

    /// <summary>Identifies the method this frame belongs to by its declaration (stable across compilations).</summary>
    public string? MethodKey { get; }

    public static string KeyOf(IMethodSymbol method) =>
        method.OriginalDefinition.DeclaringSyntaxReferences is [var reference, ..]
            ? $"{reference.SyntaxTree.FilePath}:{reference.Span.Start}"
            : method.OriginalDefinition.ToDisplayString();

    /// <summary>The frame for a call made from this frame to <paramref name="declared"/>.</summary>
    public CallFrame Enter(IMethodSymbol declared, IInvocationOperation call)
    {
        var arguments = new Dictionary<int, (IOperation, CallFrame)>();
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter is { } parameter && Unwrap(argument.Value) is { } value)
            {
                arguments[parameter.Ordinal] = (value, this);
            }
        }

        var typeArguments = new Dictionary<int, ITypeSymbol>();
        for (var i = 0; i < call.TargetMethod.TypeArguments.Length; i++)
        {
            typeArguments[i] = Substitute(call.TargetMethod.TypeArguments[i]);
        }

        return new CallFrame(KeyOf(declared), arguments, typeArguments);
    }

    /// <summary>A method type parameter of this frame's method becomes the caller's type argument.</summary>
    public ITypeSymbol Substitute(ITypeSymbol type) =>
        type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method, DeclaringMethod: { } method } parameter
        && KeyOf(method) == MethodKey
        && _typeArguments.TryGetValue(parameter.Ordinal, out var actual)
            ? actual
            : type;

    /// <summary>
    /// Follows parameter references to the caller's arguments and locals to their single initializer, returning the
    /// operation reached and the frame it belongs to.
    /// </summary>
    public (IOperation? Operation, CallFrame Frame) Resolve(IOperation? operation)
    {
        var frame = this;
        operation = Unwrap(operation);

        for (var step = 0; step < MaxResolutionSteps; step++)
        {
            switch (operation)
            {
                case IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol method } parameter }
                    when KeyOf(method) == frame.MethodKey && frame._arguments.TryGetValue(parameter.Ordinal, out var argument):
                    (operation, frame) = (Unwrap(argument.Argument), argument.Frame);
                    continue;

                case ILocalReferenceOperation local when SingleAssignmentInitializer(local) is { } initializer:
                    operation = Unwrap(initializer);
                    continue;
            }

            break;
        }

        return (operation, frame);
    }

    /// <summary>
    /// The initializer of a local that is never assigned again in its method (dev-set finding D5). Locals that are
    /// reassigned, or passed by <c>ref</c>/<c>out</c>, are not resolved.
    /// </summary>
    private static IOperation? SingleAssignmentInitializer(ILocalReferenceOperation local)
    {
        if (local.SemanticModel is not { } model
            || local.Local.DeclaringSyntaxReferences is not [var reference]
            || reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } value } declarator)
        {
            return null;
        }

        SyntaxNode scope = (SyntaxNode?)declarator.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
            ?? (SyntaxNode?)declarator.FirstAncestorOrSelf<LocalFunctionStatementSyntax>()
            ?? declarator.SyntaxTree.GetRoot();

        bool Refers(ExpressionSyntax expression) =>
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, local.Local);

        var reassigned = scope.DescendantNodes().Any(node => node switch
        {
            AssignmentExpressionSyntax assignment => Refers(assignment.Left),
            ArgumentSyntax { RefKindKeyword.RawKind: not 0 } argument => Refers(argument.Expression),
            _ => false,
        });

        return reassigned ? null : model.GetOperation(value);
    }
}
