using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Code moved from one file into another kept the line endings of the file it came from, so a
/// CRLF member pulled into an LF file (or split off into a new file whose .editorconfig says
/// <c>end_of_line = lf</c>) left the file with mixed line endings. Moved code now takes the line
/// ending of the file it goes into, except inside multi-line string literals, whose line breaks
/// are part of their value; those are kept and reported in <c>warnings</c>.
/// </summary>
public sealed class MovedCodeLineEndingTests
{
    private const string Literal = "@\"Wuff\r\nWuff\"";

    private const string LfEditorConfig = "root = true\n\n[*.cs]\nend_of_line = lf\n";

    // A type written with CRLF, with every kind of line break: code, a documentation comment, a
    // multi-line comment, directives with disabled text, and a verbatim string whose line break
    // is its value.
    private const string Dog =
        "namespace App;\r\n\r\npublic class Dog : Animal\r\n{\r\n" +
        "    /// <summary>\r\n    /// Barks twice.\r\n    /// </summary>\r\n" +
        "    public string Bark() => " + Literal + ";\r\n\r\n" +
        "    /* Four,\r\n       always */\r\n" +
        "    public int Legs\r\n    {\r\n        get\r\n        {\r\n#if NEVER\r\n            return 3;\r\n#endif\r\n            return 4;\r\n        }\r\n    }\r\n}\r\n";

    private static readonly string[] MovedParts = ["/// Barks twice.\n", "/* Four,\n       always */\n", "#if NEVER\n            return 3;\n#endif\n"];

    [Fact]
    public void Convert_WritesEveryLineBreakInTheLineEnding_ExceptInsideStringLiterals()
    {
        string[] literals =
        [
            "@\"x\r\ny\"",
            "\"\"\"\r\n        raw\r\n        \"\"\"",
            "$@\"n\r\n{n}\"",
            "$\"\"\"\r\n        {n}\r\n        \"\"\"",
        ];
        var source =
            "class C\r\n{\r\n    /* a\r\n       b */\r\n    /// <summary>\r\n    /// Doc.\r\n    /// </summary>\r\n#region R\r\n" +
            $"    string V = {literals[0]};\r\n    string R = {literals[1]};\r\n    string I(int n) => {literals[2]};\r\n    string J(int n) => {literals[3]};\r\n" +
            "#endregion\r\n#if NEVER\r\n    disabled\r\n    text\r\n#endif\r\n}\r\n";
        var root = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

        var converted = LineEndings.Convert(root, "\n", out var kept);

        Assert.Equal(4, kept);
        var expected = source;
        for (var i = 0; i < literals.Length; i++)
            expected = expected.Replace(literals[i], $"<{i}>", StringComparison.Ordinal);
        expected = expected.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var i = 0; i < literals.Length; i++)
            expected = expected.Replace($"<{i}>", literals[i], StringComparison.Ordinal);
        Assert.Equal(expected, converted.ToFullString());

        var lf = CSharpSyntaxTree.ParseText("class D\n{\n}\n", cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        Assert.Same(lf, LineEndings.Convert(lf, "\n", out var none));
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task ExtractBaseClass_IntoANewLfFile_WritesTheMovedMembersInLf()
    {
        using var ws = TestWorkspace.Create(
            [("Dog.cs", Dog.Replace(" : Animal", "", StringComparison.Ordinal))], [], editorConfig: LfEditorConfig);

        var result = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                BaseClassName = "Animal",
                Members = ["Bark", "Legs"],
                TargetFile = ws.PathOf("Animal.cs"),
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertLf(ws.ReadFile("Animal.cs"), MovedParts);
        AssertKeptLiteralWarning(result, ws.PathOf("Animal.cs"));
        Assert.DoesNotContain("\n", ws.ReadFile("Dog.cs").Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task PullMembersUp_IntoAnLfFile_WritesTheMovedMembersInLf()
    {
        using var ws = TestWorkspace.Create(
            ("Animal.cs", "namespace App;\n\npublic class Animal\n{\n}\n"),
            ("Dog.cs", Dog));

        var result = await new PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams { SourceFile = ws.PathOf("Dog.cs"), TypeName = "Dog", Members = ["Bark", "Legs"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertLf(ws.ReadFile("Animal.cs"), MovedParts);
        AssertKeptLiteralWarning(result, ws.PathOf("Animal.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task InlineMethod_IntoAnLfFile_WritesTheBodyInLf()
    {
        const string Util =
            "internal static class Util\r\n{\r\n    public static string Twice(string s) =>\r\n        s\r\n        + " + Literal + "\r\n        + s;\r\n}\r\n";
        using var ws = TestWorkspace.Create(
            ("Util.cs", Util),
            ("Program.cs", "internal static class Program\n{\n    public static string Run() => Util.Twice(\"a\");\n}\n"));

        var result = await new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf("Util.cs"), MethodName = "Twice", Line = 3 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var program = ws.ReadFile("Program.cs");
        Assert.Contains("+ \"a\"", program);
        AssertLf(program);
        AssertKeptLiteralWarning(result, ws.PathOf("Program.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MoveTypeToFile_IntoAnExistingLfFile_OrANewOneTheEditorConfigMakesLf_WritesTheTypeInLf()
    {
        const string Models =
            "namespace App;\r\n\r\npublic class User\r\n{\r\n}\r\n\r\n" +
            "public class Address\r\n{\r\n    /* Street\r\n       and city */\r\n    public string Label => " + Literal + ";\r\n}\r\n\r\n" +
            "public class Phone\r\n{\r\n    // Digits\r\n    public string Number => \"0\";\r\n}\r\n";
        using var ws = TestWorkspace.Create(
            [("Models.cs", Models), ("Contacts.cs", "namespace App;\n\npublic class Contacts\n{\n}\n")], [], editorConfig: LfEditorConfig);
        var ct = TestContext.Current.CancellationToken;

        var existing = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Address", TargetFile = ws.PathOf("Contacts.cs") }, ct);
        var created = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Phone", TargetFile = ws.PathOf("Phone.cs") }, ct);

        Assert.True(existing.Success, existing.Error?.Message);
        AssertLf(ws.ReadFile("Contacts.cs"), "/* Street\n       and city */");
        AssertKeptLiteralWarning(existing, ws.PathOf("Contacts.cs"));
        Assert.True(created.Success, created.Error?.Message);
        AssertLf(ws.ReadFile("Phone.cs"), "// Digits");
        Assert.Null(created.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task IntroduceParameter_CallInAnLfFile_GetsTheInitializerInLf()
    {
        const string Calc =
            "public static class Calc\r\n{\r\n    public static int Total(int a)\r\n    {\r\n" +
            "        var factor = System.Math.Max(\r\n            a,\r\n            2);\r\n        return a * factor;\r\n    }\r\n}\r\n";
        using var ws = TestWorkspace.Create(
            ("Calc.cs", Calc),
            ("Use.cs", "public static class Use\n{\n    public static int Run() => Calc.Total(3);\n}\n"));

        var result = await new IntroduceParameterOperation(ws.Context).ExecuteAsync(
            new IntroduceParameterParams { SourceFile = ws.PathOf("Calc.cs"), VariableName = "factor", Line = 5 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var use = ws.ReadFile("Use.cs");
        Assert.Contains("Max(", use);
        Assert.Contains("\n            3,\n", use);
        AssertLf(use);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractInterface_IntoANewLfFile_WritesWhatComesFromTheSourceInLf()
    {
        const string Source =
            "/* The usings\r\n   of the dog */\r\nusing System;\r\n\r\nnamespace App;\r\n\r\npublic class Dog\r\n{\r\n    public string Bark() => String.Empty;\r\n}\r\n";
        using var ws = TestWorkspace.Create([("Dog.cs", Source)], [], editorConfig: LfEditorConfig);

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                InterfaceName = "IDog",
                TargetFile = ws.PathOf("IDog.cs"),
                AddInterfaceToType = false,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertLf(ws.ReadFile("IDog.cs"), "/* The usings\n   of the dog */");
    }

    /// <summary>
    /// Every line break of <paramref name="text"/> is LF, but those inside <see cref="Literal"/>;
    /// and it contains <paramref name="expected"/> (moved there and converted).
    /// </summary>
    private static void AssertLf(string text, params string[] expected)
    {
        Assert.DoesNotContain('\r', text.Replace(Literal, "", StringComparison.Ordinal));
        foreach (var part in expected)
            Assert.Contains(part, text);
    }

    private static void AssertKeptLiteralWarning(RefactoringResult result, string file)
    {
        var warning = Assert.Single(result.Warnings!, w => w.Contains("string literal", StringComparison.Ordinal));
        Assert.Equal(
            $"'{file}': a string literal of the code moved there keeps its line breaks, which are part of its value; the file's other lines end in LF.",
            warning);
    }
}
