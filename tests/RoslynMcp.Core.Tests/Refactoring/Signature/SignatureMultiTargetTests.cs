using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// A multi-targeted project compiles each file once per target framework. change_signature and
/// change_return_type edited such a file through one framework's document, so a call, a use of
/// a renamed parameter or a declaration in an <c>#if</c> branch that only another framework
/// compiles was left as it was (and only showed up in that framework's build).
/// </summary>
public sealed class SignatureMultiTargetTests
{
    private const string Counter = """
        namespace Lib;

        public abstract class Counter
        {
            public abstract int Add(int a, int b);
        }
        """;

    private const string Tally = """
        using Lib;

        namespace App;

        public class Tally : Counter
        {
        #if NET11
            public override int Add(int a, int b) => a + b;
        #else
            public override int Add(int a, int b)
            {
                return checked(a + b);
            }
        #endif

            public int Twice(int value)
            {
        #if NET11
                return Add(value, 1);
        #else
                return Add(value, 2);
        #endif
            }
        }
        """;

    private const string Calc = """
        namespace App;

        public class Calc
        {
            public int Mul(
                int left,
                int right)
            {
        #if NET11
                return left * right;
        #else
                return checked(left * right);
        #endif
            }

            public int Square(int x)
            {
        #if NET11
                return Mul(
                    x,
                    x);
        #else
                return Mul(left: x, right: x);
        #endif
            }

        #if NET11
            public int Cube(int x) => Mul(Square(x), x);
        #endif
        }
        """;

    private const string Shapes = """
        namespace App;

        public abstract class Shape
        {
            public abstract int Sides();
        }

        public class Square : Shape
        {
            public override int Sides() => 4;
        }
        #if NET11

        public class Hexagon : Shape
        {
            public override int Sides() => 6;
        }
        #endif
        """;

    [Fact]
    public async Task ChangeSignature_RewritesTheOverridesAndCallsOfEveryTargetFramework()
    {
        using var ws = CreateWorkspace();

        var result = await ChangeSignatureAsync(ws, ws.PathOf("Lib", "Counter.cs"), "Add",
            new ParameterChange { OriginalName = "b", NewPosition = 0 },
            new ParameterChange { Name = "carry", Type = "int", DefaultValue = "0" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using Lib;

            namespace App;

            public class Tally : Counter
            {
            #if NET11
                public override int Add(int b, int a, int carry = 0) => a + b;
            #else
                public override int Add(int b, int a, int carry = 0)
                {
                    return checked(a + b);
                }
            #endif

                public int Twice(int value)
                {
            #if NET11
                    return Add(1, value);
            #else
                    return Add(2, value);
            #endif
                }
            }
            """), File.ReadAllText(ws.PathOf("App", "Tally.cs")));
        Assert.Equal(2, result.ReferencesUpdated);
        await AssertEveryFrameworkHasTheFileTextAsync(ws, ws.PathOf("App", "Tally.cs"));
    }

    [Fact]
    public async Task ChangeSignature_RenamesAndMovesInEveryBranch_AlsoWhereLinesAreAdded()
    {
        using var ws = CreateWorkspace();

        var result = await ChangeSignatureAsync(ws, ws.PathOf("App", "Calc.cs"), "Mul",
            new ParameterChange { OriginalName = "left", Name = "factor", NewPosition = 1 },
            new ParameterChange { Name = "scale", Type = "int" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App;

            public class Calc
            {
                public int Mul(
                    int right,
                    int factor,
                    int scale)
                {
            #if NET11
                    return factor * right;
            #else
                    return checked(factor * right);
            #endif
                }

                public int Square(int x)
                {
            #if NET11
                    return Mul(
                        x,
                        x,
                        default /* TODO: scale */);
            #else
                    return Mul(factor: x, right: x, scale: default /* TODO: scale */);
            #endif
                }

            #if NET11
                public int Cube(int x) => Mul(x, Square(x), default /* TODO: scale */);
            #endif
            }
            """), File.ReadAllText(ws.PathOf("App", "Calc.cs")));
        Assert.Equal(3, result.ReferencesUpdated);
        Assert.Equal(3, result.Warnings!.Count(w => w.Contains("TODO: replace the placeholder", StringComparison.Ordinal)));
        await AssertEveryFrameworkHasTheFileTextAsync(ws, ws.PathOf("App", "Calc.cs"));
    }

    [Fact]
    public async Task ChangeReturnType_ChangesTheDeclarationOfEveryTargetFramework()
    {
        using var ws = CreateWorkspace();

        var result = await new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams { SourceFile = ws.PathOf("Lib", "Counter.cs"), MemberName = "Add", NewType = "long" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var tally = File.ReadAllText(ws.PathOf("App", "Tally.cs"));
        Assert.Contains("    public override long Add(int a, int b) => a + b;\n#else\n    public override long Add(int a, int b)\n", tally);
        // Twice returns int: both frameworks' returns no longer compile.
        Assert.Equal(2, result.Warnings!.Count(w => w.Contains("error CS0266", StringComparison.Ordinal)));
        await AssertEveryFrameworkHasTheFileTextAsync(ws, ws.PathOf("App", "Tally.cs"));
    }

    [Fact]
    public async Task ChangeReturnType_ChangesAnOverrideOnlyAnotherFrameworkCompiles()
    {
        using var ws = CreateWorkspace();

        var result = await new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams { SourceFile = ws.PathOf("App", "Shapes.cs"), MemberName = "Sides", Line = 5, NewType = "long" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf(Shapes).Replace(" int Sides()", " long Sides()"), File.ReadAllText(ws.PathOf("App", "Shapes.cs")));
        Assert.Null(result.Warnings);
        await AssertEveryFrameworkHasTheFileTextAsync(ws, ws.PathOf("App", "Shapes.cs"));
    }

    [Fact]
    public async Task ChangeSignature_FindsAMethodDeclaredOnlyForAnotherFramework()
    {
        using var ws = CreateWorkspace();

        var result = await ChangeSignatureAsync(ws, ws.PathOf("App", "Calc.cs"), "Cube",
            new ParameterChange { OriginalName = "x", Name = "value" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("    public int Cube(int value) => Mul(Square(value), value);", File.ReadAllText(ws.PathOf("App", "Calc.cs")));
        await AssertEveryFrameworkHasTheFileTextAsync(ws, ws.PathOf("App", "Calc.cs"));
    }

    private static TestWorkspace CreateWorkspace() => TestWorkspace.CreateSolution(
        new TestWorkspace.ProjectSpec("Lib", [("Counter.cs", Lf(Counter))]),
        new TestWorkspace.ProjectSpec("App", [("Tally.cs", Lf(Tally)), ("Calc.cs", Lf(Calc)), ("Shapes.cs", Lf(Shapes))], References: ["Lib"], TargetFrameworks: ["net10", "net11"]));

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static async Task AssertEveryFrameworkHasTheFileTextAsync(TestWorkspace ws, string path)
    {
        var text = File.ReadAllText(path);
        var ids = ws.Context.Solution.GetDocumentIdsWithFilePath(path);
        Assert.Equal(2, ids.Length);
        foreach (var id in ids)
            Assert.Equal(text, (await ws.Context.Solution.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    private static Task<RefactoringResult> ChangeSignatureAsync(TestWorkspace ws, string file, string method, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams { SourceFile = file, MethodName = method, Parameters = parameters },
            TestContext.Current.CancellationToken);
}
