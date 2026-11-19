using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// The signature operations report the errors a change causes anywhere in the projects it
/// touches and in the projects depending on them, not only in the files that name what changed:
/// a call to another overload that becomes ambiguous breaks a file that never mentions the
/// changed method.
/// </summary>
public sealed class IntroducedErrorsScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangeSignature_ReportsAnOverloadMadeAmbiguousInADependentProject()
    {
        const string format = """
            namespace Lib;

            public static class Format
            {
                public static string Show(long value, string unit) => value + unit;

                public static string Show(int value, long count) => value + "x" + count;
            }
            """;
        const string report = """
            using Lib;

            namespace App;

            public static class Report
            {
                public static string Line() => Format.Show(1, 2);
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Format.cs", format)]),
            new TestWorkspace.ProjectSpec("App", [("Report.cs", report)], References: ["Lib"]));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Lib", "Format.cs"),
                MethodName = "Show",
                Line = 5,
                Parameters = [new ParameterChange { OriginalName = "unit", Type = "int" }]
            },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static string Show(long value, int unit)", File.ReadAllText(ws.PathOf("Lib", "Format.cs")));
        var warning = Assert.Single(result.Warnings!, w => w.Contains("error CS0121", StringComparison.Ordinal));
        Assert.StartsWith(ws.PathOf("App", "Report.cs") + "(7,", warning);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task ChangeReturnType_ReportsErrorsOnlyOnce_ForEveryFrameworkOfAMultiTargetedProject()
    {
        const string counter = """
            namespace Lib;

            public class Counter
            {
                public int Count() => 1;
            }
            """;
        const string use = """
            using Lib;

            namespace App;

            public static class Use
            {
                public static int Total(Counter counter)
                {
                    var count = counter.Count();
                    return Twice(count);
                }

                private static int Twice(int value) => value * 2;
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Counter.cs", counter)]),
            new TestWorkspace.ProjectSpec("App", [("Use.cs", use)], References: ["Lib"], TargetFrameworks: ["net10_0", "net11_0"]));

        var result = await new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams { SourceFile = ws.PathOf("Lib", "Counter.cs"), MemberName = "Count", NewType = "long" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var warning = Assert.Single(result.Warnings!);
        Assert.StartsWith(ws.PathOf("App", "Use.cs") + "(10,", warning);
        Assert.Contains("error CS1503", warning);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task IntroduceParameter_ReportsAnOverloadMadeAmbiguousInAFileThatCallsAnother()
    {
        const string api = """
            namespace App;

            public static class Api
            {
                public static string Send(int id)
                {
                    string channel = "mail";
                    return id + channel;
                }

                public static string Send(int id, System.Uri target) => id + target.ToString();
            }
            """;
        const string client = """
            namespace App;

            public static class Client
            {
                public static string Post() => Api.Send(1, null);
            }
            """;
        using var ws = TestWorkspace.Create(("Api.cs", api), ("Client.cs", client));

        var result = await new IntroduceParameterOperation(ws.Context).ExecuteAsync(
            new IntroduceParameterParams { SourceFile = ws.PathOf("Api.cs"), VariableName = "channel", Line = 7 },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static string Send(int id, string channel)", ws.ReadFile("Api.cs"));
        var warning = Assert.Single(result.Warnings!, w => w.Contains("error CS0121", StringComparison.Ordinal));
        Assert.StartsWith(ws.PathOf("Client.cs") + "(5,", warning);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }
}
