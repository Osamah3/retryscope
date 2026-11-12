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

    public Value<string>? BaseAddress { get; set; }

    /// <summary>Host of a constant BaseAddress (with Aspire service discovery, the service name).</summary>
    public string? Target { get; set; }

    /// <summary>Per-client handlers; the first one registered is outermost.</summary>
    public List<Handler> Handlers { get; } = [];
}

/// <summary>
/// Walks the code reachable from a root method and records HttpClient registrations and resilience handlers.
/// Calls into methods that have source in the solution (helpers, Aspire ServiceDefaults) are followed; library
/// calls are matched by resolved symbol (<see cref="KnownApis"/>). Call <see cref="Complete"/> after the last root.
/// </summary>
/// <remarks>
/// Supported: <c>AddHttpClient</c> (named, typed; constant <c>BaseAddress</c>), <c>ConfigureHttpClientDefaults</c>
/// with a lambda, <c>AddStandardResilienceHandler</c> with no options, an options lambda or a configuration section,
/// and chained <c>.Configure(...)</c>. Anything else on that path produces a diagnostic rather than a silent guess.
/// </remarks>
internal sealed class RegistrationWalker(Solution solution, string rootDirectory, string packageVersion, AppSettingsReader? appSettings)
{
    private const int MaxDepth = 8;

    private readonly HashSet<(string File, int Start)> _visited = [];

    // Lambda parameters that stand for "every client" (the IHttpClientBuilder inside ConfigureHttpClientDefaults).
    private readonly HashSet<ISymbol> _defaultsBuilders = new(SymbolEqualityComparer.Default);

    // Standard handlers are collected first and built in Complete(), so that chained Configure(...) calls, which the
    // pre-order walk sees before the AddStandardResilienceHandler call they wrap, are applied in runtime order.
    private readonly Dictionary<SyntaxNode, PendingHandler> _pending = new();
    private readonly List<PendingHandler> _pendingOrder = [];
    private readonly HashSet<SyntaxNode> _appliedConfigureCalls = [];

    public Dictionary<string, ClientRecord> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>Handlers from <c>ConfigureHttpClientDefaults</c>; the first one registered is outermost.</summary>
    public List<Handler> DefaultHandlers { get; } = [];

    public List<RsDiagnostic> Diagnostics { get; } = [];

    public Task WalkAsync(IMethodSymbol root, CancellationToken cancellationToken) =>
        WalkMethodAsync(root, depth: 0, cancellationToken);

    /// <summary>Builds the collected handlers and attaches them to their clients (or to the defaults).</summary>
    public void Complete()
    {
        foreach (var pending in _pendingOrder)
        {
            switch (pending.Target)
            {
                case DefaultsTarget:
                    DefaultHandlers.Add(pending.Settings.Build(HandlerOrigin.Defaults, pending.Location));
                    break;
                case ClientTarget(var client):
                    client.Handlers.Add(pending.Settings.Build(HandlerOrigin.PerClient, pending.Location));
                    break;
            }
        }

        _pendingOrder.Clear();
        _pending.Clear();
    }

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
                Warn(DiagnosticCodes.WorkspaceLoad, $"Source of '{method.ToDisplayString()}' is not part of the loaded solution; not followed.");
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
            _ = GetOrCreatePending(invocation);
        }
        else if (KnownApis.ConfigureStandardHandler.Matches(method))
        {
            ApplyConfigureChain(invocation);
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
        if (Lambda(Argument(invocation, "configure")) is { Symbol.Parameters.Length: 1 } lambda)
        {
            _ = _defaultsBuilders.Add(lambda.Symbol.Parameters[0]);
            return;
        }

        Warn(DiagnosticCodes.UnresolvedValue,
            "ConfigureHttpClientDefaults is called with something other than a lambda; its handlers are not followed yet.",
            invocation.Syntax);
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
                Warn(DiagnosticCodes.UnresolvedValue, $"Client name '{nameArgument.Syntax}' is not a compile-time constant.", nameArgument.Syntax);
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

        ReadBaseAddress(invocation, client);
        return client;
    }

    /// <summary>Reads <c>c.BaseAddress = new Uri("...")</c> from the configureClient lambda.</summary>
    private void ReadBaseAddress(IInvocationOperation addHttpClient, ClientRecord client)
    {
        if (Lambda(Argument(addHttpClient, "configureClient")) is not { } lambda)
        {
            return;
        }

        foreach (var assignment in lambda.Body.Descendants().OfType<ISimpleAssignmentOperation>())
        {
            if (assignment.Target is not IPropertyReferenceOperation { Property.Name: "BaseAddress" })
            {
                continue;
            }

            var location = LocationOf(assignment.Syntax);
            if (Unwrap(assignment.Value) is IObjectCreationOperation create
                && create.Type?.ToDisplayString() == "System.Uri"
                && create.Arguments.FirstOrDefault()?.Value.ConstantValue is { HasValue: true, Value: string text }
                && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                client.BaseAddress = Values.Explicit(text, location);
                client.Target = uri.Host;
            }
            else
            {
                client.BaseAddress = Values.Unresolved(string.Empty, assignment.Value.Syntax.ToString(), location);
                client.Target = null;
            }
        }
    }

    /// <summary>The pending standard handler for an <c>AddStandardResilienceHandler</c> call, created on first sight.</summary>
    private PendingHandler GetOrCreatePending(IInvocationOperation add)
    {
        if (_pending.TryGetValue(add.Syntax, out var existing))
        {
            return existing;
        }

        var location = LocationOf(MethodNameSyntax(add.Syntax));
        var target = ResolveBuilder(Argument(add, ordinal: 0));
        if (target is null)
        {
            Warn(DiagnosticCodes.UnresolvedValue, "Could not determine which HttpClient this resilience handler belongs to.", add.Syntax);
        }

        var pending = new PendingHandler(new StandardHandlerSettings(packageVersion), target, location);
        _pending.Add(add.Syntax, pending);
        _pendingOrder.Add(pending);

        // AddStandardResilienceHandler(section) / AddStandardResilienceHandler(o => ...).
        ApplyOptions(add, pending.Settings);
        return pending;
    }

    /// <summary>
    /// Applies <c>.Configure(...)</c> calls chained on a standard handler, innermost first, which is the order they run in.
    /// Visiting the outermost call applies the whole chain; inner calls visited later are skipped.
    /// </summary>
    private void ApplyConfigureChain(IInvocationOperation outermost)
    {
        var chain = new List<IInvocationOperation>();
        IOperation? current = outermost;
        while (Unwrap(current) is IInvocationOperation call && KnownApis.ConfigureStandardHandler.Matches(call.TargetMethod))
        {
            chain.Add(call);
            current = Argument(call, ordinal: 0);
        }

        if (Unwrap(current) is not IInvocationOperation add || !KnownApis.AddStandardResilienceHandler.Matches(add.TargetMethod))
        {
            Warn(DiagnosticCodes.UnresolvedValue,
                "Configure(...) is called on a pipeline builder that is not traced to an AddStandardResilienceHandler call; not interpreted.",
                outermost.Syntax);
            return;
        }

        var pending = GetOrCreatePending(add);
        chain.Reverse();
        foreach (var call in chain.Where(c => _appliedConfigureCalls.Add(c.Syntax)))
        {
            ApplyOptions(call, pending.Settings);
        }
    }

    /// <summary>Applies a <c>section</c> or <c>configure</c> argument, if the call has one.</summary>
    private void ApplyOptions(IInvocationOperation call, StandardHandlerSettings settings)
    {
        if (Argument(call, "section") is { } section)
        {
            var location = LocationOf(section.Syntax);
            if (SectionPath(section) is not { } path)
            {
                MarkAllUnresolved(settings, $"configuration section not resolved: {section.Syntax}", location);
                Warn(DiagnosticCodes.UnresolvedValue, $"Configuration section '{section.Syntax}' could not be resolved to a constant path.", section.Syntax);
            }
            else if (appSettings is null)
            {
                MarkAllUnresolved(settings, $"configuration section {path}: no appsettings files available", location);
            }
            else
            {
                appSettings.Apply(path, settings, location);
            }
        }

        if (Argument(call, "configure") is { } configure)
        {
            if (Lambda(configure) is { Symbol.Parameters.Length: > 0 } lambda)
            {
                ApplyLambda(lambda, settings);
            }
            else
            {
                MarkAllUnresolved(settings, $"options delegate is not a lambda: {configure.Syntax}", LocationOf(configure.Syntax));
                Warn(DiagnosticCodes.OpaqueDelegate, $"Options delegate '{configure.Syntax}' is not a lambda; its settings are unresolved.", configure.Syntax);
            }
        }
    }

    /// <summary>Interprets <c>o.Path = value;</c> statements. Anything else is reported, never guessed.</summary>
    private void ApplyLambda(IAnonymousFunctionOperation lambda, StandardHandlerSettings settings)
    {
        var options = lambda.Symbol.Parameters[0];

        foreach (var statement in lambda.Body.Operations)
        {
            if (statement is IReturnOperation { ReturnedValue: null })
            {
                continue;
            }

            if (statement is IExpressionStatementOperation { Operation: ISimpleAssignmentOperation assignment }
                && PathFrom(assignment.Target, options) is { } path)
            {
                if (StandardHandlerSettings.TryGetKind(path, out var kind))
                {
                    if (Evaluate(assignment.Value, kind, settings.Current(path), LocationOf(assignment.Syntax)) is { } value)
                    {
                        settings.Set(path, value);
                    }
                }
                else
                {
                    Diagnostics.Add(new RsDiagnostic(DiagnosticCodes.UnresolvedValue, Severity.Info,
                        $"Option '{path}' is not modelled; ignored.", LocationOf(assignment.Syntax)));
                }

                continue;
            }

            // A call, a condition, a loop...: report it, and mark any modelled option assigned inside it as unresolved.
            Warn(DiagnosticCodes.UnresolvedValue, $"Statement not interpreted: {statement.Syntax}", statement.Syntax);
            foreach (var nested in statement.Descendants().OfType<ISimpleAssignmentOperation>())
            {
                if (PathFrom(nested.Target, options) is { } nestedPath
                    && StandardHandlerSettings.TryGetKind(nestedPath, out _)
                    && settings.Current(nestedPath) is { } fallback)
                {
                    settings.Set(nestedPath, Values.Unresolved<object>(fallback, $"assigned inside an uninterpreted statement: {nested.Syntax}", LocationOf(nested.Syntax)));
                }
            }
        }
    }

    /// <summary>A literal value becomes Explicit; a predicate becomes Opaque; anything else Unresolved (keeping the fallback).</summary>
    private Value<object>? Evaluate(IOperation value, SettingKind kind, object? fallback, SourceLocation location)
    {
        if (kind == SettingKind.Predicate)
        {
            return Values.Opaque<object>(PredicateKind.Custom, $"custom predicate: {Shorten(value.Syntax)}", location);
        }

        object? result = kind switch
        {
            SettingKind.Int when ConstantEvaluator.TryInt(value, out var i) => i,
            SettingKind.Double when ConstantEvaluator.TryDouble(value, out var d) => d,
            SettingKind.Bool when ConstantEvaluator.TryBool(value, out var b) => b,
            SettingKind.TimeSpan when ConstantEvaluator.TryTimeSpan(value, out var t) => t,
            SettingKind.Backoff when ConstantEvaluator.TryBackoff(value, out var e) => e,
            _ => null,
        };

        if (result is not null)
        {
            return Values.Explicit<object>(result, location);
        }

        if (fallback is null)
        {
            Warn(DiagnosticCodes.UnresolvedValue, $"Value '{value.Syntax}' could not be evaluated and has no default; ignored.", value.Syntax);
            return null;
        }

        return Values.Unresolved<object>(fallback, value.Syntax.ToString(), location);
    }

    /// <summary>For <c>o.Retry.MaxRetryAttempts</c> rooted at the options parameter, returns <c>Retry.MaxRetryAttempts</c>.</summary>
    private static string? PathFrom(IOperation target, IParameterSymbol options)
    {
        var names = new Stack<string>();
        var current = target;
        while (current is IPropertyReferenceOperation property)
        {
            names.Push(property.Property.Name);
            current = property.Instance;
        }

        return names.Count > 0
            && current is IParameterReferenceOperation parameter
            && SymbolEqualityComparer.Default.Equals(parameter.Parameter, options)
            ? string.Join('.', names)
            : null;
    }

    /// <summary>
    /// Resolves <c>config.GetSection("A").GetSection("B")</c> to <c>A:B</c>. The chain must start at the root
    /// configuration (a property or parameter typed as root configuration); anything else returns null.
    /// </summary>
    private static string? SectionPath(IOperation? operation)
    {
        switch (Unwrap(operation))
        {
            case IInvocationOperation { TargetMethod.Name: "GetSection" } call
                when call.TargetMethod.ContainingType?.ToDisplayString() == KnownApis.ConfigurationInterface:
                if (Argument(call, "key")?.ConstantValue is not { HasValue: true, Value: string key })
                {
                    return null;
                }

                var prefix = SectionPath(call.Instance);
                return prefix is null ? null : prefix.Length == 0 ? key : $"{prefix}:{key}";

            case { } root when root is IPropertyReferenceOperation or IParameterReferenceOperation
                && root.Type?.ToDisplayString() is { } type && KnownApis.RootConfigurationTypes.Contains(type):
                return string.Empty;

            default:
                return null;
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
                return ResolveBuilder(Argument(call, ordinal: 0));

            default:
                return null;
        }
    }

    private static void MarkAllUnresolved(StandardHandlerSettings settings, string reason, SourceLocation location)
    {
        foreach (var (path, _) in StandardHandlerSettings.Paths)
        {
            if (settings.Current(path) is { } fallback)
            {
                settings.Set(path, Values.Unresolved<object>(fallback, reason, location));
            }
        }
    }

    private static bool IsHttpClientBuilder(ITypeSymbol type) =>
        type.ToDisplayString() == KnownApis.HttpClientBuilderInterface;

    private static IOperation? Argument(IInvocationOperation invocation, string parameterName) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName)?.Value;

    private static IOperation? Argument(IInvocationOperation invocation, int ordinal) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal)?.Value;

    private static IAnonymousFunctionOperation? Lambda(IOperation? operation) =>
        Unwrap(operation) is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } ? lambda : null;

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static string Shorten(SyntaxNode node)
    {
        var text = string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>For <c>a.B(...)</c>, the <c>B</c> token, so chained calls are located at the method, not the chain start.</summary>
    private static SyntaxNode MethodNameSyntax(SyntaxNode invocation) =>
        invocation is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ? access.Name : invocation;

    private void Warn(string code, string message, SyntaxNode? node = null) =>
        Diagnostics.Add(new RsDiagnostic(code, Severity.Warning, message, node is null ? null : LocationOf(node)));

    private SourceLocation LocationOf(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var file = Path.GetRelativePath(rootDirectory, span.Path).Replace('\\', '/');
        return new SourceLocation(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private sealed record PendingHandler(StandardHandlerSettings Settings, BuilderTarget? Target, SourceLocation Location);

    private abstract record BuilderTarget;

    private sealed record DefaultsTarget : BuilderTarget
    {
        public static readonly DefaultsTarget Instance = new();
    }

    private sealed record ClientTarget(ClientRecord Client) : BuilderTarget;
}
