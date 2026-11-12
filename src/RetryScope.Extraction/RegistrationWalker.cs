using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetryScope.Model;
using static RetryScope.Extraction.OperationHelpers;

namespace RetryScope.Extraction;

/// <summary>A client found by the extractor, with its per-client handlers in registration order.</summary>
internal sealed class ClientRecord(string name, ClientKind kind, SourceLocation location)
{
    public string Name { get; } = name;

    public ClientKind Kind { get; } = kind;

    public SourceLocation Location { get; } = location;

    public Value<string>? BaseAddress { get; set; }

    /// <summary>Host of a constant BaseAddress (with Aspire service discovery, the service name).</summary>
    public string? Target { get; set; }

    /// <summary>Whether any part of the registration depends on a condition (labelling guide hard case 1).</summary>
    public bool Conditional { get; set; }

    /// <summary>Sequence number of the last <c>RemoveAllResilienceHandlers</c> on this client, if any.</summary>
    public int? RemovedAt { get; set; }

    /// <summary>Per-client handlers with their registration sequence numbers, in order.</summary>
    public List<(int Sequence, Handler Handler)> Registered { get; } = [];

    /// <summary>
    /// The effective handlers, outermost first: defaults wrap per-client handlers (docs/findings.md F1).
    /// <c>RemoveAllResilienceHandlers</c> removes every Polly v8 handler added before it, defaults included; Polly v7
    /// handlers are not <c>ResilienceHandler</c>s and survive (ResilienceHttpClientBuilderExtensions.Resilience.cs).
    /// </summary>
    public IReadOnlyList<Handler> EffectiveHandlers(IReadOnlyList<Handler> defaults)
    {
        bool Survives(int sequence, Handler handler) =>
            RemovedAt is not { } removed || sequence > removed || handler.Kind == HandlerKind.PollyV7;

        return [.. defaults.Where(h => Survives(-1, h)), .. Registered.Where(r => Survives(r.Sequence, r.Handler)).Select(r => r.Handler)];
    }
}

/// <summary>
/// Walks the code reachable from a root method and records HttpClient registrations and resilience handlers.
/// Calls into methods that have source in the solution (helpers, Aspire ServiceDefaults) are followed; library
/// calls are matched by resolved symbol (<see cref="KnownApis"/>). Call <see cref="Complete"/> after the last root.
/// </summary>
/// <remarks>
/// Branches guarded by host-environment checks are decided for the configured environment; registrations under any
/// other condition are kept (both branches) and marked conditional, with a warning. Anything not understood produces a
/// diagnostic rather than a silent guess.
/// </remarks>
internal sealed class RegistrationWalker(Solution solution, ExtractionContext context, AppSettingsReader? appSettings)
{
    private const int MaxDepth = 8;

    private readonly HandlerReaders _readers = new(context);
    private readonly HashSet<(string File, int Start)> _visited = [];

    // Lambda parameters that stand for "every client" (the IHttpClientBuilder inside ConfigureHttpClientDefaults).
    private readonly HashSet<ISymbol> _defaultsBuilders = new(SymbolEqualityComparer.Default);

    // Handlers are collected first and built in Complete(), so that chained Configure(...) calls, which the pre-order
    // walk sees before the AddStandardResilienceHandler call they wrap, are applied in runtime order. Sequence numbers
    // order handlers and removals as they run.
    private readonly Dictionary<SyntaxNode, PendingHandler> _pending = new();
    private readonly HashSet<SyntaxNode> _appliedConfigureCalls = [];
    private readonly HashSet<SyntaxNode> _recordedRemovals = [];
    private readonly HashSet<SyntaxNode> _warnedConditions = [];
    private int _sequence;

    public Dictionary<string, ClientRecord> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>Handlers from <c>ConfigureHttpClientDefaults</c>; the first one registered is outermost.</summary>
    public List<Handler> DefaultHandlers { get; } = [];

    /// <summary>Whether a defaults handler was registered under a condition (then every client is conditional).</summary>
    public bool DefaultsConditional { get; private set; }

    public Task WalkAsync(IMethodSymbol root, CancellationToken cancellationToken) =>
        WalkMethodAsync(root, depth: 0, conditional: false, cancellationToken);

    /// <summary>Builds the collected handlers and attaches them to their clients (or to the defaults).</summary>
    public void Complete()
    {
        foreach (var pending in _pending.Values.OrderBy(p => p.Sequence))
        {
            var origin = pending.Target is DefaultsTarget ? HandlerOrigin.Defaults : HandlerOrigin.PerClient;
            var handler = (pending.Built ?? pending.Standard!.Build(origin, pending.Location)) with { Origin = origin };

            switch (pending.Target)
            {
                case DefaultsTarget:
                    DefaultHandlers.Add(handler);
                    DefaultsConditional |= pending.Conditional;
                    break;
                case ClientTarget(var client):
                    client.Registered.Add((pending.Sequence, handler));
                    client.Conditional |= pending.Conditional;
                    break;
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// Records every <c>new HttpClient(...)</c> in the project. Such clients bypass IHttpClientFactory, so no factory
    /// handler applies to them; they are reported as direct clients with no resilience (diagnostic RS003).
    /// </summary>
    public async Task ScanDirectClientsAsync(Project project, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
                || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            {
                continue;
            }

            foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetOperation(creation, cancellationToken) is not IObjectCreationOperation { Type: { } type } operation
                    || type.ToDisplayString() != KnownApis.HttpClientType)
                {
                    continue;
                }

                var name = EnclosingMemberName(model, creation.SpanStart);
                if (!Clients.TryGetValue($"direct:{name}", out var client))
                {
                    client = new ClientRecord(name, ClientKind.Direct, context.LocationOf(creation));
                    Clients.Add($"direct:{name}", client);
                    context.Info(DiagnosticCodes.DirectHttpClient,
                        $"'new HttpClient()' in {name} bypasses IHttpClientFactory; no factory resilience handlers apply.", creation);
                }

                foreach (var initializer in operation.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
                {
                    if (initializer is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "BaseAddress" } } assignment)
                    {
                        ReadUri(assignment, client);
                    }
                }
            }
        }
    }

    private async Task WalkMethodAsync(IMethodSymbol method, int depth, bool conditional, CancellationToken cancellationToken)
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
                context.Warn(DiagnosticCodes.WorkspaceLoad, $"Source of '{method.ToDisplayString()}' is not part of the loaded solution; not followed.");
                continue;
            }

            var body = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

            // Pre-order: an outer call (e.g. ConfigureHttpClientDefaults) is seen before the calls inside its lambda.
            foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                await VisitInvocationAsync(invocation, model, body, depth, conditional, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task VisitInvocationAsync(
        InvocationExpressionSyntax syntax, SemanticModel model, SyntaxNode body, int depth, bool inheritedConditional, CancellationToken cancellationToken)
    {
        if (model.GetOperation(syntax, cancellationToken) is not IInvocationOperation invocation)
        {
            return;
        }

        var (skip, branchConditional) = BranchState(syntax, model, body);
        var conditional = inheritedConditional || branchConditional;
        var method = invocation.TargetMethod;

        if (skip)
        {
            // The branch does not run in this environment, but the client's behaviour still depends on it.
            if (IsHandlerRegistration(method))
            {
                switch (ResolveBuilder(Argument(invocation, ordinal: 0), register: false))
                {
                    case ClientTarget(var client):
                        client.Conditional = true;
                        break;
                    case DefaultsTarget:
                        DefaultsConditional = true;
                        break;
                }
            }

            return;
        }

        if (KnownApis.ConfigureHttpClientDefaults.Matches(method))
        {
            RegisterDefaultsLambda(invocation);
        }
        else if (KnownApis.AddHttpClient.Matches(method))
        {
            if (GetOrAddClient(invocation, register: true) is { } client)
            {
                client.Conditional |= conditional;
            }
        }
        else if (KnownApis.AddStandardResilienceHandler.Matches(method))
        {
            _ = GetOrCreateStandard(invocation, conditional);
        }
        else if (KnownApis.ConfigureStandardHandler.Matches(method))
        {
            ApplyConfigureChain(invocation, conditional);
        }
        else if (KnownApis.AddResilienceHandler.Matches(method))
        {
            CreateHandler(invocation, conditional, _readers.ReadCustomPipeline);
        }
        else if (KnownApis.AddStandardHedgingHandler.Matches(method))
        {
            CreateHandler(invocation, conditional, _readers.ReadStandardHedging);
        }
        else if (KnownApis.AddPollyV7Policy.Matches(method))
        {
            CreateHandler(invocation, conditional, _readers.ReadPollyV7);
        }
        else if (KnownApis.RemoveAllResilienceHandlers.Matches(method))
        {
            RecordRemoval(invocation, conditional);
        }
        else if (KnownApis.AddResiliencePipeline.Matches(method))
        {
            context.Warn(DiagnosticCodes.UnlinkedPipeline,
                "Keyed resilience pipeline registered in DI; code that executes it around HttpClient calls is not linked to a client in V1.",
                invocation.Syntax);
        }
        else if (depth < MaxDepth)
        {
            // Our own code (a helper extension, Aspire ServiceDefaults): follow it.
            var declared = (method.ReducedFrom ?? method).OriginalDefinition;
            if (declared.DeclaringSyntaxReferences.Length > 0)
            {
                await WalkMethodAsync(declared, depth + 1, conditional, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsHandlerRegistration(IMethodSymbol method) =>
        KnownApis.AddStandardResilienceHandler.Matches(method)
        || KnownApis.ConfigureStandardHandler.Matches(method)
        || KnownApis.AddResilienceHandler.Matches(method)
        || KnownApis.AddStandardHedgingHandler.Matches(method)
        || KnownApis.AddPollyV7Policy.Matches(method)
        || KnownApis.RemoveAllResilienceHandlers.Matches(method);

    /// <summary>
    /// Whether the call sits in an <c>if</c> branch or <c>?:</c> arm (within the walked method). Returns skip = true when
    /// a host-environment condition rules the branch out; an undecidable condition keeps the call, with a warning.
    /// </summary>
    private (bool Skip, bool Conditional) BranchState(SyntaxNode syntax, SemanticModel model, SyntaxNode root)
    {
        var conditional = false;
        SyntaxNode child = syntax;

        for (var node = syntax.Parent; node is not null && child != root; child = node, node = node.Parent)
        {
            var (condition, inTrueBranch) = node switch
            {
                IfStatementSyntax ifStatement when child == ifStatement.Statement => (ifStatement.Condition, true),
                IfStatementSyntax ifStatement when child == ifStatement.Else => (ifStatement.Condition, false),
                ConditionalExpressionSyntax ternary when child == ternary.WhenTrue => (ternary.Condition, true),
                ConditionalExpressionSyntax ternary when child == ternary.WhenFalse => (ternary.Condition, false),
                _ => ((ExpressionSyntax?)null, false),
            };

            if (condition is null)
            {
                continue;
            }

            conditional = true;
            switch (ConditionEvaluator.Evaluate(model.GetOperation(condition), context.Environment))
            {
                case null when _warnedConditions.Add(condition):
                    context.Warn(DiagnosticCodes.UnresolvedValue,
                        $"Condition '{Shorten(condition)}' cannot be decided statically; registrations in both branches are kept and marked conditional.",
                        condition);
                    break;
                case { } taken when taken != inTrueBranch:
                    return (true, true);
            }
        }

        return (false, conditional);
    }

    private void RegisterDefaultsLambda(IInvocationOperation invocation)
    {
        if (Lambda(Argument(invocation, "configure")) is { Symbol.Parameters.Length: 1 } lambda)
        {
            _ = _defaultsBuilders.Add(lambda.Symbol.Parameters[0]);
            return;
        }

        context.Warn(DiagnosticCodes.UnresolvedValue,
            "ConfigureHttpClientDefaults is called with something other than a lambda; its handlers are not followed yet.",
            invocation.Syntax);
    }

    /// <summary>The client an <c>AddHttpClient</c> call configures; created only when <paramref name="register"/>.</summary>
    private ClientRecord? GetOrAddClient(IInvocationOperation invocation, bool register)
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
                var location = context.LocationOf(nameArgument.Syntax);
                name = $"<unresolved:{location.File}:{location.Line}>";
                if (register)
                {
                    context.Warn(DiagnosticCodes.UnresolvedValue, $"Client name '{Shorten(nameArgument.Syntax)}' is not a compile-time constant.", nameArgument.Syntax);
                }
            }
        }
        else
        {
            // AddHttpClient(services) with no name only registers the factory; it returns no client builder.
            return null;
        }

        if (!Clients.TryGetValue(name, out var client))
        {
            if (!register)
            {
                return null;
            }

            client = new ClientRecord(name, kind, context.LocationOf(MethodNameSyntax(invocation.Syntax)));
            Clients.Add(name, client);
        }

        if (register && Lambda(Argument(invocation, "configureClient")) is { } configureClient)
        {
            foreach (var assignment in configureClient.Body.Descendants().OfType<ISimpleAssignmentOperation>())
            {
                if (assignment.Target is IPropertyReferenceOperation { Property.Name: "BaseAddress" })
                {
                    ReadUri(assignment, client);
                }
            }
        }

        return client;
    }

    /// <summary>Reads <c>BaseAddress = new Uri("...")</c>; anything non-constant is recorded as unresolved.</summary>
    private void ReadUri(ISimpleAssignmentOperation assignment, ClientRecord client)
    {
        var location = context.LocationOf(assignment.Syntax);
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
            client.BaseAddress = Values.Unresolved(string.Empty, Shorten(assignment.Value.Syntax), location);
            client.Target = null;
        }
    }

    /// <summary>The pending standard handler for an <c>AddStandardResilienceHandler</c> call, created on first sight.</summary>
    private PendingHandler GetOrCreateStandard(IInvocationOperation add, bool conditional)
    {
        if (_pending.TryGetValue(add.Syntax, out var existing))
        {
            existing.Conditional |= conditional;
            return existing;
        }

        var pending = NewPending(add, conditional);
        pending.Standard = new StandardHandlerSettings(context.ResilienceVersion);

        // AddStandardResilienceHandler(section) / AddStandardResilienceHandler(o => ...).
        ApplyOptions(add, pending.Standard);
        return pending;
    }

    /// <summary>Records a non-standard handler (custom pipeline, hedging, Polly v7) built by <paramref name="read"/>.</summary>
    private void CreateHandler(IInvocationOperation call, bool conditional, Func<IInvocationOperation, SourceLocation, Handler> read)
    {
        if (_pending.ContainsKey(call.Syntax))
        {
            return;
        }

        var pending = NewPending(call, conditional);
        pending.Built = read(call, pending.Location);
    }

    /// <summary>Resolves the client first (which may record a chained removal), then takes the next sequence number.</summary>
    private PendingHandler NewPending(IInvocationOperation call, bool conditional)
    {
        var location = context.LocationOf(MethodNameSyntax(call.Syntax));
        var target = ResolveBuilder(Argument(call, ordinal: 0), register: true);
        if (target is null)
        {
            context.Warn(DiagnosticCodes.UnresolvedValue, "Could not determine which HttpClient this resilience handler belongs to.", call.Syntax);
        }

        var pending = new PendingHandler(++_sequence, target, location) { Conditional = conditional };
        _pending.Add(call.Syntax, pending);
        return pending;
    }

    private void RecordRemoval(IInvocationOperation removal, bool conditional)
    {
        if (!_recordedRemovals.Add(removal.Syntax))
        {
            return;
        }

        switch (ResolveBuilder(Argument(removal, ordinal: 0), register: true))
        {
            case ClientTarget(var client):
                client.RemovedAt = ++_sequence;
                client.Conditional |= conditional;
                break;
            default:
                context.Warn(DiagnosticCodes.UnresolvedValue, "RemoveAllResilienceHandlers on a builder that is not traced to one client; ignored.", removal.Syntax);
                break;
        }
    }

    /// <summary>
    /// Applies <c>.Configure(...)</c> calls chained on a standard handler, innermost first, which is the order they run in.
    /// Visiting the outermost call applies the whole chain; inner calls visited later are skipped.
    /// </summary>
    private void ApplyConfigureChain(IInvocationOperation outermost, bool conditional)
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
            context.Warn(DiagnosticCodes.UnresolvedValue,
                "Configure(...) is called on a pipeline builder that is not traced to an AddStandardResilienceHandler call; not interpreted.",
                outermost.Syntax);
            return;
        }

        var pending = GetOrCreateStandard(add, conditional);
        chain.Reverse();
        foreach (var call in chain.Where(c => _appliedConfigureCalls.Add(c.Syntax)))
        {
            ApplyOptions(call, pending.Standard!);
        }
    }

    /// <summary>Applies a <c>section</c> or <c>configure</c> argument, if the call has one.</summary>
    private void ApplyOptions(IInvocationOperation call, StandardHandlerSettings settings)
    {
        if (Argument(call, "section") is { } section)
        {
            var location = context.LocationOf(section.Syntax);
            if (SectionPath(section) is not { } path)
            {
                MarkAllUnresolved(settings, $"configuration section not resolved: {Shorten(section.Syntax)}", location);
                context.Warn(DiagnosticCodes.UnresolvedValue, $"Configuration section '{Shorten(section.Syntax)}' could not be resolved to a constant path.", section.Syntax);
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
                MarkAllUnresolved(settings, $"options delegate is not a lambda: {Shorten(configure.Syntax)}", context.LocationOf(configure.Syntax));
                context.Warn(DiagnosticCodes.OpaqueDelegate, $"Options delegate '{Shorten(configure.Syntax)}' is not a lambda; its settings are unresolved.", configure.Syntax);
            }
        }
    }

    /// <summary>
    /// Interprets <c>o.Path = value;</c> and <c>o.Retry.DisableFor...(...)</c> statements. Anything else is reported,
    /// and any modelled option assigned inside it becomes unresolved.
    /// </summary>
    private void ApplyLambda(IAnonymousFunctionOperation lambda, StandardHandlerSettings settings)
    {
        var options = lambda.Symbol.Parameters[0];

        foreach (var statement in Statements(lambda))
        {
            var expression = ExpressionOf(statement);

            if (expression is ISimpleAssignmentOperation assignment && PathFrom(assignment.Target, options) is { } path)
            {
                if (StandardHandlerSettings.TryGetKind(path, out var kind))
                {
                    if (context.Evaluate(assignment.Value, kind, settings.Current(path), context.LocationOf(assignment.Syntax)) is { } value)
                    {
                        settings.Set(path, value);
                    }
                }
                else
                {
                    context.Info(DiagnosticCodes.UnresolvedValue, $"Option '{path}' is not modelled; ignored.", assignment.Syntax);
                }

                continue;
            }

            if (expression is IInvocationOperation call && PathFrom(Argument(call, ordinal: 0), options) == "Retry"
                && TryDisableMethods(call, settings))
            {
                continue;
            }

            context.Warn(DiagnosticCodes.UnresolvedValue, $"Statement not interpreted: {Shorten(statement.Syntax)}", statement.Syntax);
            foreach (var nested in statement.Descendants().OfType<ISimpleAssignmentOperation>())
            {
                if (PathFrom(nested.Target, options) is { } nestedPath
                    && StandardHandlerSettings.TryGetKind(nestedPath, out _)
                    && settings.Current(nestedPath) is { } fallback)
                {
                    settings.Set(nestedPath, Values.Unresolved<object>(fallback, $"assigned inside an uninterpreted statement: {Shorten(nested.Syntax)}", context.LocationOf(nested.Syntax)));
                }
            }
        }
    }

    /// <summary><c>o.Retry.DisableForUnsafeHttpMethods()</c> and <c>o.Retry.DisableFor(HttpMethod.Post, ...)</c>.</summary>
    private bool TryDisableMethods(IInvocationOperation call, StandardHandlerSettings settings)
    {
        if (KnownApis.DisableForUnsafeHttpMethods.Matches(call.TargetMethod))
        {
            settings.DisabledForMethods.UnionWith(KnownApis.UnsafeHttpMethods);
            return true;
        }

        if (!KnownApis.DisableFor.Matches(call.TargetMethod))
        {
            return false;
        }

        var elements = Unwrap(Argument(call, "methods")) is IArrayCreationOperation { Initializer: { } initializer }
            ? initializer.ElementValues
            : ImmutableArray<IOperation>.Empty;

        foreach (var element in elements)
        {
            if (Unwrap(element) is IPropertyReferenceOperation { Property: { IsStatic: true } property }
                && property.ContainingType.ToDisplayString() == "System.Net.Http.HttpMethod")
            {
                _ = settings.DisabledForMethods.Add(property.Name.ToUpperInvariant());
            }
            else
            {
                context.Warn(DiagnosticCodes.UnresolvedValue, $"HTTP method '{Shorten(element.Syntax)}' is not a constant; retries for it are assumed enabled.", element.Syntax);
            }
        }

        return true;
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

    /// <summary>
    /// Follows an <c>IHttpClientBuilder</c> expression back to the client (or defaults) it configures. With
    /// <paramref name="register"/> false it has no side effects (used for branches that do not run).
    /// </summary>
    private BuilderTarget? ResolveBuilder(IOperation? operation, bool register)
    {
        switch (Unwrap(operation))
        {
            case IParameterReferenceOperation parameter when _defaultsBuilders.Contains(parameter.Parameter):
                return DefaultsTarget.Instance;

            case IInvocationOperation call when KnownApis.AddHttpClient.Matches(call.TargetMethod):
                return GetOrAddClient(call, register) is { } client ? new ClientTarget(client) : null;

            // A removal in the chain runs before whatever is chained after it.
            case IInvocationOperation call when KnownApis.RemoveAllResilienceHandlers.Matches(call.TargetMethod):
                if (register)
                {
                    RecordRemoval(call, conditional: false);
                }

                return ResolveBuilder(Argument(call, ordinal: 0), register);

            // A builder extension that returns its receiver (e.g. ConfigureHttpClient, AddHttpMessageHandler).
            case IInvocationOperation call when call.TargetMethod.IsExtensionMethod
                && IsHttpClientBuilder(call.TargetMethod.ReturnType)
                && call.TargetMethod.Parameters.Length > 0
                && IsHttpClientBuilder(call.TargetMethod.Parameters[0].Type):
                return ResolveBuilder(Argument(call, ordinal: 0), register);

            // var search = services.AddHttpClient(...); search.AddStandardResilienceHandler(); (initializer only).
            case ILocalReferenceOperation local when LocalInitializer(local) is { } initializer:
                return ResolveBuilder(initializer, register);

            default:
                return null;
        }
    }

    /// <summary>The initializer of a local variable, if it has one. Later reassignments are not tracked.</summary>
    private static IOperation? LocalInitializer(ILocalReferenceOperation local) =>
        local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value }
            ? local.SemanticModel?.GetOperation(value)
            : null;

    private static string EnclosingMemberName(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol is null ? "<unknown>" : $"{symbol.ContainingType?.Name}.{symbol.Name}";
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

    private sealed class PendingHandler(int sequence, BuilderTarget? target, SourceLocation location)
    {
        public int Sequence { get; } = sequence;

        public BuilderTarget? Target { get; } = target;

        public SourceLocation Location { get; } = location;

        public bool Conditional { get; set; }

        /// <summary>For the standard handler: settings that Configure(...) calls may still change.</summary>
        public StandardHandlerSettings? Standard { get; set; }

        /// <summary>For other handlers: built on sight.</summary>
        public Handler? Built { get; set; }
    }

    private abstract record BuilderTarget;

    private sealed record DefaultsTarget : BuilderTarget
    {
        public static readonly DefaultsTarget Instance = new();
    }

    private sealed record ClientTarget(ClientRecord Client) : BuilderTarget;
}
