using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// References in source-generated code (a Razor component's, here <see cref="ViewGenerator"/>'s)
/// cannot be edited: the operations leave them and say where to change them by hand, at the
/// <c>.razor</c> line the generated code's <c>#line</c> directive maps them to. change_signature
/// used to fail with a NullReferenceException on such a call.
/// </summary>
public sealed class GeneratedReferenceTests
{
    private const string Api = """
        namespace App;

        public static class Api
        {
            public static int Count;

            internal static int _total;

            public static int Total { get => _total; set => _total = value; }

            public static string Format(int count, string unit) => count + unit;

            public static string Use() => Format(1, "a") + Twice(2);

            public static int Twice(int value) => value * 2;

            public static int Load()
            {
                System.Threading.Tasks.Task.Delay(1).Wait();
                return 1;
            }

            public static int Scaled()
            {
                int factor = 2;
                return factor * 3;
            }

            public static int Unused() => 0;
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangeSignature_LeavesACallInGeneratedCode_AndReportsItsRazorLine()
    {
        using var ws = Create("""@Api.Format(2, "b")""");

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Api.cs"),
                MethodName = "Format",
                Parameters = [new ParameterChange { OriginalName = "unit", NewPosition = 0 }]
            },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static string Use() => Format(\"a\", 1) + Twice(2);", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, """@Api.Format(2, "b")""");
        Assert.Contains($"{ws.PathOf("Widget.razor")}(2,6): the call to 'App.Api.Format(int, string)' is in code generated from this file and was not rewritten; change it here by hand.", result.Warnings!);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task ChangeReturnType_ReportsTheErrorInGeneratedCode_AtItsRazorLine()
    {
        using var ws = Create("""@Api.Format(2, "b").Length""");

        var result = await new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams { SourceFile = ws.PathOf("Api.cs"), MemberName = "Format", NewType = "int" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Warnings!, w => w.StartsWith(ws.PathOf("Widget.razor") + "(2,", StringComparison.Ordinal) && w.Contains("error CS1061", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task RenameSymbol_LeavesGeneratedCode_AndSaysWhereToRename()
    {
        using var ws = Create("""@Api.Format(2, "b")""");

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Api.cs"), SymbolName = "Format", NewName = "Show" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static string Show(int count, string unit)", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, """@Api.Format(2, "b")""");
        Assert.Contains($"{ws.PathOf("Widget.razor")}(2,6): the reference to 'App.Api.Format(int, string)' is in code generated from this file and was not renamed to 'Show'; change it here by hand.", result.Warnings!);
    }

    [Fact]
    public async Task ConvertToAsync_LeavesACallInGeneratedCode()
    {
        using var ws = Create("@Api.Load()");

        var result = await new ConvertToAsyncOperation(ws.Context).ExecuteAsync(
            new ConvertToAsyncParams { SourceFile = ws.PathOf("Api.cs"), MethodName = "Load" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static async Task<int> LoadAsync()", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, "@Api.Load()");
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): the reference to 'App.Api.Load()' is in code generated from this file and was not renamed to 'LoadAsync' or awaited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntroduceParameter_LeavesACallInGeneratedCode()
    {
        using var ws = Create("@Api.Scaled()");

        var result = await new IntroduceParameterOperation(ws.Context).ExecuteAsync(
            new IntroduceParameterParams { SourceFile = ws.PathOf("Api.cs"), VariableName = "factor", Line = 25 },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Scaled(int factor)", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, "@Api.Scaled()");
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): the use of 'Scaled' is in code generated from this file and was not given the new argument", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task EncapsulateField_LeavesAUseInGeneratedCode()
    {
        using var ws = Create("@Api.Count");

        var result = await new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
            new EncapsulateFieldParams { SourceFile = ws.PathOf("Api.cs"), FieldName = "Count", PropertyName = "Number" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Number", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, "@Api.Count");
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): the use of 'Count' is in code generated from this file and was not changed to 'Number'", StringComparison.Ordinal));
        // The field is private now: the generated use no longer compiles, which is reported too.
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): error CS0122", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task ConvertProperty_LeavesAUseOfTheFieldInGeneratedCode()
    {
        using var ws = Create("@Api._total");

        var result = await new ConvertPropertyOperation(ws.Context).ExecuteAsync(
            new ConvertPropertyParams { SourceFile = ws.PathOf("Api.cs"), PropertyName = "Total", Direction = "ToAutoProperty" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Total { get; set; }", ws.ReadFile("Api.cs"));
        AssertRazorUnchanged(ws, "@Api._total");
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): the use of '_total' is in code generated from this file and was not changed to 'Total'", StringComparison.Ordinal));
        // The field is gone: the generated use no longer compiles, which is reported too.
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(2,6): error CS0117", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task InlineMethod_LeavesACallInGeneratedCode_AndKeepsTheMethod()
    {
        using var ws = Create("@Api.Twice(4)");

        var result = await new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf("Api.cs"), MethodName = "Twice" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Api.cs");
        Assert.Contains("public static string Use() => Format(1, \"a\") + 2 * 2;", text);
        Assert.Contains("public static int Twice(int value) => value * 2;", text);
        AssertRazorUnchanged(ws, "@Api.Twice(4)");
        Assert.Contains($"{ws.PathOf("Widget.razor")}(2,6): the reference to 'App.Api.Twice(int)' is in code generated from this file and was not inlined; change it here by hand.", result.Warnings!);
    }

    [Fact]
    public async Task SafeDelete_ListsAReferenceInGeneratedCode_AtItsRazorLine()
    {
        using var ws = Create("@Api.Unused()");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new SafeDeleteOperation(ws.Context).ExecuteAsync(
            new SafeDeleteParams { SourceFile = ws.PathOf("Api.cs"), SymbolName = "Unused" },
            Ct));

        Assert.Equal(ErrorCodes.SymbolStillReferenced, ex.ErrorCode);
        Assert.Contains($"(1 in generated code, listed where it comes from, e.g. a .razor file): {ws.PathOf("Widget.razor")}(2,6)", ex.Message);
    }

    private static TestWorkspace Create(string expression) =>
        TestWorkspace.Create([("Api.cs", Api)], [], additionalFiles: [("Widget.razor", View(expression))], generators: [ViewGenerator.Instance]);

    private static string View(string expression) => "<h1>Total</h1>\n" + expression + "\n";

    private static void AssertRazorUnchanged(TestWorkspace ws, string expression) =>
        Assert.Equal(View(expression), ws.ReadFile("Widget.razor"));
}
