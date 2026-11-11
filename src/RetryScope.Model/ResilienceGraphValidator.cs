namespace RetryScope.Model;

/// <summary>A structural or semantic problem in a <see cref="ResilienceGraph"/>.</summary>
/// <param name="Code">Short stable code, e.g. <c>UnknownOwner</c>.</param>
/// <param name="Message">Explanation.</param>
/// <param name="Subject">The id of the element concerned.</param>
public sealed record ValidationIssue(string Code, string Message, string? Subject = null);

/// <summary>
/// Checks graph integrity, plus the two startup rules the standard resilience handler enforces
/// (dotnet/extensions <c>HttpStandardResilienceOptionsCustomValidator</c>, verified 11 Nov 2026):
/// attempt timeout must not exceed total timeout, and the breaker sampling duration must be at least twice
/// the attempt timeout. A configuration breaking these rules would fail at startup, so it must be reported,
/// not simulated.
/// </summary>
public static class ResilienceGraphValidator
{
    /// <summary>Multiplier from <c>HttpStandardResilienceOptionsCustomValidator.CircuitBreakerTimeoutMultiplier</c>.</summary>
    public const int CircuitBreakerTimeoutMultiplier = 2;

    /// <summary>Returns every issue found; an empty list means the graph is valid.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(ResilienceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var issues = new List<ValidationIssue>();

        if (graph.SchemaVersion != ResilienceGraph.CurrentSchemaVersion)
        {
            issues.Add(new("SchemaVersion", $"Expected schema {ResilienceGraph.CurrentSchemaVersion} but found {graph.SchemaVersion}."));
        }

        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in graph.Services)
        {
            if (!serviceIds.Add(service.Id))
            {
                issues.Add(new("DuplicateService", "Service id appears more than once.", service.Id));
            }
        }

        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in graph.Bindings)
        {
            if (!bindingIds.Add(binding.Id))
            {
                issues.Add(new("DuplicateBinding", "Binding id appears more than once.", binding.Id));
            }

            if (!serviceIds.Contains(binding.OwnerService))
            {
                issues.Add(new("UnknownOwner", $"Owner service '{binding.OwnerService}' is not in the graph.", binding.Id));
            }

            if (binding.TargetService is { } target && !serviceIds.Contains(target))
            {
                issues.Add(new("UnknownTarget", $"Target service '{target}' is not in the graph.", binding.Id));
            }

            foreach (var handler in binding.Handlers.Where(h => h.Kind == HandlerKind.Standard))
            {
                ValidateStandardHandler(binding, handler, issues);
            }
        }

        foreach (var edge in graph.Edges)
        {
            var subject = $"{edge.From}->{edge.To}";
            if (!serviceIds.Contains(edge.From) || !serviceIds.Contains(edge.To))
            {
                issues.Add(new("UnknownEdgeEndpoint", "Edge refers to a service that is not in the graph.", subject));
            }

            if (edge.CallsPerRequest <= 0 || double.IsNaN(edge.CallsPerRequest))
            {
                issues.Add(new("InvalidCallsPerRequest", "CallsPerRequest must be a positive number.", subject));
            }
        }

        return issues;
    }

    private static void ValidateStandardHandler(ClientBinding binding, Handler handler, List<ValidationIssue> issues)
    {
        var attempt = handler.Strategies.OfType<TimeoutStrategy>().FirstOrDefault(t => t.Role == TimeoutRole.Attempt);
        var total = handler.Strategies.OfType<TimeoutStrategy>().FirstOrDefault(t => t.Role == TimeoutRole.Total);
        var breaker = handler.Strategies.OfType<CircuitBreakerStrategy>().FirstOrDefault();

        if (attempt is not null && total is not null && attempt.Timeout.Effective > total.Timeout.Effective)
        {
            issues.Add(new(
                DiagnosticCodes.InvalidStandardOptions,
                $"Attempt timeout {attempt.Timeout.Effective} exceeds total timeout {total.Timeout.Effective}; the standard handler rejects this at startup.",
                binding.Id));
        }

        if (attempt is not null && breaker is not null
            && breaker.SamplingDuration.Effective < attempt.Timeout.Effective * CircuitBreakerTimeoutMultiplier)
        {
            issues.Add(new(
                DiagnosticCodes.InvalidStandardOptions,
                $"Circuit-breaker sampling duration {breaker.SamplingDuration.Effective} is less than {CircuitBreakerTimeoutMultiplier}x the attempt timeout {attempt.Timeout.Effective}; the standard handler rejects this at startup.",
                binding.Id));
        }
    }
}
