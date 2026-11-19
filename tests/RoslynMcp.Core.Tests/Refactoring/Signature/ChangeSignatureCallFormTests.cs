using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Calls change_signature used to leave alone (their errors only showed up in warnings): an
/// extension method called on a receiver whose <c>this</c> parameter moves or goes, and
/// expanded <c>params</c> arguments that would need a name.
/// </summary>
public sealed class ChangeSignatureCallFormTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Pads = """
        namespace App;

        public static class Text
        {
            public static string Pad(this string text, int width, char fill) => text.PadLeft(width, fill);

            public static string Tag(this object value, string label) => label;
        }

        public static class Use
        {
            public static string Run(string s) => s.Pad(5, '*') + Text.Pad("x", 2, '-') + "y".Pad(width: 3, fill: '+') + s.Tag("t");
        }
        """;

    [Fact]
    public async Task ExtensionCalls_TakeTheStaticForm_WhenThisMoves()
    {
        using var ws = TestWorkspace.Create(("Text.cs", Pads));

        var result = await ChangeAsync(ws, "Text.cs", "Pad", new ParameterChange { OriginalName = "text", NewPosition = 2 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Text.cs");
        Assert.Contains("public static string Pad(int width, char fill, string text) => text.PadLeft(width, fill);", text);
        Assert.Contains("=> Text.Pad(5, '*', s) + Text.Pad(2, '-', \"x\") + Text.Pad(text: \"y\", width: 3, fill: '+') + s.Tag(\"t\");", text);
        Assert.Equal(3, result.ReferencesUpdated);
        var note = Assert.Single(result.Warnings!);
        Assert.Contains("is no longer an extension method: its 'this' parameter 'text' is no longer first", note);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtensionCalls_DropTheReceiver_WhenThisIsRemoved()
    {
        using var ws = TestWorkspace.Create(("Text.cs", Pads));

        var result = await ChangeAsync(ws, "Text.cs", "Tag", new ParameterChange { OriginalName = "value", Remove = true });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Text.cs");
        Assert.Contains("public static string Tag(string label) => label;", text);
        Assert.Contains("+ Text.Tag(\"t\");", text);
        Assert.Contains(result.Warnings!, w => w.Contains("its 'this' parameter 'value' is removed", StringComparison.Ordinal));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ConditionalExtensionCall_IsLeftAlone_AndItsErrorReported()
    {
        const string source = """
            namespace App;

            public static class Text
            {
                public static int Size(this string text, int extra) => text.Length + extra;

                public static int? Run(string? s) => s?.Size(1);
            }
            """;
        using var ws = TestWorkspace.Create(("Text.cs", source));

        var result = await ChangeAsync(ws, "Text.cs", "Size", new ParameterChange { OriginalName = "text", NewPosition = 1 });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int? Run(string? s) => s?.Size(1);", ws.ReadFile("Text.cs"));
        Assert.Contains(result.Warnings!, w => w.Contains("error CS1061", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Theory]
    [InlineData(LanguageVersion.Latest, "parts: [\"a\", \"b\"]")]
    [InlineData(LanguageVersion.CSharp11, "parts: new string[] { \"a\", \"b\" }")]
    public async Task ExpandedParamsArguments_ThatNeedAName_ArePassedAsOneCollection(LanguageVersion version, string collection)
    {
        const string source = """
            namespace App;

            public static class Fmt
            {
                public static string Join(string separator, bool trim, params string[] parts) => string.Join(separator, parts);

                public static string Use() => Join(separator: ",", trim: true, "a", "b") + Join(";", false, "c", "d");
            }
            """;
        using var ws = TestWorkspace.Create([("Fmt.cs", source)], [], languageVersion: version);

        var result = await ChangeAsync(ws, "Fmt.cs", "Join", new ParameterChange { OriginalName = "trim", NewPosition = 0 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Fmt.cs");
        Assert.Contains("public static string Join(bool trim, string separator, params string[] parts)", text);
        Assert.Contains($"=> Join(separator: \",\", trim: true, {collection}) + Join(false, \";\", \"c\", \"d\");", text);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task NamedArgumentOfARemovedParameter_IsRemovedWithIt()
    {
        const string source = """
            namespace App;

            public static class Calc
            {
                public static int Sum(int a, int b, int c = 0) => a + c;

                public static int Use() => Sum(c: 3, b: 2, a: 1) + Sum(1, b: 2);
            }
            """;
        using var ws = TestWorkspace.Create(("Calc.cs", source));

        var result = await ChangeAsync(ws, "Calc.cs", "Sum", new ParameterChange { OriginalName = "b", Remove = true });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Use() => Sum(c: 3, a: 1) + Sum(1);", ws.ReadFile("Calc.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static Task<RefactoringResult> ChangeAsync(TestWorkspace ws, string file, string method, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams { SourceFile = ws.PathOf(file), MethodName = method, Parameters = parameters },
            Ct);
}
