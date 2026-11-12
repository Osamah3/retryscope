using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using RetryScope.Model;
using static RetryScope.Extraction.OperationHelpers;

namespace RetryScope.Extraction;

/// <summary>
/// Reads the handlers other than the standard one: custom Polly v8 pipelines (<c>AddResilienceHandler</c>), the
/// standard hedging handler and Polly v7 policies. Defaults are those in docs/verified-defaults.md.
/// </summary>
internal sealed class HandlerReaders(ExtractionContext context)
{
    /// <summary>
    /// <c>AddResilienceHandler(name, b =&gt; { b.AddRetry(...); b.AddTimeout(...); })</c>. Strategies are listed in the
    /// order added, which is outermost first in Polly v8. A timeout added after a retry runs inside it, so it bounds each
    /// attempt; a timeout added before any retry bounds the whole call.
    /// </summary>
    public Handler ReadCustomPipeline(IInvocationOperation addResilienceHandler, SourceLocation location)
    {
        var name = Argument(addResilienceHandler, "pipelineName")?.ConstantValue is { HasValue: true, Value: string constant } ? constant : null;
        var strategies = new List<Strategy>();

        if (Lambda(Argument(addResilienceHandler, "configure")) is not { Symbol.Parameters.Length: > 0 } lambda)
        {
            context.Warn(DiagnosticCodes.OpaqueDelegate, "Custom pipeline is not configured by a lambda; its strategies are unknown.", addResilienceHandler.Syntax);
            return new Handler(HandlerKind.Custom, HandlerOrigin.PerClient, strategies, name, location);
        }

        var builder = lambda.Symbol.Parameters[0];
        var sawRetry = false;

        foreach (var statement in Statements(lambda))
        {
            if (ChainFrom(ExpressionOf(statement), builder) is not { } calls)
            {
                context.Warn(DiagnosticCodes.UnresolvedValue, $"Statement in custom pipeline not interpreted: {Shorten(statement.Syntax)}", statement.Syntax);
                continue;
            }

            foreach (var call in calls)
            {
                if (ReadStrategy(call, sawRetry) is { } strategy)
                {
                    strategies.Add(strategy);
                    sawRetry |= strategy is RetryStrategy;
                }
            }
        }

        return new Handler(HandlerKind.Custom, HandlerOrigin.PerClient, strategies, name, location);
    }

    /// <summary>
    /// <c>AddStandardHedgingHandler()</c>: recorded with its outer total timeout and per-endpoint circuit breaker. The
    /// hedged attempt count is not bounded in V1 (labelling guide P11), and the per-endpoint timeout is not an attempt
    /// timeout in the guide's sense, so neither is represented.
    /// </summary>
    public Handler ReadStandardHedging(IInvocationOperation call, SourceLocation location)
    {
        context.Info(DiagnosticCodes.HedgingNotSimulated, "Standard hedging handler: recorded, attempt bound unknown, not simulated in V1.", call.Syntax);
        var version = context.ResilienceVersion;
        return new Handler(
            HandlerKind.Hedging,
            HandlerOrigin.PerClient,
            [
                new TimeoutStrategy(TimeoutRole.Total, Values.PackageDefault(TimeSpan.FromSeconds(30), version)),
                Breaker(http: true, options: null),
            ],
            Location: location);
    }

    /// <summary>
    /// Polly v7 (<c>AddTransientHttpErrorPolicy(p =&gt; p.WaitAndRetryAsync(2, ...))</c>): only the literal retry count
    /// is read; delays are left opaque. Recorded, not simulated in V1.
    /// </summary>
    public Handler ReadPollyV7(IInvocationOperation call, SourceLocation location)
    {
        context.Info(DiagnosticCodes.PollyV7NotSimulated, "Polly v7 policy: retry count read from literals; not simulated in V1.", call.Syntax);

        var policyCalls = call.Arguments.Skip(1).SelectMany(a => a.Value.DescendantsAndSelf()).OfType<IInvocationOperation>().ToList();
        var strategies = new List<Strategy>();

        foreach (var policy in policyCalls)
        {
            Value<int>? retries = null;
            if (KnownApis.PollyV7RetryForever.Matches(policy.TargetMethod))
            {
                retries = Values.Explicit(int.MaxValue, context.LocationOf(policy.Syntax));
            }
            else if (KnownApis.PollyV7Retry.Matches(policy.TargetMethod))
            {
                var countArgument = Argument(policy, "retryCount");
                retries = countArgument is null
                    ? Values.Explicit(1, context.LocationOf(policy.Syntax)) // RetryAsync() with no count retries once.
                    : ConstantEvaluator.TryInt(countArgument, out var count)
                        ? Values.Explicit(count, context.LocationOf(countArgument.Syntax))
                        : Values.Unresolved(1, Shorten(countArgument.Syntax), context.LocationOf(countArgument.Syntax));
            }

            if (retries is null)
            {
                continue;
            }

            var opaque = $"Polly v7 {policy.TargetMethod.Name}: delays come from a sleep-duration delegate";
            var at = context.LocationOf(policy.Syntax);
            strategies.Add(new RetryStrategy(
                retries,
                Values.Opaque(TimeSpan.Zero, opaque, at),
                Values.Opaque(BackoffType.Constant, opaque, at),
                Values.Opaque(false, opaque, at),
                Values.PackageDefault(false, "Polly v7 does not read Retry-After"),
                Values.PackageDefault(PredicateKind.DefaultTransient, "Microsoft.Extensions.Http.Polly HandleTransientHttpError: HttpRequestException, 5xx, 408 (not 429)"),
                Array.Empty<string>()));
        }

        if (strategies.Count == 0)
        {
            context.Warn(DiagnosticCodes.PollyV7NotSimulated, "Polly v7 policy without a recognisable retry; recorded with no strategies.", call.Syntax);
        }

        return new Handler(HandlerKind.PollyV7, HandlerOrigin.PerClient, strategies, Location: location);
    }

    private Strategy? ReadStrategy(IInvocationOperation call, bool afterRetry)
    {
        var method = call.TargetMethod;

        if (KnownApis.AddRetry.Matches(method))
        {
            var options = Argument(call, "options");
            return Retry(IsType(options, KnownApis.HttpRetryOptions), options);
        }

        if (KnownApis.AddTimeout.Matches(method))
        {
            var role = afterRetry ? TimeoutRole.Attempt : TimeoutRole.Total;
            if (Argument(call, "timeout") is { } timeout)
            {
                var at = context.LocationOf(timeout.Syntax);
                return new TimeoutStrategy(role, ConstantEvaluator.TryTimeSpan(timeout, out var value)
                    ? Values.Explicit(value, at)
                    : Values.Unresolved(TimeSpan.FromSeconds(30), Shorten(timeout.Syntax), at));
            }

            var bag = new OptionBag().Define("Timeout", SettingKind.TimeSpan, TimeSpan.FromSeconds(30), context.PollyVersion);
            bag.Read(Argument(call, "options"), context);
            return new TimeoutStrategy(role, bag.Get<TimeSpan>("Timeout"));
        }

        if (KnownApis.AddCircuitBreaker.Matches(method))
        {
            var options = Argument(call, "options");
            return Breaker(IsType(options, KnownApis.HttpCircuitBreakerOptions), options);
        }

        if (KnownApis.AddConcurrencyLimiter.Matches(method))
        {
            var bag = new OptionBag()
                .Define("PermitLimit", SettingKind.Int, null, context.PollyVersion)
                .Define("QueueLimit", SettingKind.Int, 0, context.PollyVersion);
            if (Argument(call, "options") is { } limiterOptions)
            {
                bag.Read(limiterOptions, context);
            }
            else
            {
                Set(bag, "PermitLimit", "permitLimit", call);
                Set(bag, "QueueLimit", "queueLimit", call);
            }

            return bag.GetOptional<int>("PermitLimit") is { } permits
                ? new RateLimiterStrategy(permits, bag.Get<int>("QueueLimit"))
                : null;
        }

        context.Warn(DiagnosticCodes.UnresolvedValue, $"Strategy '{method.Name}' is not modelled in V1; ignored.", call.Syntax);
        return null;
    }

    /// <summary>Retry options: HTTP defaults for <c>HttpRetryStrategyOptions</c>, plain Polly defaults otherwise.</summary>
    private RetryStrategy Retry(bool http, IOperation? options)
    {
        var version = http ? context.ResilienceVersion : context.PollyVersion;
        var bag = new OptionBag()
            .Define("MaxRetryAttempts", SettingKind.Int, 3, version)
            .Define("Delay", SettingKind.TimeSpan, TimeSpan.FromSeconds(2), version)
            .Define("BackoffType", SettingKind.Backoff, http ? BackoffType.Exponential : BackoffType.Constant, version)
            .Define("UseJitter", SettingKind.Bool, http, version)
            .Define("ShouldRetryAfterHeader", SettingKind.Bool, http, version)
            .Define("MaxDelay", SettingKind.TimeSpan, null, version)
            .Define("ShouldHandle", SettingKind.Predicate, http ? PredicateKind.DefaultTransient : PredicateKind.ExceptionsOnly, version);
        bag.Read(options, context);

        return new RetryStrategy(
            bag.Get<int>("MaxRetryAttempts"),
            bag.Get<TimeSpan>("Delay"),
            bag.Get<BackoffType>("BackoffType"),
            bag.Get<bool>("UseJitter"),
            bag.Get<bool>("ShouldRetryAfterHeader"),
            bag.Get<PredicateKind>("ShouldHandle"),
            Array.Empty<string>(),
            bag.GetOptional<TimeSpan>("MaxDelay"));
    }

    /// <summary>Circuit breaker options: the numbers are the same for both types; only the default predicate differs.</summary>
    private CircuitBreakerStrategy Breaker(bool http, IOperation? options)
    {
        var version = http ? context.ResilienceVersion : context.PollyVersion;
        var bag = new OptionBag()
            .Define("FailureRatio", SettingKind.Double, 0.1, version)
            .Define("MinimumThroughput", SettingKind.Int, 100, version)
            .Define("SamplingDuration", SettingKind.TimeSpan, TimeSpan.FromSeconds(30), version)
            .Define("BreakDuration", SettingKind.TimeSpan, TimeSpan.FromSeconds(5), version)
            .Define("ShouldHandle", SettingKind.Predicate, http ? PredicateKind.DefaultTransient : PredicateKind.ExceptionsOnly, version);
        bag.Read(options, context);

        return new CircuitBreakerStrategy(
            bag.Get<double>("FailureRatio"),
            bag.Get<int>("MinimumThroughput"),
            bag.Get<TimeSpan>("SamplingDuration"),
            bag.Get<TimeSpan>("BreakDuration"),
            bag.Get<PredicateKind>("ShouldHandle"));
    }

    /// <summary>Sets an int option from an argument written in the call (not one filled in from a default value).</summary>
    private void Set(OptionBag bag, string name, string parameterName, IInvocationOperation call)
    {
        var argument = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName && a.ArgumentKind != ArgumentKind.DefaultValue)?.Value;
        if (argument is not null
            && context.Evaluate(argument, SettingKind.Int, bag.Current(name) ?? 0, context.LocationOf(argument.Syntax)) is { } value)
        {
            bag.Override(name, value);
        }
    }

    private static bool IsType(IOperation? options, string typeName) =>
        Unwrap(options)?.Type?.OriginalDefinition.ToDisplayString() == typeName;
}
