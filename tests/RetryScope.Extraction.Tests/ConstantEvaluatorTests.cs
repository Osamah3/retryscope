using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetryScope.Model;

namespace RetryScope.Extraction.Tests;

/// <summary>Option values are only accepted when the compiler can prove them constant; everything else is unresolved.</summary>
public class ConstantEvaluatorTests
{
    private const string Snippet = """
        using System;
        using System.Threading;
        using Polly;

        static class Values
        {
            const int Retries = 4;
            static int notConstant = 7;

            static object[] All =
            [
                TimeSpan.FromSeconds(5),        // 0: FromSeconds(long)
                TimeSpan.FromMilliseconds(500), // 1
                TimeSpan.FromSeconds(1.5),      // 2: FromSeconds(double value)
                new TimeSpan(0, 0, 3),          // 3
                TimeSpan.Zero,                  // 4
                TimeSpan.FromMinutes(1, 30),    // 5: minutes + seconds
                Timeout.InfiniteTimeSpan,       // 6
                TimeSpan.FromSeconds(notConstant), // 7: not constant
                Retries,                        // 8: const field
                notConstant,                    // 9: not constant
                DelayBackoffType.Linear,        // 10
                0.25,                           // 11
            ];
        }
        """;

    [Fact]
    public void TimeSpan_forms_with_constant_arguments_evaluate()
    {
        var items = Items();

        Assert.Equal(TimeSpan.FromSeconds(5), TimeSpanOf(items[0]));
        Assert.Equal(TimeSpan.FromMilliseconds(500), TimeSpanOf(items[1]));
        Assert.Equal(TimeSpan.FromSeconds(1.5), TimeSpanOf(items[2]));
        Assert.Equal(TimeSpan.FromSeconds(3), TimeSpanOf(items[3]));
        Assert.Equal(TimeSpan.Zero, TimeSpanOf(items[4]));
        Assert.Equal(TimeSpan.FromSeconds(90), TimeSpanOf(items[5]));
        Assert.Equal(Timeout.InfiniteTimeSpan, TimeSpanOf(items[6]));
    }

    [Fact]
    public void Non_constant_values_are_not_evaluated()
    {
        var items = Items();

        Assert.False(ConstantEvaluator.TryTimeSpan(items[7], out _));
        Assert.False(ConstantEvaluator.TryInt(items[9], out _));
    }

    [Fact]
    public void Const_fields_enums_and_doubles_evaluate()
    {
        var items = Items();

        Assert.True(ConstantEvaluator.TryInt(items[8], out var retries));
        Assert.Equal(4, retries);
        Assert.True(ConstantEvaluator.TryBackoff(items[10], out var backoff));
        Assert.Equal(BackoffType.Linear, backoff);
        Assert.True(ConstantEvaluator.TryDouble(items[11], out var ratio));
        Assert.Equal(0.25, ratio);
    }

    private static TimeSpan? TimeSpanOf(IOperation operation) =>
        ConstantEvaluator.TryTimeSpan(operation, out var value) ? value : null;

    /// <summary>The operations for each element of the array, in order (conversions to object unwrapped).</summary>
    private static List<IOperation> Items()
    {
        var (compilation, tree) = Snippets.Compile(Snippet);
        var model = compilation.GetSemanticModel(tree);
        var collection = tree.GetRoot(Snippets.Ct).DescendantNodes().OfType<CollectionExpressionSyntax>().Single();

        return collection.Elements
            .OfType<ExpressionElementSyntax>()
            .Select(e => model.GetOperation(e.Expression, Snippets.Ct)!)
            .ToList();
    }
}
