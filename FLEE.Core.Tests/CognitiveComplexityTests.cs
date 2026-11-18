using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SonarAnalyzer.CSharp.Metrics;
using Xunit;

namespace FLEE.Core.Tests;

public sealed class CognitiveComplexityTests(ITestOutputHelper output)
{
    private const int MaximumComplexity = 15;

    [Theory]
    [InlineData("CoreSource")]
    [InlineData("AppSource")]
    public void MethodsStayWithinComplexityBudget(string sourceFolder)
    {
        var source = Path.Combine(AppContext.BaseDirectory, sourceFolder);
        var scores = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .SelectMany(MeasureFile).OrderByDescending(score => score.Complexity).ToArray();
        Assert.NotEmpty(scores);
        output.WriteLine($"Measured {scores.Length} members; maximum complexity: {scores[0].Complexity}; limit: {MaximumComplexity}.");
        foreach (var score in scores.Where(score => score.Complexity > 0))
            output.WriteLine($"{score.File}:{score.Line} {score.Member}: {score.Complexity}");
        var violations = scores.Where(score => score.Complexity > MaximumComplexity).ToArray();
        Assert.True(violations.Length == 0, $"Cognitive complexity must not exceed {MaximumComplexity}:\n" +
            string.Join("\n", violations.Select(score => $"{score.File}:{score.Line} {score.Member}: {score.Complexity}")));
    }

    private static IEnumerable<MemberScore> MeasureFile(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new(LanguageVersion.Preview), path);
        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is not (BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                or PropertyDeclarationSyntax { ExpressionBody: not null } or IndexerDeclarationSyntax { ExpressionBody: not null })) continue;
            var metricNode = node is PropertyDeclarationSyntax property ? property.ExpressionBody! : node;
            var type = node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.Text;
            var name = node switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
                LocalFunctionStatementSyntax local => local.Identifier.Text,
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.Identifier.Text,
                AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
                _ => node.Kind().ToString()
            };
            yield return new(Path.GetFileName(path), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                $"{type}.{name}", CSharpCognitiveComplexityMetric.GetComplexity(metricNode).Complexity);
        }
    }

    private sealed record MemberScore(string File, int Line, string Member, int Complexity);
}
