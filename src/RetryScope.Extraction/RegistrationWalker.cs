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
/// Calls into methods that have source in the solution (helpers, Aspire ServiceDefaults) are followed with a
/// <see cref="CallFrame"/>, so a helper's parameters resolve to the caller's arguments; library calls are matched by
/// resolved symbol (<see cref="KnownApis"/>). Call <see cref="Complete"/> after the last root.
/// </summary>
/// <remarks>
/// Branches guarded by host-environment checks are decided for the configured environment; registrations under any
/// other condition are kept (both branches) and marked conditional, with a warning when something is registered there.
/// Anything not understood produces a diagnostic rather than a silent guess.
/// </remarks>
internal sealed class RegistrationWalker
{
    private const int MaxDepth = 8;

    private readonly Solution _solution;
    private readonly ExtractionContext _context;
    private readonly AppSettingsReader? _appSettings;
    private readonly HandlerReaders _readers;

    // Methods on the current call path (recursion guard). A helper called from two places is walked twice, once per
    // call frame, because it can register different clients each time.
    private readonly HashSet<string> _callPath = new(StringComparer.Ordinal);

    // Lambda parameters that stand for "every client" (the IHttpClientBuilder inside ConfigureHttpClientDefaults).
    private readonly HashSet<ISymbol> _defaultsBuilders = new(SymbolEqualityComparer.Default);

    // Handlers are collected first and built in Complete(), so that chained Configure(...) calls, which the pre-order
    // walk sees before the AddStandardResilienceHandler call they wrap, are applied in runtime order. Sequence numbers
    // order handlers and removals as they run. Keys include the frame: the same syntax in a helper is a different
    // registration for each call.
    private readonly Dictionary<(SyntaxNode Node, CallFrame Frame), PendingHandler> _pending = new();
    private readonly HashSet<(SyntaxNode, CallFrame)> _appliedConfigureCalls = [];
    private readonly HashSet<(SyntaxNode, CallFrame)> _recordedRemovals = [];
    private readonly HashSet<(SyntaxNode, CallFrame)> _reportedOutOfScope = [];
    private readonly HashSet<SyntaxNode> _warnedConditions = [];
    private readonly HashSet<SyntaxNode> _reportedUnbound = [];
    private int _sequence;
    private CallFrame _frame = CallFrame.Root;

    public RegistrationWalker(Solution solution, ExtractionContext context, AppSettingsReader? appSettings)
    {
        _solution = solution;
        _context = context;
        _appSettings = appSettings;
        _readers = new HandlerReaders(context);
        _context.Resolve = operation => _frame.Resolve(operation).Operation;
    }

    public Dictionary<string, ClientRecord> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>Handlers from <c>ConfigureHttpClientDefaults</c>; the first one registered is outermost.</summary>
    public List<Handler> DefaultHandlers { get; } = [];

    /// <summary>Whether a defaults handler was registered under a condition (then every client is conditional).</summary>
    public bool DefaultsConditional { get; private set; }

    public Task WalkAsync(IMethodSymbol root, CancellationToken cancellationToken) =>
        WalkMethodAsync(root, depth: 0, Guard.None, CallFrame.Root, cancellationToken);

    /// <summary>Builds the collected handlers and attaches them to their clients (or to the defaults).</summary>
    public void Complete()
    {
        foreach (var pending in _pending.Values.OrderBy(p => p.Sequence))
        {
            var origin = pending.Target is DefaultsTarget ? HandlerOrigin.Defaults : HandlerOrigin.PerClient;

            switch (pending.Target)
            {
                case DefaultsTarget:
                    DefaultHandlers.Add(Build(pending, origin));
                    DefaultsConditional |= pending.Conditional;
                    break;
                case ClientTarget(var client):
                    client.Registered.Add((pending.Sequence, Build(pending, origin)));
                    client.Conditional |= pending.Conditional;
                    break;
            }
        }

        _pending.Clear();

        static Handler Build(PendingHandler pending, HandlerOrigin origin) =>
            (pending.Built ?? pending.Standard!.Build(origin, pending.Location)) with { Origin = origin };
    }

    /// <summary>
    /// Records every <c>new HttpClient(...)</c> in the project. Such clients bypass IHttpClientFactory, so no factory
    /// handler applies to them; they are reported as direct clients with no resilience (diagnostic RS003).
    /// </summary>
    public async Task ScanDirectClientsAsync(Project project, CancellationToken cancellationToken)
    {
        _frame = CallFrame.Root;
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
                    client = new ClientRecord(name, ClientKind.Direct, _context.LocationOf(creation));
                    Clients.Add($"direct:{name}", client);
                    _context.Info(DiagnosticCodes.DirectHttpClient,
                        $"'new HttpClient()' in {name} bypasses IHttpClientFactory; no factory resilience handlers apply.", creation);
                }

                foreach (var initializer in operation.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
                {
                    if (initializer is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "BaseAddress" } } assignment)
                    {
                        ReadUri(assignment, client, CallFrame.Root);
                    }
                }
            }
        }
    }

    private async Task WalkMethodAsync(IMethodSymbol method, int depth, Guard guard, CallFrame frame, CancellationToken cancellationToken)
    {
        var key = CallFrame.KeyOf(method);
        if (!_callPath.Add(key))
        {
            return; // recursion
        }

        var previous = _frame;
        try
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var document = _solution.GetDocument(reference.SyntaxTree);
                if (document is null || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
                {
                    _context.Warn(DiagnosticCodes.WorkspaceLoad, $"Source of '{method.ToDisplayString()}' is not part of the loaded solution; not followed.");
                    continue;
                }

                var body = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

                // Pre-order: an outer call (e.g. ConfigureHttpClientDefaults) is seen before the calls inside its lambda.
                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    _frame = frame;
                    await VisitInvocationAsync(invocation, model, body, depth, guard, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _frame = previous;
            _callPath.Remove(key);
        }
    }

    private async Task VisitInvocationAsync(
        InvocationExpressionSyntax syntax, SemanticModel model, SyntaxNode body, int depth, Guard inherited, CancellationToken cancellationToken)
    {
        var operation = model.GetOperation(syntax, cancellationToken);
        if (operation is not IInvocationOperation invocation)
        {
            // Dev-set finding D1: report calls on the walked path that do not bind; whatever they register is missed.
            if (operation is IInvalidOperation && _reportedUnbound.Add(syntax))
            {
                _context.Warn(DiagnosticCodes.UnboundCall, $"Call '{Shorten(syntax)}' does not bind; anything it registers is not extracted.", syntax);
            }

            return;
        }

        var (skip, guard) = BranchState(syntax, model, body, inherited);
        var method = invocation.TargetMethod;
        var frame = _frame;

        if (skip)
        {
            // The branch does not run in this environment, but the client's behaviour still depends on it.
            if (IsHandlerRegistration(method))
            {
                switch (ResolveBuilder(Argument(invocation, ordinal: 0), frame, register: false))
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
            if (GetOrAddClient(invocation, frame, register: true) is { } client)
            {
                client.Conditional |= Note(guard);
            }
        }
        else if (KnownApis.AddStandardResilienceHandler.Matches(method))
        {
            _ = GetOrCreateStandard(invocation, frame, guard);
        }
        else if (KnownApis.ConfigureStandardHandler.Matches(method))
        {
            ApplyConfigureChain(invocation, frame, guard);
        }
        else if (KnownApis.AddResilienceHandler.Matches(method))
        {
            CreateHandler(invocation, frame, guard, _readers.ReadCustomPipeline);
        }
        else if (KnownApis.AddStandardHedgingHandler.Matches(method))
        {
            CreateHandler(invocation, frame, guard, _readers.ReadStandardHedging);
        }
        else if (KnownApis.AddPollyV7Policy.Matches(method))
        {
            CreateHandler(invocation, frame, guard, _readers.ReadPollyV7);
        }
        else if (KnownApis.RemoveAllResilienceHandlers.Matches(method))
        {
            RecordRemoval(invocation, frame, guard);
        }
        else if (KnownApis.AddGrpcClient.Matches(method))
        {
            ReportOutOfScope(invocation, frame);
        }
        else if (KnownApis.AddResiliencePipeline.Matches(method))
        {
            _context.Warn(DiagnosticCodes.UnlinkedPipeline,
                "Keyed resilience pipeline registered in DI; code that executes it around HttpClient calls is not linked to a client in V1.",
                invocation.Syntax);
        }
        else if (depth < MaxDepth)
        {
            // Our own code (a helper extension, Aspire ServiceDefaults): follow it with the caller's arguments.
            var declared = (method.ReducedFrom ?? method).OriginalDefinition;
            if (declared.DeclaringSyntaxReferences.Length > 0)
            {
                await WalkMethodAsync(declared, depth + 1, guard, frame.Enter(declared, invocation), cancellationToken).ConfigureAwait(false);
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
    /// Adds the <c>if</c> branches and <c>?:</c> arms enclosing the call (within the walked method) to the inherited
    /// guard. Returns skip = true when a host-environment condition rules the branch out.
    /// </summary>
    private (bool Skip, Guard Guard) BranchState(SyntaxNode syntax, SemanticModel model, SyntaxNode root, Guard guard)
    {
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

            switch (ConditionEvaluator.Evaluate(model.GetOperation(condition), _context.Environment))
            {
                case null:
                    guard = guard with { Conditional = true, Undecided = guard.Undecided.Add(condition) };
                    break;
                case { } taken when taken != inTrueBranch:
                    return (true, guard with { Conditional = true });
                default:
                    guard = guard with { Conditional = true };
                    break;
            }
        }

        return (false, guard);
    }

    /// <summary>
    /// Called when something is registered under <paramref name="guard"/>: warns once per undecidable condition
    /// (dev-set finding D3: conditions that guard no registration stay silent) and returns whether it is conditional.
    /// </summary>
    private bool Note(Guard guard)
    {
        foreach (var condition in guard.Undecided.Where(_warnedConditions.Add))
        {
            _context.Warn(DiagnosticCodes.UnresolvedValue,
                $"Condition '{Shorten(condition)}' cannot be decided statically; registrations in both branches are kept and marked conditional.",
                condition);
        }

        return guard.Conditional;
    }

    private void RegisterDefaultsLambda(IInvocationOperation invocation)
    {
        if (Lambda(Argument(invocation, "configure")) is { Symbol.Parameters.Length: 1 } lambda)
        {
            _ = _defaultsBuilders.Add(lambda.Symbol.Parameters[0]);
            return;
        }

        _context.Warn(DiagnosticCodes.UnresolvedValue,
            "ConfigureHttpClientDefaults is called with something other than a lambda; its handlers are not followed yet.",
            invocation.Syntax);
    }

    /// <summary>The client an <c>AddHttpClient</c> call configures; created only when <paramref name="register"/>.</summary>
    private ClientRecord? GetOrAddClient(IInvocationOperation invocation, CallFrame frame, bool register)
    {
        var method = invocation.TargetMethod;
        string name;
        ClientKind kind;

        if (method.TypeArguments.Length > 0)
        {
            // Typed client: the factory names it after the short type name (TypeNameHelper, fullName: false). In a
            // generic helper the type argument comes from the caller (dev-set finding D4).
            var type = frame.Substitute(method.TypeArguments[0]);
            name = type.Name;
            kind = ClientKind.Typed;
            if (type is ITypeParameterSymbol && register)
            {
                _context.Warn(DiagnosticCodes.UnresolvedValue, $"Typed client type '{type.Name}' is a type parameter whose argument is unknown here.", invocation.Syntax);
            }
        }
        else if (Argument(invocation, "name") is { } nameArgument)
        {
            if (frame.Resolve(nameArgument).Operation?.ConstantValue is { HasValue: true, Value: string constant })
            {
                name = constant;

                // Dev-set finding D6: the empty name is the default client (Options.DefaultName).
                kind = constant.Length == 0 ? ClientKind.Default : ClientKind.Named;
            }
            else
            {
                var location = _context.LocationOf(nameArgument.Syntax);
                name = $"<unresolved:{location.File}:{location.Line}>";
                kind = ClientKind.Named;
                if (register)
                {
                    _context.Warn(DiagnosticCodes.UnresolvedValue, $"Client name '{Shorten(nameArgument.Syntax)}' is not a compile-time constant.", nameArgument.Syntax);
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

            client = new ClientRecord(name, kind, _context.LocationOf(MethodNameSyntax(invocation.Syntax)));
            Clients.Add(name, client);
        }

        if (register && Lambda(Argument(invocation, "configureClient")) is { } configureClient)
        {
            foreach (var assignment in configureClient.Body.Descendants().OfType<ISimpleAssignmentOperation>())
            {
                if (assignment.Target is IPropertyReferenceOperation { Property.Name: "BaseAddress" })
                {
                    ReadUri(assignment, client, frame);
                }
            }
        }

        return client;
    }

    /// <summary>
    /// Reads <c>BaseAddress = new Uri("...")</c>, following locals and helper parameters (dev-set findings D4, D5);
    /// anything non-constant is recorded as unresolved.
    /// </summary>
    private void ReadUri(ISimpleAssignmentOperation assignment, ClientRecord client, CallFrame frame)
    {
        var location = _context.LocationOf(assignment.Syntax);
        (var value, frame) = frame.Resolve(assignment.Value);

        if (value is IObjectCreationOperation create
            && create.Type?.ToDisplayString() == "System.Uri"
            && create.Arguments.Length > 0
            && frame.Resolve(create.Arguments[0].Value).Operation?.ConstantValue is { HasValue: true, Value: string text }
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
    private PendingHandler GetOrCreateStandard(IInvocationOperation add, CallFrame frame, Guard guard)
    {
        if (_pending.TryGetValue((add.Syntax, frame), out var existing))
        {
            existing.Conditional |= Note(guard);
            return existing;
        }

        var pending = NewPending(add, frame, guard);
        pending.Standard = new StandardHandlerSettings(_context.ResilienceVersion);

        // AddStandardResilienceHandler(section) / AddStandardResilienceHandler(o => ...).
        ApplyOptions(add, pending.Standard);
        return pending;
    }

    /// <summary>Records a non-standard handler (custom pipeline, hedging, Polly v7) built by <paramref name="read"/>.</summary>
    private void CreateHandler(IInvocationOperation call, CallFrame frame, Guard guard, Func<IInvocationOperation, SourceLocation, Handler> read)
    {
        if (_pending.ContainsKey((call.Syntax, frame)))
        {
            return;
        }

        var pending = NewPending(call, frame, guard);
        pending.Built = read(call, pending.Location);
    }

    /// <summary>Resolves the client first (which may record a chained removal), then takes the next sequence number.</summary>
    private PendingHandler NewPending(IInvocationOperation call, CallFrame frame, Guard guard)
    {
        var location = _context.LocationOf(MethodNameSyntax(call.Syntax));
        var target = ResolveBuilder(Argument(call, ordinal: 0), frame, register: true);
        if (target is null)
        {
            _context.Warn(DiagnosticCodes.UnresolvedValue, "Could not determine which HttpClient this resilience handler belongs to.", call.Syntax);
        }

        var pending = new PendingHandler(++_sequence, target, location) { Conditional = Note(guard) };
        _pending.Add((call.Syntax, frame), pending);
        return pending;
    }

    private void RecordRemoval(IInvocationOperation removal, CallFrame frame, Guard guard)
    {
        if (!_recordedRemovals.Add((removal.Syntax, frame)))
        {
            return;
        }

        switch (ResolveBuilder(Argument(removal, ordinal: 0), frame, register: true))
        {
            case ClientTarget(var client):
                client.RemovedAt = ++_sequence;
                client.Conditional |= Note(guard);
                break;
            case OutOfScopeTarget:
                break;
            default:
                _context.Warn(DiagnosticCodes.UnresolvedValue, "RemoveAllResilienceHandlers on a builder that is not traced to one client; ignored.", removal.Syntax);
                break;
        }
    }

    /// <summary>Dev-set finding D7: clients outside V1 scope (gRPC) are reported, not silently dropped.</summary>
    private void ReportOutOfScope(IInvocationOperation call, CallFrame frame)
    {
        if (_reportedOutOfScope.Add((call.Syntax, frame)))
        {
            var type = call.TargetMethod.TypeArguments.Length > 0 ? frame.Substitute(call.TargetMethod.TypeArguments[0]).ToDisplayString() : "?";
            _context.Info(DiagnosticCodes.OutOfScopeClient,
                $"gRPC client '{type}' is out of V1 scope (ADR 0002); ConfigureHttpClientDefaults handlers also apply to it.", call.Syntax);
        }
    }

    /// <summary>
    /// Applies <c>.Configure(...)</c> calls chained on a standard handler, innermost first, which is the order they run in.
    /// Visiting the outermost call applies the whole chain; inner calls visited later are skipped.
    /// </summary>
    private void ApplyConfigureChain(IInvocationOperation outermost, CallFrame frame, Guard guard)
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
            _context.Warn(DiagnosticCodes.UnresolvedValue,
                "Configure(...) is called on a pipeline builder that is not traced to an AddStandardResilienceHandler call; not interpreted.",
                outermost.Syntax);
            return;
        }

        var pending = GetOrCreateStandard(add, frame, guard);
        chain.Reverse();
        foreach (var call in chain.Where(c => _appliedConfigureCalls.Add((c.Syntax, frame))))
        {
            ApplyOptions(call, pending.Standard!);
        }
    }

    /// <summary>Applies a <c>section</c> or <c>configure</c> argument, if the call has one.</summary>
    private void ApplyOptions(IInvocationOperation call, StandardHandlerSettings settings)
    {
        if (Argument(call, "section") is { } section)
        {
            var location = _context.LocationOf(section.Syntax);
            if (SectionPath(section, _frame) is not { } path)
            {
                MarkAllUnresolved(settings, $"configuration section not resolved: {Shorten(section.Syntax)}", location);
                _context.Warn(DiagnosticCodes.UnresolvedValue, $"Configuration section '{Shorten(section.Syntax)}' could not be resolved to a constant path.", section.Syntax);
            }
            else if (_appSettings is null)
            {
                MarkAllUnresolved(settings, $"configuration section {path}: no appsettings files available", location);
            }
            else
            {
                _appSettings.Apply(path, settings, location);
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
                MarkAllUnresolved(settings, $"options delegate is not a lambda: {Shorten(configure.Syntax)}", _context.LocationOf(configure.Syntax));
                _context.Warn(DiagnosticCodes.OpaqueDelegate, $"Options delegate '{Shorten(configure.Syntax)}' is not a lambda; its settings are unresolved.", configure.Syntax);
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
                    if (_context.Evaluate(assignment.Value, kind, settings.Current(path), _context.LocationOf(assignment.Syntax)) is { } value)
                    {
                        settings.Set(path, value);
                    }
                }
                else
                {
                    _context.Info(DiagnosticCodes.UnresolvedValue, $"Option '{path}' is not modelled; ignored.", assignment.Syntax);
                }

                continue;
            }

            if (expression is IInvocationOperation call && PathFrom(Argument(call, ordinal: 0), options) == "Retry"
                && TryDisableMethods(call, settings))
            {
                continue;
            }

            _context.Warn(DiagnosticCodes.UnresolvedValue, $"Statement not interpreted: {Shorten(statement.Syntax)}", statement.Syntax);
            foreach (var nested in statement.Descendants().OfType<ISimpleAssignmentOperation>())
            {
                if (PathFrom(nested.Target, options) is { } nestedPath
                    && StandardHandlerSettings.TryGetKind(nestedPath, out _)
                    && settings.Current(nestedPath) is { } fallback)
                {
                    settings.Set(nestedPath, Values.Unresolved<object>(fallback, $"assigned inside an uninterpreted statement: {Shorten(nested.Syntax)}", _context.LocationOf(nested.Syntax)));
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
                _context.Warn(DiagnosticCodes.UnresolvedValue, $"HTTP method '{Shorten(element.Syntax)}' is not a constant; retries for it are assumed enabled.", element.Syntax);
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves <c>config.GetSection("A").GetSection("B")</c> to <c>A:B</c>. The chain must start at the root
    /// configuration (a property or parameter typed as root configuration); anything else returns null.
    /// </summary>
    private static string? SectionPath(IOperation? operation, CallFrame frame)
    {
        (operation, frame) = frame.Resolve(operation);
        switch (operation)
        {
            case IInvocationOperation { TargetMethod.Name: "GetSection" } call
                when call.TargetMethod.ContainingType?.ToDisplayString() == KnownApis.ConfigurationInterface:
                if (frame.Resolve(Argument(call, "key")).Operation?.ConstantValue is not { HasValue: true, Value: string key })
                {
                    return null;
                }

                var prefix = SectionPath(call.Instance, frame);
                return prefix is null ? null : prefix.Length == 0 ? key : $"{prefix}:{key}";

            case { } root when root is IPropertyReferenceOperation or IParameterReferenceOperation
                && root.Type?.ToDisplayString() is { } type && KnownApis.RootConfigurationTypes.Contains(type):
                return string.Empty;

            default:
                return null;
        }
    }

    /// <summary>
    /// Follows an <c>IHttpClientBuilder</c> expression back to the client (or defaults) it configures, through helper
    /// parameters and locals. With <paramref name="register"/> false it has no side effects (branches that do not run).
    /// </summary>
    private BuilderTarget? ResolveBuilder(IOperation? operation, CallFrame frame, bool register)
    {
        (operation, frame) = frame.Resolve(operation);
        switch (operation)
        {
            case IParameterReferenceOperation parameter when _defaultsBuilders.Contains(parameter.Parameter):
                return DefaultsTarget.Instance;

            case IInvocationOperation call when KnownApis.AddHttpClient.Matches(call.TargetMethod):
                return GetOrAddClient(call, frame, register) is { } client ? new ClientTarget(client) : null;

            case IInvocationOperation call when KnownApis.AddGrpcClient.Matches(call.TargetMethod):
                return OutOfScopeTarget.Instance;

            // A removal in the chain runs before whatever is chained after it.
            case IInvocationOperation call when KnownApis.RemoveAllResilienceHandlers.Matches(call.TargetMethod):
                if (register)
                {
                    RecordRemoval(call, frame, Guard.None);
                }

                return ResolveBuilder(Argument(call, ordinal: 0), frame, register);

            // A builder extension that returns its receiver (e.g. ConfigureHttpClient, AddHttpMessageHandler).
            case IInvocationOperation call when call.TargetMethod.IsExtensionMethod
                && IsHttpClientBuilder(call.TargetMethod.ReturnType)
                && call.TargetMethod.Parameters.Length > 0
                && IsHttpClientBuilder(call.TargetMethod.Parameters[0].Type):
                return ResolveBuilder(Argument(call, ordinal: 0), frame, register);

            default:
                return null;
        }
    }

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

    /// <summary>The conditions a call is under: whether any applies, and those that could not be decided.</summary>
    private sealed record Guard(bool Conditional, ImmutableList<SyntaxNode> Undecided)
    {
        public static readonly Guard None = new(false, ImmutableList<SyntaxNode>.Empty);
    }

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

    /// <summary>A builder for a client outside V1 scope (gRPC): its handlers are not modelled.</summary>
    private sealed record OutOfScopeTarget : BuilderTarget
    {
        public static readonly OutOfScopeTarget Instance = new();
    }
}
