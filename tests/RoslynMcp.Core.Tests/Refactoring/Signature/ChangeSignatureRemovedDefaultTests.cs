using System.Reflection;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Removing a parameter's default value (<c>defaultValue: ""</c>) gave the calls that left the
/// argument out a <c>default /* TODO */</c> placeholder and a warning. They now pass the value
/// they used to get: the default of the member the call binds to, as the declaration writes it
/// (qualified so it means the same at the call), or as a literal where that cannot be written.
/// </summary>
public sealed class ChangeSignatureRemovedDefaultTests
{
    [Fact]
    public async Task CallsThatLeftTheArgumentOut_PassTheOldDefault()
    {
        const string source = """
            namespace App;

            public class M
            {
                public int Mix(int a, int b, int c = 7) => a * 100 + b * 10 + c;
            }

            public static class Use
            {
                public static int Positional() => new M().Mix(1, 2);
                public static int Named() => new M().Mix(b: 1, a: 2);
                public static int Passed() => new M().Mix(1, 2, 3);
            }
            """;
        using var ws = TestWorkspace.Create(("M.cs", source));

        var result = await ChangeAsync(ws, "M.cs", "Mix", line: null, new ParameterChange { OriginalName = "c", DefaultValue = "" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("M.cs");
        Assert.Contains("public int Mix(int a, int b, int c) =>", text);
        Assert.Contains("new M().Mix(1, 2, 7);", text);
        Assert.Contains("new M().Mix(b: 1, a: 2, c: 7);", text);
        Assert.Contains("new M().Mix(1, 2, 3);", text);
        Assert.Equal(2, result.ReferencesUpdated);
        Assert.Null(result.Warnings);
        Assert.Equal(127, Run(ws, "Positional"));
        Assert.Equal(217, Run(ws, "Named"));
    }

    [Fact]
    public async Task NamedDefaults_AreQualifiedForTheCall_OrWrittenAsTheirValue()
    {
        const string library = """
            namespace Lib;

            public enum Mode { Fast = 1, Safe = 2 }

            public static class Limits
            {
                public const int Max = 40;
            }

            public class Worker
            {
                public const int Step = 5;
                private const int Hidden = 3;

                public int Run(int a, int max = Limits.Max, int step = Step, int hidden = Hidden + 1, Mode mode = Mode.Safe) =>
                    a + max + step * 10 + hidden * 100 + (int)mode * 1000;
            }
            """;
        const string application = """
            namespace App;

            public static class Use
            {
                public static int Call() => new Lib.Worker().Run(1);
            }
            """;
        using var ws = TestWorkspace.Create(("Worker.cs", library), ("Use.cs", application));
        var before = Run(ws, "Call");

        var result = await ChangeAsync(ws, "Worker.cs", "Run", line: null,
            new ParameterChange { OriginalName = "max", DefaultValue = "" },
            new ParameterChange { OriginalName = "step", DefaultValue = "" },
            new ParameterChange { OriginalName = "hidden", DefaultValue = "" },
            new ParameterChange { OriginalName = "mode", DefaultValue = "" });

        Assert.True(result.Success, result.Error?.Message);
        // Step is qualified, the private Hidden + 1 becomes its value, and the namespace stays
        // where the call has no using for it.
        Assert.Contains(
            "new Lib.Worker().Run(1, Lib.Limits.Max, Lib.Worker.Step, 4, Lib.Mode.Safe);",
            ws.ReadFile("Use.cs"));
        Assert.Null(result.Warnings);
        Assert.Equal(2491, before);
        Assert.Equal(before, Run(ws, "Call"));
    }

    [Fact]
    public async Task FamilyMembersWithDifferentDefaults_EachCallPassesTheDefaultOfTheMemberItBindsTo()
    {
        const string source = """
            namespace App;

            public interface IShape
            {
                int Area(int scale = 1);
            }

            public class Square : IShape
            {
                public int Area(int scale = 2) => 10 * scale;
            }

            public static class Use
            {
                public static int ViaInterface() => ((IShape)new Square()).Area();
                public static int ViaClass() => new Square().Area();
            }
            """;
        using var ws = TestWorkspace.Create(("Shape.cs", source));

        var result = await ChangeAsync(ws, "Shape.cs", "Area", line: 5, new ParameterChange { OriginalName = "scale", DefaultValue = "" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Shape.cs");
        Assert.Contains("int Area(int scale);", text);
        Assert.Contains("public int Area(int scale) => 10 * scale;", text);
        Assert.Contains("((IShape)new Square()).Area(1);", text);
        Assert.Contains("new Square().Area(2);", text);
        Assert.Null(result.Warnings);
        Assert.Equal(10, Run(ws, "ViaInterface"));
        Assert.Equal(20, Run(ws, "ViaClass"));
    }

    /// <summary>Compiles the files on disk and runs a method of <c>App.Use</c>.</summary>
    private static object? Run(TestWorkspace ws, string method) =>
        Assembly.Load(ws.EmitDiskAssembly()).GetType("App.Use")!.GetMethod(method)!.Invoke(null, null);

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws, string file, string method, int? line, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams { SourceFile = ws.PathOf(file), MethodName = method, Line = line, Parameters = parameters },
            TestContext.Current.CancellationToken);
}
