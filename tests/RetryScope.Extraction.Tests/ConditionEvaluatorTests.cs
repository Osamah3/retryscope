using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetryScope.Extraction.Tests;

/// <summary>Only host-environment conditions are decided; anything else stays undecided (null), never guessed.</summary>
public class ConditionEvaluatorTests
{
    private const string Snippet = """
        using Microsoft.Extensions.Hosting;

        static class Conditions
        {
            static bool[] All(IHostEnvironment env, bool other) =>
            [
                env.IsProduction(),                  // 0
                env.IsDevelopment(),                 // 1
                !env.IsDevelopment(),                // 2
                env.IsEnvironment("production"),     // 3: case-insensitive
                env.EnvironmentName == "Production", // 4
                other,                               // 5: unknown
                other && env.IsDevelopment(),        // 6: unknown && false
                other || env.IsProduction(),         // 7: unknown || true
                other && env.IsProduction(),         // 8: unknown && true
            ];
        }
        """;

    [Fact]
    public void Environment_checks_are_decided_for_production()
    {
        var results = Evaluate("Production");

        Assert.Equal(
            new bool?[] { true, false, true, true, true, null, false, true, null },
            results);
    }

    [Fact]
    public void The_same_checks_flip_for_development()
    {
        var results = Evaluate("Development");

        Assert.Equal(
            new bool?[] { false, true, false, false, false, null, null, null, false },
            results);
    }

    private static bool?[] Evaluate(string environment)
    {
        var (compilation, tree) = Snippets.Compile(Snippet);
        var model = compilation.GetSemanticModel(tree);
        var collection = tree.GetRoot(Snippets.Ct).DescendantNodes().OfType<CollectionExpressionSyntax>().Single();

        return collection.Elements
            .OfType<ExpressionElementSyntax>()
            .Select(e => ConditionEvaluator.Evaluate(model.GetOperation(e.Expression, Snippets.Ct), environment))
            .ToArray();
    }
}
