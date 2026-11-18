using System.Reflection;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// How change_signature rewrites calls, and what it reports about the code it cannot fix.
/// Calls used to be rewritten into declaration order, which changed the order named arguments
/// are evaluated in; and errors such as a method group that no longer matches its delegate
/// went unreported.
/// </summary>
public sealed class ChangeSignatureCallSiteTests
{
    private const string Pairs = """
        namespace App;

        public static class Pair
        {
            private static int _next;

            public static int Next() => ++_next;

            public static string Make(int a, int b) => $"{a},{b}";

            public static string Use() => Make(b: Next(), a: Next());
        }
        """;

    [Fact]
    public async Task NamedArguments_KeepTheirOrder_AndANewArgumentAfterThemIsNamed()
    {
        using var ws = TestWorkspace.Create(("Pair.cs", Pairs));
        var before = Run(ws, "App.Pair", "Use");

        var result = await ChangeAsync(ws, "Pair.cs", "Make", Keep("a"), Keep("b"), Add("c", "int", "0"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static string Use() => Make(b: Next(), a: Next(), c: 0);", ws.ReadFile("Pair.cs"));
        Assert.Equal("2,1", before);
        Assert.Equal(before, Run(ws, "App.Pair", "Use"));
    }

    [Fact]
    public async Task RemovedAndRenamedNamedArguments_ChangeInPlace()
    {
        const string source = """
            namespace App;

            public static class Triple
            {
                private static int _next;

                public static int Next() => ++_next;

                public static string Make(int a, int b, int c) => $"{a},{c}";

                public static string Use() => Make(c: Next(), b: Next(), a: Next());
            }
            """;
        using var ws = TestWorkspace.Create(("Triple.cs", source));

        var result = await ChangeAsync(ws, "Triple.cs", "Make",
            Keep("a"), new ParameterChange { OriginalName = "c", Name = "last" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Triple.cs");
        Assert.Contains("public static string Make(int a, int last) => $\"{a},{last}\";", text);
        Assert.Contains("public static string Use() => Make(last: Next(), a: Next());", text);
        Assert.Null(result.Warnings);
        Assert.Equal("2,1", Run(ws, "App.Triple", "Use"));
    }

    [Fact]
    public async Task Reorder_MovesPositionalArguments_AndNamesWhatWouldBeOutOfPosition()
    {
        const string source = """
            namespace App;

            public static class Digits
            {
                public static int Mix(int a, int b, int c) => a * 100 + b * 10 + c;

                public static int Positional() => Mix(1, 2, 3);
                public static int NamedLast() => Mix(1, 2, c: 3);
                public static int NamedFirst() => Mix(a: 1, 2, 3);
            }
            """;
        using var ws = TestWorkspace.Create(("Digits.cs", source));

        var result = await ChangeAsync(ws, "Digits.cs", "Mix",
            new ParameterChange { OriginalName = "c", Name = "c", NewPosition = 0 },
            new ParameterChange { OriginalName = "a", Name = "a", NewPosition = 1 },
            new ParameterChange { OriginalName = "b", Name = "b", NewPosition = 2 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Digits.cs");
        Assert.Contains("public static int Mix(int c, int a, int b)", text);
        Assert.Contains("Positional() => Mix(3, 1, 2);", text);
        Assert.Contains("NamedLast() => Mix(a: 1, b: 2, c: 3);", text);
        Assert.Contains("NamedFirst() => Mix(a: 1, c: 3, b: 2);", text);
        foreach (var method in new[] { "Positional", "NamedLast", "NamedFirst" })
            Assert.Equal(123, Run(ws, "App.Digits", method));
    }

    private const string Measures = """
        using System;

        namespace App;

        public interface IMeasure
        {
            double Measure(int a, int b);
        }

        public class Ruler : IMeasure
        {
            public double Measure(int a, int b) => a + b;
        }

        public static class Program
        {
            public static Func<int, int, double> Make() => new Ruler().Measure;

            public static double Call(IMeasure measure) => measure.Measure(1, 2);
        }
        """;

    [Fact]
    public async Task MethodGroupThatNoLongerMatchesItsDelegate_IsAWarning()
    {
        using var ws = TestWorkspace.Create(("Measure.cs", Measures));

        var result = await ChangeAsync(ws, "Measure.cs", "Measure", line: 7, Keep("a"), Keep("b"), Add("c", "int", "0"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("measure.Measure(1, 2, 0)", ws.ReadFile("Measure.cs"));
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("Measure.cs(17,", warning);
        Assert.Contains("error CS0123", warning);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task Preview_WarnsAboutARemovedParameterTheBodyStillUses_AndWritesNothing()
    {
        using var ws = TestWorkspace.Create(("Measure.cs", Measures));

        var result = await ChangeAsync(ws, "Measure.cs", "Measure", line: 7, preview: true, Keep("a"));

        Assert.True(result.Preview);
        Assert.Equal(Measures, ws.ReadFile("Measure.cs"));
        Assert.Contains(result.Warnings!, w => w.Contains("Measure.cs(12,", StringComparison.Ordinal) && w.Contains("error CS0103", StringComparison.Ordinal));
        Assert.Contains(result.Warnings!, w => w.Contains("Measure.cs(17,", StringComparison.Ordinal) && w.Contains("error CS0123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlaceholderForAParameterWithoutDefault_IsAWarning()
    {
        using var ws = TestWorkspace.Create(("Measure.cs", Measures));

        var result = await ChangeAsync(ws, "Measure.cs", "Measure", line: 7,
            Keep("a"), Keep("b"), new ParameterChange { Name = "unit", Type = "string" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("measure.Measure(1, 2, default /* TODO: unit */)", ws.ReadFile("Measure.cs"));
        Assert.Contains(result.Warnings!, w =>
            w.StartsWith(ws.PathOf("Measure.cs") + "(19,", StringComparison.Ordinal) &&
            w.EndsWith("TODO: replace the placeholder 'default' passed for parameter 'unit'", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    /// <summary>Compiles the files on disk and runs a static method without parameters.</summary>
    private static object? Run(TestWorkspace ws, string type, string method) =>
        Assembly.Load(ws.EmitDiskAssembly()).GetType(type)!.GetMethod(method)!.Invoke(null, null);

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws, string file, string method, params ParameterChange[] parameters) =>
        ChangeAsync(ws, file, method, line: null, preview: false, parameters);

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws, string file, string method, int? line, params ParameterChange[] parameters) =>
        ChangeAsync(ws, file, method, line, preview: false, parameters);

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws, string file, string method, int? line, bool preview, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf(file),
                MethodName = method,
                Line = line,
                Parameters = parameters,
                Preview = preview
            },
            TestContext.Current.CancellationToken);

    private static ParameterChange Keep(string name) => new() { OriginalName = name, Name = name };

    private static ParameterChange Add(string name, string type, string defaultValue) =>
        new() { Name = name, Type = type, DefaultValue = defaultValue };
}
