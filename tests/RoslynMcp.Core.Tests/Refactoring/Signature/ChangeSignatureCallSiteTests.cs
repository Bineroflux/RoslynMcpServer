using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// What change_signature reports about the code it cannot fix: errors such as a method group
/// that no longer matches its delegate used to go unreported.
/// </summary>
public sealed class ChangeSignatureCallSiteTests
{
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
