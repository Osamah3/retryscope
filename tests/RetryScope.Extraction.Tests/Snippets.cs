using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RetryScope.Extraction.Tests;

/// <summary>Compiles C# snippets against the real assemblies this test process loads (BCL + Microsoft.Extensions.*).</summary>
internal static class Snippets
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static (CSharpCompilation Compilation, SyntaxTree Tree) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: Ct);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "Snippet",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics(Ct).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        return (compilation, tree);
    }
}
