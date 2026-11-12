using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetryScope.Extraction.Tests;

/// <summary>
/// Calls are matched by the method the compiler resolved, not by the text of the call. These tests compile a snippet
/// against the real library assemblies this test project references.
/// </summary>
public class KnownApiMatchingTests
{
    private const string Snippet = """
        using Microsoft.Extensions.DependencyInjection;

        static class Lookalike
        {
            // Same name and shape as the library method, but a different method.
            public static IHttpClientBuilder AddStandardResilienceHandler(this IHttpClientBuilder b, int marker) => b;
        }

        static class Registrations
        {
            static void Real(IServiceCollection services) =>
                services.AddHttpClient("a").AddStandardResilienceHandler();

            static void Fake(IServiceCollection services) =>
                services.AddHttpClient("b").AddStandardResilienceHandler(42);
        }
        """;

    [Fact]
    public void Only_the_library_method_matches_not_a_lookalike_with_the_same_name()
    {
        var (compilation, tree) = Snippets.Compile(Snippet);
        var model = compilation.GetSemanticModel(tree);

        var calls = tree.GetRoot(Snippets.Ct).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => (Text: i.ToString(), Method: model.GetSymbolInfo(i, Snippets.Ct).Symbol as IMethodSymbol))
            .Where(c => c.Text.EndsWith(".AddStandardResilienceHandler()", StringComparison.Ordinal)
                     || c.Text.EndsWith(".AddStandardResilienceHandler(42)", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, calls.Count);
        Assert.True(KnownApis.AddStandardResilienceHandler.Matches(calls.Single(c => c.Text.EndsWith("()", StringComparison.Ordinal)).Method));
        Assert.False(KnownApis.AddStandardResilienceHandler.Matches(calls.Single(c => c.Text.EndsWith("(42)", StringComparison.Ordinal)).Method));
    }

    [Fact]
    public void AddHttpClient_matches_through_the_reduced_extension_call()
    {
        var (compilation, tree) = Snippets.Compile(Snippet);
        var model = compilation.GetSemanticModel(tree);

        var addHttpClient = tree.GetRoot(Snippets.Ct).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(i => i.ToString() == "services.AddHttpClient(\"a\")");

        Assert.True(KnownApis.AddHttpClient.Matches(model.GetSymbolInfo(addHttpClient, Snippets.Ct).Symbol as IMethodSymbol));
    }
}
