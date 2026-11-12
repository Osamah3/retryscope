using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetryScope.Model;
using RsDiagnostic = RetryScope.Model.Diagnostic;

namespace RetryScope.Extraction;

/// <summary>A client registration found by the walker, with its per-client handlers in registration order.</summary>
internal sealed class ClientRecord(string name, ClientKind kind, SourceLocation location)
{
    public string Name { get; } = name;

    public ClientKind Kind { get; } = kind;

    public SourceLocation Location { get; } = location;

    /// <summary>Per-client handlers; the first one registered is outermost.</summary>
    public List<Handler> Handlers { get; } = [];
}

/// <summary>
/// Walks the code reachable from a root method and records HttpClient registrations and resilience handlers.
/// Calls into methods that have source in the solution (helpers, Aspire ServiceDefaults) are followed; library
/// calls are matched by resolved symbol (<see cref="KnownApis"/>).
/// </summary>
/// <remarks>
/// Slice 1 scope: <c>AddHttpClient</c> (named and typed), <c>ConfigureHttpClientDefaults</c> with a lambda, and
/// <c>AddStandardResilienceHandler()</c> with no options. Anything else on that path produces a diagnostic rather than
/// a silent guess.
/// </remarks>
internal sealed class RegistrationWalker(Solution solution, string rootDirectory, string packageVersion)
{
    private const int MaxDepth = 8;

    private readonly HashSet<(string File, int Start)> _visited = [];

    // Lambda parameters that stand for "every client" (the IHttpClientBuilder inside ConfigureHttpClientDefaults).
    private readonly HashSet<ISymbol> _defaultsBuilders = new(SymbolEqualityComparer.Default);

    public Dictionary<string, ClientRecord> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>Handlers from <c>ConfigureHttpClientDefaults</c>; the first one registered is outermost.</summary>
    public List<Handler> DefaultHandlers { get; } = [];

    public List<RsDiagnostic> Diagnostics { get; } = [];

    public Task WalkAsync(IMethodSymbol root, CancellationToken cancellationToken) =>
        WalkMethodAsync(root, depth: 0, cancellationToken);

    private async Task WalkMethodAsync(IMethodSymbol method, int depth, CancellationToken cancellationToken)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (!_visited.Add((reference.SyntaxTree.FilePath, reference.Span.Start)))
            {
                continue;
            }

            var document = solution.GetDocument(reference.SyntaxTree);
            if (document is null || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                Diagnostics.Add(new RsDiagnostic(
                    DiagnosticCodes.WorkspaceLoad,
                    Severity.Warning,
                    $"Source of '{method.ToDisplayString()}' is not part of the loaded solution; not followed."));
                continue;
            }

            var body = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

            // Pre-order: an outer call (e.g. ConfigureHttpClientDefaults) is seen before the calls inside its lambda.
            foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                await VisitInvocationAsync(invocation, model, depth, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task VisitInvocationAsync(
        InvocationExpressionSyntax syntax, SemanticModel model, int depth, CancellationToken cancellationToken)
    {
        if (model.GetOperation(syntax, cancellationToken) is not IInvocationOperation invocation)
        {
            return;
        }

        var method = invocation.TargetMethod;

        if (KnownApis.ConfigureHttpClientDefaults.Matches(method))
        {
            RegisterDefaultsLambda(invocation);
        }
        else if (KnownApis.AddHttpClient.Matches(method))
        {
            _ = GetOrAddClient(invocation);
        }
        else if (KnownApis.AddStandardResilienceHandler.Matches(method))
        {
            AttachStandardHandler(invocation);
        }
        else if (depth < MaxDepth)
        {
            // Our own code (a helper extension, Aspire ServiceDefaults): follow it.
            var declared = (method.ReducedFrom ?? method).OriginalDefinition;
            if (declared.DeclaringSyntaxReferences.Length > 0)
            {
                await WalkMethodAsync(declared, depth + 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void RegisterDefaultsLambda(IInvocationOperation invocation)
    {
        var configure = Argument(invocation, "configure");
        if (Unwrap(configure) is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }
            && lambda.Symbol.Parameters.Length == 1)
        {
            _ = _defaultsBuilders.Add(lambda.Symbol.Parameters[0]);
            return;
        }

        Diagnostics.Add(new RsDiagnostic(
            DiagnosticCodes.UnresolvedValue,
            Severity.Warning,
            "ConfigureHttpClientDefaults is called with something other than a lambda; its handlers are not followed yet.",
            LocationOf(invocation.Syntax)));
    }

    private ClientRecord? GetOrAddClient(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        string name;
        ClientKind kind;

        if (method.TypeArguments.Length > 0)
        {
            // Typed client: the factory names it after the short type name (TypeNameHelper, fullName: false).
            name = method.TypeArguments[0].Name;
            kind = ClientKind.Typed;
        }
        else if (Argument(invocation, "name") is { } nameArgument)
        {
            kind = ClientKind.Named;
            if (nameArgument.ConstantValue is { HasValue: true, Value: string constant })
            {
                name = constant;
            }
            else
            {
                var location = LocationOf(nameArgument.Syntax);
                name = $"<unresolved:{location.File}:{location.Line}>";
                Diagnostics.Add(new RsDiagnostic(
                    DiagnosticCodes.UnresolvedValue,
                    Severity.Warning,
                    $"Client name '{nameArgument.Syntax}' is not a compile-time constant.",
                    location));
            }
        }
        else
        {
            // AddHttpClient(services) with no name only registers the factory; it returns no client builder.
            return null;
        }

        if (!Clients.TryGetValue(name, out var client))
        {
            client = new ClientRecord(name, kind, LocationOf(MethodNameSyntax(invocation.Syntax)));
            Clients.Add(name, client);
        }

        return client;
    }

    private void AttachStandardHandler(IInvocationOperation invocation)
    {
        var location = LocationOf(MethodNameSyntax(invocation.Syntax));

        if (invocation.Arguments.Length > 1)
        {
            // Overloads taking a configure lambda or an IConfigurationSection: options are interpreted in slice 2.
            Diagnostics.Add(new RsDiagnostic(
                DiagnosticCodes.UnresolvedValue,
                Severity.Warning,
                "AddStandardResilienceHandler options are not interpreted yet; package defaults were used.",
                location));
        }

        var handlerBuilder = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value;
        switch (ResolveBuilder(handlerBuilder))
        {
            case DefaultsTarget:
                DefaultHandlers.Add(StandardHandlerDefaults.Create(HandlerOrigin.Defaults, packageVersion, location));
                break;
            case ClientTarget(var client):
                client.Handlers.Add(StandardHandlerDefaults.Create(HandlerOrigin.PerClient, packageVersion, location));
                break;
            default:
                Diagnostics.Add(new RsDiagnostic(
                    DiagnosticCodes.UnresolvedValue,
                    Severity.Warning,
                    "Could not determine which HttpClient this resilience handler belongs to.",
                    location));
                break;
        }
    }

    /// <summary>Follows an <c>IHttpClientBuilder</c> expression back to the client (or defaults) it configures.</summary>
    private BuilderTarget? ResolveBuilder(IOperation? operation)
    {
        switch (Unwrap(operation))
        {
            case IParameterReferenceOperation parameter when _defaultsBuilders.Contains(parameter.Parameter):
                return DefaultsTarget.Instance;

            case IInvocationOperation call when KnownApis.AddHttpClient.Matches(call.TargetMethod):
                return GetOrAddClient(call) is { } client ? new ClientTarget(client) : null;

            // A builder extension that returns its receiver (e.g. ConfigureHttpClient, AddHttpMessageHandler).
            case IInvocationOperation call when call.TargetMethod.IsExtensionMethod
                && IsHttpClientBuilder(call.TargetMethod.ReturnType)
                && call.TargetMethod.Parameters.Length > 0
                && IsHttpClientBuilder(call.TargetMethod.Parameters[0].Type):
                return ResolveBuilder(call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value);

            default:
                return null;
        }
    }

    private static bool IsHttpClientBuilder(ITypeSymbol type) =>
        type.ToDisplayString() == KnownApis.HttpClientBuilderInterface;

    private static IOperation? Argument(IInvocationOperation invocation, string parameterName) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName)?.Value;

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>For <c>a.B(...)</c>, the <c>B</c> token, so chained calls are located at the method, not the chain start.</summary>
    private static SyntaxNode MethodNameSyntax(SyntaxNode invocation) =>
        invocation is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ? access.Name : invocation;

    private SourceLocation LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var file = Path.GetRelativePath(rootDirectory, span.Path).Replace('\\', '/');
        return new SourceLocation(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private abstract record BuilderTarget;

    private sealed record DefaultsTarget : BuilderTarget
    {
        public static readonly DefaultsTarget Instance = new();
    }

    private sealed record ClientTarget(ClientRecord Client) : BuilderTarget;
}
