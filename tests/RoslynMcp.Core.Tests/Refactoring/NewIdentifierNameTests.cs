using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// The names the tools are asked to declare are checked by one rule (<c>IdentifierNames</c>):
/// any C# identifier, non-ASCII ones included; a reserved keyword only verbatim (<c>@class</c>);
/// contextual keywords where they compile. rename_symbol used to refuse <c>Größe</c> (its
/// pattern was ASCII-only) and extract_variable / extract_constant accepted <c>class</c> and
/// <c>int</c>, writing code that did not compile.
/// </summary>
public sealed class NewIdentifierNameTests
{
    private const string FileName = "Widget.cs";

    private const string Source = """
        namespace App;

        public class Widget
        {
            private int count;

            public int Total(int factor)
            {
                var sum = count * 42;
                return sum + factor;
            }

            public async System.Threading.Tasks.Task<int> LoadAsync()
            {
                await System.Threading.Tasks.Task.Yield();
                return count + 7;
            }
        }

        public class User
        {
            public int Use(Widget w) => w.Total(2);
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region rename_symbol

    [Theory]
    [InlineData("Total", "Größe", "w.Größe(2)")]
    [InlineData("Total", "Λ", "w.Λ(2)")]
    [InlineData("Total", "@class", "w.@class(2)")]
    [InlineData("Total", "async", "w.async(2)")]
    [InlineData("Widget", "Größe", "Use(Größe w)")]
    [InlineData("Widget", "@class", "Use(@class w)")]
    [InlineData("Total", "@value", "w.value(2)")]
    public async Task RenameSymbol_AcceptsEveryIdentifierThatCompiles(string symbol, string newName, string expected)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await RenameAsync(ws, symbol, newName);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(expected, ReadAll(ws));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task RenameSymbol_TypeToVerbatimName_NamesTheFileWithoutTheAt()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await RenameAsync(ws, "Widget", "@class");

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(File.Exists(ws.PathOf("class.cs")));
        Assert.False(File.Exists(ws.PathOf(FileName)));
        Assert.Contains("public class @class", ws.ReadFile("class.cs"));
    }

    [Theory]
    [InlineData("Total", "class", ErrorCodes.ReservedKeyword)]
    [InlineData("Total", "1st", ErrorCodes.InvalidNewName)]
    [InlineData("Total", "a-b", ErrorCodes.InvalidNewName)]
    [InlineData("Widget", "required", ErrorCodes.ReservedKeyword)]
    [InlineData("Widget", "record", ErrorCodes.ReservedKeyword)]
    [InlineData("Widget", "@required", ErrorCodes.ReservedKeyword)]
    [InlineData("Total", "@Total", ErrorCodes.SameLocation)]
    public async Task RenameSymbol_RefusesNamesThatDoNotCompile(string symbol, string newName, string code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, symbol, newName));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Fact]
    public async Task RenameSymbol_PlainKeyword_SuggestsTheVerbatimName()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, "Total", "class"));

        Assert.Equal("newName 'class' is a C# keyword; write '@class' to use it as a name.", ex.Message);
    }

    #endregion

    #region extract_variable / extract_constant / extract_method

    [Theory]
    [InlineData("Größe", "var Größe = count * 42;")]
    [InlineData("@class", "var @class = count * 42;")]
    [InlineData("await", "var await = count * 42;")]
    [InlineData("nameof", "var nameof = count * 42;")]
    public async Task ExtractVariable_AcceptsEveryIdentifierThatCompiles(string name, string expected)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("count * 42");

        var result = await ExtractVariableAsync(ws, line, start, end, name);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(expected, ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("class", "count * 42")]
    [InlineData("int", "count * 42")]
    [InlineData("await", "count + 7")]
    public async Task ExtractVariable_RefusesKeywords(string name, string expression)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate(expression);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractVariableAsync(ws, line, start, end, name));

        Assert.Equal(ErrorCodes.ReservedKeyword, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Fact]
    public async Task ExtractVariable_VerbatimAwaitInAsyncMethod_Compiles()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("count + 7");

        var result = await ExtractVariableAsync(ws, line, start, end, "@await");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("return @await;", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("Größe", "private const int Größe = 42;")]
    [InlineData("@int", "private const int @int = 42;")]
    public async Task ExtractConstant_AcceptsEveryIdentifierThatCompiles(string name, string expected)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("42");

        var result = await new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                ConstantName = name
            },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(expected, ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("int", ErrorCodes.ReservedKeyword)]
    [InlineData("null", ErrorCodes.ReservedKeyword)]
    [InlineData("4you", ErrorCodes.InvalidSymbolName)]
    public async Task ExtractConstant_RefusesNamesThatDoNotCompile(string name, string code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                ConstantName = name
            },
            Ct));

        Assert.Equal(code, ex.ErrorCode);
    }

    [Theory]
    [InlineData(6, "Größe", null)]
    [InlineData(6, "@this", null)]
    [InlineData(6, "this", ErrorCodes.ReservedKeyword)]
    [InlineData(6, "await", null)]
    [InlineData(12, "await", ErrorCodes.ReservedKeyword)]
    public async Task ExtractMethod_ChecksTheName(int line, string name, string? code)
    {
        const string logger = """
            namespace App;

            public static class Logger
            {
                public static void Log(int value)
                {
                    System.Console.WriteLine(value);
                }

                public static async System.Threading.Tasks.Task LogAsync(int value)
                {
                    await System.Threading.Tasks.Task.Yield();
                    System.Console.WriteLine(value + 1);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Logger.cs", logger));
        var statement = logger.ReplaceLineEndings("\n").Split('\n')[line];

        var run = () => new ExtractMethodOperation(ws.Context).ExecuteAsync(
            new ExtractMethodParams
            {
                SourceFile = ws.PathOf("Logger.cs"),
                StartLine = line + 1,
                StartColumn = statement.IndexOf('S') + 1,
                EndLine = line + 1,
                EndColumn = statement.Length + 1,
                MethodName = name
            },
            Ct);

        if (code != null)
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<RefactoringException>(run)).ErrorCode);
            return;
        }

        var result = await run();
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains($"private static void {name}(int value)", ws.ReadFile("Logger.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    #endregion

    #region types, properties, parameters

    [Theory]
    [InlineData("IGröße", null)]
    [InlineData("@record", null)]
    [InlineData("record", ErrorCodes.ReservedKeyword)]
    [InlineData("interface", ErrorCodes.ReservedKeyword)]
    [InlineData("I.Widget", ErrorCodes.InvalidSymbolName)]
    public async Task ExtractInterface_ChecksTheName(string name, string? code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var run = () => new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams { SourceFile = ws.PathOf(FileName), TypeName = "Widget", InterfaceName = name },
            Ct);

        if (code != null)
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<RefactoringException>(run)).ErrorCode);
            return;
        }

        var result = await run();
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains($"public interface {name}", ws.ReadFile(FileName));
        Assert.Contains($"public class Widget : {name}", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("Grundgröße", null)]
    [InlineData("@file", null)]
    [InlineData("file", ErrorCodes.ReservedKeyword)]
    [InlineData("scoped", ErrorCodes.ReservedKeyword)]
    public async Task ExtractBaseClass_ChecksTheName(string name, string? code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var run = () => new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams { SourceFile = ws.PathOf(FileName), TypeName = "Widget", BaseClassName = name, Members = ["Total", "count"] },
            Ct);

        if (code != null)
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<RefactoringException>(run)).ErrorCode);
            return;
        }

        var result = await run();
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains($"public class {name}", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("Größe", null)]
    [InlineData("@int", null)]
    [InlineData("int", ErrorCodes.ReservedKeyword)]
    public async Task EncapsulateField_ChecksThePropertyName(string name, string? code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var run = () => new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
            new EncapsulateFieldParams { SourceFile = ws.PathOf(FileName), FieldName = "count", PropertyName = name },
            Ct);

        if (code != null)
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<RefactoringException>(run)).ErrorCode);
            return;
        }

        var result = await run();
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains($"public int {name}", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ChangeSignature_VerbatimAndNonAsciiParameterNames_Compile()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ChangeSignatureAsync(
            ws,
            new ParameterChange { OriginalName = "factor", Name = "Faktör" },
            new ParameterChange { Name = "@event", Type = "int", DefaultValue = "0" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains("public int Total(int Faktör, int @event = 0)", text);
        Assert.Contains("return sum + Faktör;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("event", ErrorCodes.ReservedKeyword)]
    [InlineData("two words", ErrorCodes.InvalidNewName)]
    public async Task ChangeSignature_RefusesParameterNamesThatDoNotCompile(string name, string code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeSignatureAsync(
            ws,
            new ParameterChange { OriginalName = "factor", Name = "factor" },
            new ParameterChange { Name = name, Type = "int", DefaultValue = "0" }));

        Assert.Equal(code, ex.ErrorCode);
        Assert.StartsWith("parameters[1].name", ex.Message);
    }

    #endregion

    #region namespaces

    [Fact]
    public async Task RenameNamespace_AcceptsNonAsciiSegments()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await new RenameNamespaceOperation(ws.Context).ExecuteAsync(
            new RenameNamespaceParams { OldNamespace = "App", NewNamespace = "Äpp.Größen" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith("namespace Äpp.Größen;", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("App.class", ErrorCodes.ReservedKeyword)]
    [InlineData("App.@class", ErrorCodes.InvalidNamespace)]
    [InlineData("App..Core", ErrorCodes.InvalidNamespace)]
    public async Task Namespaces_RefuseKeywordsAndInvalidSegments(string name, string code)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var rename = await Assert.ThrowsAsync<RefactoringException>(() => new RenameNamespaceOperation(ws.Context).ExecuteAsync(
            new RenameNamespaceParams { OldNamespace = "App", NewNamespace = name },
            Ct));
        var move = await Assert.ThrowsAsync<RefactoringException>(() => new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf(FileName), SymbolName = "User", TargetNamespace = name },
            Ct));

        Assert.Equal(code, rename.ErrorCode);
        Assert.Equal(code, move.ErrorCode);
    }

    [Fact]
    public async Task MoveTypeToNamespace_AcceptsNonAsciiSegments()
    {
        using var ws = TestWorkspace.Create((FileName, Source), ("Gadget.cs", "namespace App;\n\npublic class Gadget\n{\n}\n"));

        var result = await new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Gadget.cs"), SymbolName = "Gadget", TargetNamespace = "App.Größen" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("namespace App.Größen", ws.ReadFile("Gadget.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    #endregion

    private static string[] Lines => Source.ReplaceLineEndings("\n").Split('\n');

    private static (int Line, int Start, int End) Locate(string token)
    {
        var match = Assert.Single(
            Lines.Select((text, index) => (Text: text, Line: index + 1)),
            l => l.Text.Contains(token, StringComparison.Ordinal));
        var column = match.Text.IndexOf(token, StringComparison.Ordinal) + 1;
        return (match.Line, column, column + token.Length);
    }

    private static string ReadAll(TestWorkspace ws) =>
        string.Concat(Directory.GetFiles(ws.Directory, "*.cs").Order().Select(File.ReadAllText));

    private static Task<RefactoringResult> RenameAsync(TestWorkspace ws, string symbol, string newName) =>
        new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf(FileName), SymbolName = symbol, NewName = newName },
            Ct);

    private static Task<RefactoringResult> ExtractVariableAsync(TestWorkspace ws, int line, int start, int end, string name) =>
        new ExtractVariableOperation(ws.Context).ExecuteAsync(
            new ExtractVariableParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                VariableName = name
            },
            Ct);

    private static Task<RefactoringResult> ChangeSignatureAsync(TestWorkspace ws, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams { SourceFile = ws.PathOf(FileName), MethodName = "Total", Parameters = parameters },
            Ct);
}
