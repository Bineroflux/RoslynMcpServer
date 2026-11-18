using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_base_class picks members like pull_members_up: field-like events, indexers and one
/// variable of several used to be "not found", <c>private protected</c> became
/// <c>protected protected</c>, and members using what stayed behind were moved anyway.
/// </summary>
public sealed class ExtractBaseClassSelectionTests
{
    [Fact]
    public async Task FieldLikeEventsIndexersAndOneOfSeveralVariables_AreExtracted()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Lf("""
            using System;

            namespace App;

            public class Widget
            {
                public int Width = 1, Height = 2;
                public event EventHandler? Clicked;
                public int this[int index] => index;
            }
            """)));

        var result = await ExtractAsync(ws, ["Height", "Clicked", "this"]);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Widget.cs");
        Assert.Contains("public class WidgetBase\n{\n    public int Height = 2;\n    public event EventHandler? Clicked;\n    public int this[int index] => index;\n}", text);
        Assert.Contains("public class Widget : WidgetBase\n{\n    public int Width = 1;\n}", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task PrivateProtectedStays_PrivateBecomesProtected()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Lf("""
            namespace App;

            public class Widget
            {
                private protected int Shared() => 1;
                private int Own() => 2;
                public int Use() => Shared() + Own();
            }
            """)));

        var result = await ExtractAsync(ws, ["Shared", "Own"]);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Widget.cs");
        Assert.Contains("    private protected int Shared() => 1;\n", text);
        Assert.Contains("    protected int Own() => 2;\n", text);
        Assert.DoesNotContain("protected protected", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task AMemberUsingAPrivateMemberLeftBehind_IsRefused()
    {
        var source = Lf("""
            namespace App;

            public class Widget
            {
                private int _count;
                public int Next() => ++_count;
            }
            """);
        using var ws = TestWorkspace.Create(("Widget.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, ["Next"]));

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
        Assert.Contains("'App.Widget._count'", ex.Message);
        Assert.Equal(source, ws.ReadFile("Widget.cs"));
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> ExtractAsync(TestWorkspace ws, string[] members) =>
        new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams { SourceFile = ws.PathOf("Widget.cs"), TypeName = "Widget", BaseClassName = "WidgetBase", Members = members },
            TestContext.Current.CancellationToken);
}
