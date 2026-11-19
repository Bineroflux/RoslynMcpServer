using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Where <see cref="UsingDirectives"/> puts the plain, static and alias directives it adds.
/// </summary>
public sealed class UsingDirectivesTests
{
    private static readonly UsingDirectives.NewUsing[] Added =
    [
        new("Lib"),
        new("System.Text"),
        new("System.Math", Static: true),
        new("Lib.Units", Static: true),
        new("System.Text.StringBuilder", Alias: "Sb"),
        new("System.Collections.Generic", Alias: "Col")
    ];

    [Theory]
    [InlineData(
        "namespace App;\n",
        "using System.Text;\nusing Lib;\nusing static System.Math;\nusing static Lib.Units;\nusing Col = System.Collections.Generic;\nusing Sb = System.Text.StringBuilder;\n\nnamespace App;\n")]
    [InlineData(
        "// header\nusing static App.Z;\nusing Zed = App.Z;\n\nnamespace App;\n",
        "// header\nusing System.Text;\nusing Lib;\nusing static System.Math;\nusing static App.Z;\nusing static Lib.Units;\nusing Col = System.Collections.Generic;\nusing Sb = System.Text.StringBuilder;\nusing Zed = App.Z;\n\nnamespace App;\n")]
    [InlineData(
        "global using System;\nusing Sb = System.Text.StringBuilder;\nusing static System.Math;\nusing System.Text;\n\nnamespace App;\n",
        "global using System;\nusing Col = System.Collections.Generic;\nusing Sb = System.Text.StringBuilder;\nusing static System.Math;\nusing static Lib.Units;\nusing System.Text;\nusing Lib;\n\nnamespace App;\n")]
    public void PlainStaticAndAliasDirectives_GoIntoTheirGroups_InOrder_ThoseThereAlreadyAreSkipped(string source, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken).GetRoot(cancellationToken);

        var result = UsingDirectives.Add(root, Added, "\n");

        Assert.Equal(expected, result.ToFullString());
    }

    [Fact]
    public void InAFileWithItsUsingsInANamespace_TheyGoThereIndentedLikeTheOthers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            "namespace App\n{\n    using System;\n\n    class C { }\n}\n", cancellationToken: cancellationToken).GetRoot(cancellationToken);

        UsingDirectives.NewUsing[] added = [new("System.Text"), new("System.Math", Static: true)];

        var result = UsingDirectives.Add(root, added, "\n");

        Assert.Equal(
            "namespace App\n{\n    using System;\n    using System.Text;\n    using static System.Math;\n\n    class C { }\n}\n",
            result.ToFullString());
    }
}
