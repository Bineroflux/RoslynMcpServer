using System.Runtime.Loader;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_method decides the new method's signature from the selection's data flow: instance
/// members are used directly (never `this` as an argument), variables assigned in the selection
/// and read after it are returned (a tuple for several), a selection that always returns is
/// returned, and await makes it async. Each extraction is compiled and run before and after.
/// </summary>
public sealed class ExtractMethodFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InstanceMembers_InstanceMethodWithoutThisArgument()
    {
        using var ws = Create("""
            public class Greeter
            {
                private readonly string _prefix;

                public Greeter(string prefix) => _prefix = prefix;

                public string Greet(string name)
                {
                    var text = _prefix + ", " + name;
                    return text.ToUpperInvariant();
                }
            }

            public static class Program
            {
                public static string Run() => new Greeter("Hello").Greet("Ada");
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var text = _prefix", "+ name;", "Compose");

        Assert.Contains("var text = Compose(name);", text);
        Assert.Contains("private string Compose(string name)", text);
        Assert.DoesNotContain("this", text);
    }

    [Fact]
    public async Task StructInstanceState_InstanceMethodMutatesTheValue()
    {
        using var ws = Create("""
            public struct Counter
            {
                private int _count;

                public int Count => _count;

                public void Bump(int by)
                {
                    _count += by;
                    _count *= 2;
                }
            }

            public static class Program
            {
                public static string Run()
                {
                    var counter = new Counter();
                    counter.Bump(3);
                    counter.Bump(1);
                    return counter.Count.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "_count += by;", "_count *= 2;", "Apply", expected: "14");

        Assert.Contains("Apply(by);", text);
        Assert.Contains("private void Apply(int by)", text);
    }

    [Fact]
    public async Task ReadOnlyStructMember_ReadOnlyMethod()
    {
        using var ws = Create("""
            public struct Point
            {
                public int X;
                public int Y;

                public readonly int Length()
                {
                    var sum = X * X + Y * Y;
                    return sum;
                }
            }

            public static class Program
            {
                public static string Run() => new Point { X = 3, Y = 4 }.Length().ToString();
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var sum", "Y * Y;", "SumOfSquares");

        Assert.Contains("private readonly int SumOfSquares()", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task NoInstanceState_StaticMethod()
    {
        using var ws = Create("""
            public class Formatter
            {
                private int _calls;

                public string Format(int value)
                {
                    _calls++;
                    var text = "#" + value;
                    return text + _calls;
                }
            }

            public static class Program
            {
                public static string Run() => new Formatter().Format(7);
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var text = \"#\"", "value;", "Tag");

        Assert.Contains("var text = Tag(value);", text);
        Assert.Contains("private static string Tag(int value)", text);
    }

    [Fact]
    public async Task VariableAssignedAndReadAfter_IsReturned()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var items = new[] { 3, 1, 4, 1, 5 };
                    var total = 0;
                    foreach (var item in items)
                    {
                        total += item;
                    }

                    return total.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "foreach", "total += item;\n        }", "Sum", expected: "14");

        Assert.Contains("total = Sum(items, total);", text);
        Assert.Contains("private static int Sum(int[] items, int total)", text);
        Assert.Contains("return total;", text);
    }

    [Fact]
    public async Task SeveralVariablesFlowOut_TupleDeconstructedIntoThem()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var items = new[] { 3, 1, 4, 1, 5 };
                    var min = int.MaxValue;
                    var max = int.MinValue;
                    foreach (var item in items)
                    {
                        if (item < min) min = item;
                        if (item > max) max = item;
                    }

                    return $"{min}..{max}";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "foreach", "max = item;\n        }", "Range", expected: "1..5");

        Assert.Contains("(min, max) = Range(items, min, max);", text);
        Assert.Contains("private static (int min, int max) Range(int[] items, int min, int max)", text);
        Assert.Contains("return (min, max);", text);
    }

    [Fact]
    public async Task VariablesDeclaredInSelection_DeclaredByTheDeconstruction()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var items = new[] { 3, 1, 4 };
                    var count = items.Length;
                    var first = items[0];
                    return $"{count}/{first}";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var count", "items[0];", "Measure", expected: "3/3");

        Assert.Contains("var (count, first) = Measure(items);", text);
        Assert.Contains("private static (int count, int first) Measure(int[] items)", text);
    }

    [Fact]
    public async Task AssignedAndDeclaredVariables_MixedDeconstruction()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var label = "start";
                    label = label + "!";
                    var size = label.Length;
                    return label + size;
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "label = label", "label.Length;", "Mark", expected: "start!6");

        Assert.Contains("(label, var size) = Mark(label);", text);
        Assert.Contains("private static (string label, int size) Mark(string label)", text);
    }

    [Fact]
    public async Task VariableAssignedOnSomePaths_IsPassedInAndReturned()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run() => Classify(new[] { 1, 2 }) + Classify(new[] { 1, 2, 3, 4 });

                private static string Classify(int[] items)
                {
                    var label = "few";
                    if (items.Length > 3)
                    {
                        label = "many";
                    }

                    return label;
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "if (items.Length", "\"many\";\n        }", "Choose", expected: "fewmany");

        Assert.Contains("label = Choose(items, label);", text);
        Assert.Contains("private static string Choose(int[] items, string label)", text);
    }

    [Fact]
    public async Task ReturnStatement_CallIsReturned_ArgumentsSpaced()
    {
        using var ws = Create("""
            using System.Collections.Generic;

            public static class Program
            {
                public static string Run() => Describe("odd", new List<int> { 1, 3 });

                private static string Describe(string name, List<int> list)
                {
                    return name + ": " + string.Join(",", list);
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "return name", "list);", "Join", expected: "odd: 1,3");

        Assert.Contains("return Join(name, list);", text);
        Assert.Contains("private static string Join(string name, List<int> list)", text);
    }

    [Fact]
    public async Task ReturnsOnEveryPath_CallIsReturned()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run() => Sign(4) + Sign(-2) + Sign(0);

                private static string Sign(int x)
                {
                    var y = x * 2;
                    if (y > 0)
                        return "+";
                    if (y < 0)
                        return "-";
                    return "0";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "if (y > 0)", "return \"0\";", "Classify", expected: "+-0");

        Assert.Contains("return Classify(y);", text);
        Assert.Contains("private static string Classify(int y)", text);
    }

    [Fact]
    public async Task ThrowsOnEveryPath_CallIsReturned()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run() => Check(5).ToString();

                private static int Check(int x)
                {
                    if (x > 0)
                        return x;
                    throw new System.ArgumentOutOfRangeException(nameof(x));
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "throw new", "(x));", "Fail", expected: "5");

        Assert.Contains("return Fail(x);", text);
        Assert.Contains("private static int Fail(int x)", text);
    }

    [Fact]
    public async Task VoidReturnInsideTheFunction_CallThenReturn()
    {
        using var ws = Create("""
            using System.Collections.Generic;

            public static class Program
            {
                public static string Run()
                {
                    var log = new List<string>();
                    Log(log, -1);
                    Log(log, 1);
                    return string.Join(",", log);
                }

                private static void Log(List<string> log, int x)
                {
                    if (x < 0)
                    {
                        log.Add("negative");
                        return;
                    }

                    log.Add("ok");
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "log.Add(\"negative\");", "return;", "Report", expected: "negative,ok");

        Assert.Contains("Report(log);\n            return;", text);
        Assert.Contains("private static void Report(List<string> log)", text);
    }

    [Fact]
    public async Task ReturnOnSomePathsOnly_IsRefused()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run() => Sign(1);

                private static string Sign(int x)
                {
                    if (x > 0)
                        return "+";
                    return "-";
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "if (x > 0)", "return \"+\";", "Positive"));

        Assert.Equal(ErrorCodes.MultipleExitPoints, ex.ErrorCode);
        Assert.Contains("some paths only", ex.Message);
    }

    [Theory]
    [InlineData("break;", "break")]
    [InlineData("continue;", "continue")]
    public async Task JumpOutOfTheSelection_IsRefused(string jump, string keyword)
    {
        using var ws = Create($$"""
            public static class Program
            {
                public static string Run()
                {
                    var total = 0;
                    for (var i = 0; i < 10; i++)
                    {
                        if (i == 5)
                            {{jump}}
                        total += i;
                    }

                    return total.ToString();
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "if (i == 5)", jump, "Stop"));

        Assert.Equal(ErrorCodes.UnresolvableControlFlow, ex.ErrorCode);
        Assert.Contains($"'{keyword}'", ex.Message);
    }

    [Fact]
    public async Task Await_AsyncMethodIsAwaited()
    {
        using var ws = Create("""
            using System.Threading.Tasks;

            public static class Program
            {
                public static string Run() => RunAsync().GetAwaiter().GetResult();

                private static Task<int> LoadAsync(int x) => Task.FromResult(x * 3);

                private static async Task<string> RunAsync()
                {
                    var a = 2;
                    var loaded = await LoadAsync(a);
                    var doubled = loaded * 2;
                    return doubled.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var loaded", "loaded * 2;", "ComputeAsync", expected: "12");

        Assert.Contains("var doubled = await ComputeAsync(a);", text);
        Assert.Contains("private static async Task<int> ComputeAsync(int a)", text);
    }

    [Fact]
    public async Task AwaitAndReturn_AwaitedCallIsReturned()
    {
        using var ws = Create("""
            using System.Threading.Tasks;

            public static class Program
            {
                public static string Run() => RunAsync().GetAwaiter().GetResult();

                private static Task<int> LoadAsync(int x) => Task.FromResult(x * 3);

                private static async Task<string> RunAsync()
                {
                    var a = 2;
                    var loaded = await LoadAsync(a);
                    return loaded.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var loaded", "return loaded.ToString();", "LoadTextAsync", expected: "6");

        Assert.Contains("return await LoadTextAsync(a);", text);
        Assert.Contains("private static async Task<string> LoadTextAsync(int a)", text);
    }

    [Fact]
    public async Task AwaitInNestedLambdaOnly_MethodIsNotAsync()
    {
        using var ws = Create("""
            using System;
            using System.Threading.Tasks;

            public static class Program
            {
                public static string Run()
                {
                    var x = 4;
                    Func<Task<int>> load = async () => await Task.FromResult(x);
                    return load().GetAwaiter().GetResult().ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "Func<Task<int>> load", "FromResult(x);", "MakeLoader", expected: "4");

        Assert.Contains("var load = MakeLoader(x);", text);
        Assert.Contains("private static Func<Task<int>> MakeLoader(int x)", text);
    }

    [Fact]
    public async Task ExpressionThatIncrements_VariablePassedByRef()
    {
        using var ws = Create("""
            public static class Program
            {
                private static int Take(int x) => x * 10;

                public static string Run()
                {
                    var i = 0;
                    var first = Take(i++);
                    var second = Take(i++);
                    return $"{first},{second},{i}";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "i++", "i++", "Next", expected: "0,10,2");

        Assert.Contains("var first = Take(Next(ref i));", text);
        Assert.Contains("private static int Next(ref int i)", text);
    }

    [Fact]
    public async Task ExpressionWithOutVarUsedAfter_OutParameter()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var text = "42";
                    if (int.TryParse(text, out var n) && n > 0)
                        return n.ToString();
                    return "none";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "int.TryParse", "out var n)", "Parse", expected: "42");

        Assert.Contains("if (Parse(text, out var n) && n > 0)", text);
        Assert.Contains("private static bool Parse(string text, out int n)", text);
        Assert.Contains("return int.TryParse(text, out n);", text);
    }

    [Fact]
    public async Task VariableAssignedBeforeRead_DeclaredInTheNewMethod()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    int tmp;
                    tmp = 5;
                    var shown = tmp * 2;
                    return shown.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "tmp = 5;", "tmp * 2;", "Compute", expected: "10");

        Assert.Contains("var shown = Compute();", text);
        Assert.Contains("int tmp;\n        tmp = 5;", text);
    }

    [Fact]
    public async Task GenericMethod_TypeParametersAndConstraintsCarriedOver()
    {
        using var ws = Create("""
            using System.Collections.Generic;

            public static class Program
            {
                public static string Run() => First(new List<string> { "a", "b" });

                private static T First<T>(List<T> items) where T : class
                {
                    var item = items[0];
                    return item;
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var item", "items[0];", "Pick", expected: "a");

        Assert.Contains("var item = Pick<T>(items);", text);
        Assert.Contains("private static T Pick<T>(List<T> items)", text);
        Assert.Contains("where T : class", text);
    }

    [Fact]
    public async Task LambdaBody_ExtractedFromTheLambda()
    {
        using var ws = Create("""
            using System.Linq;

            public static class Program
            {
                public static string Run()
                {
                    var factor = 3;
                    var values = new[] { 1, 2 }.Select(x =>
                    {
                        var y = x * factor;
                        return y + 1;
                    }).ToArray();
                    return string.Join(",", values);
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var y = x", "return y + 1;", "Scale", expected: "4,7");

        Assert.Contains("return Scale(factor, x);", text);
        Assert.Contains("private static int Scale(int factor, int x)", text);
    }

    [Fact]
    public async Task FieldInitializer_StaticMethod()
    {
        using var ws = Create("""
            public class Config
            {
                private readonly int _size = 4 * 1024;

                public int Size => _size;
            }

            public static class Program
            {
                public static string Run() => new Config().Size.ToString();
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "4 * 1024", "4 * 1024", "DefaultSize", expected: "4096");

        Assert.Contains("private readonly int _size = DefaultSize();", text);
        Assert.Contains("private static int DefaultSize()", text);
    }

    [Fact]
    public async Task CheckedBlock_NewMethodStaysChecked()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var a = int.MaxValue;
                    try
                    {
                        checked
                        {
                            var big = a + 1;
                            return big.ToString();
                        }
                    }
                    catch (System.OverflowException)
                    {
                        return "overflow";
                    }
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var big", "a + 1;", "Grow", expected: "overflow");

        Assert.Contains("checked\n        {\n            var big = a + 1;", text);
    }

    [Fact]
    public async Task MakeStaticWithInstanceMembers_IsRefused()
    {
        using var ws = Create("""
            public class Box
            {
                private int _size = 2;

                public int Twice()
                {
                    var size = _size * 2;
                    return size;
                }
            }

            public static class Program
            {
                public static string Run() => new Box().Twice().ToString();
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "var size", "* 2;", "Double", makeStatic: true));

        Assert.Equal(ErrorCodes.InvalidParameterValue, ex.ErrorCode);
    }

    [Fact]
    public async Task LocalFunctionDeclaredOutside_IsRefused()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    int Square(int x) => x * x;
                    var result = Square(3);
                    return result.ToString();
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "var result", "Square(3);", "Compute"));

        Assert.Equal(ErrorCodes.SelectionCrossesScopes, ex.ErrorCode);
        Assert.Contains("'Square'", ex.Message);
    }

    [Fact]
    public async Task RethrowOutsideItsCatch_IsRefused()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    try
                    {
                        return "ok";
                    }
                    catch (System.Exception)
                    {
                        System.Console.WriteLine("failed");
                        throw;
                    }
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "System.Console.WriteLine(\"failed\");", "throw;", "Fail"));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("rethrows", ex.Message);
    }

    [Theory]
    [InlineData("Size")]
    [InlineData("local")]
    public async Task NameThatClashes_IsRefused(string name)
    {
        using var ws = Create("""
            public class Box
            {
                public int Size { get; set; }

                public int Grow(int by)
                {
                    var local = 1;
                    var grown = by * 2 + local;
                    return grown;
                }
            }

            public static class Program
            {
                public static string Run() => new Box().Grow(2).ToString();
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "var grown", "+ local;", name));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
    }

    [Fact]
    public async Task SwitchSection_StatementsReplacedInTheSection()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run() => Name(1) + Name(2);

                private static string Name(int code)
                {
                    switch (code)
                    {
                        case 1:
                            var one = "one";
                            return one.ToUpperInvariant();
                        default:
                            return "other";
                    }
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var one", "ToUpperInvariant();", "Upper", expected: "ONEother");

        Assert.Contains("case 1:\n                return Upper();", text);
    }

    [Fact]
    public async Task EmbeddedStatementNeedingTwoStatements_BecomesABlock()
    {
        using var ws = Create("""
            using System.Collections.Generic;

            public static class Program
            {
                public static string Run()
                {
                    var log = new List<string>();
                    Check(log, 1);
                    return string.Join(",", log);
                }

                private static void Check(List<string> log, int x)
                {
                    if (x < 0)
                        throw new System.ArgumentException("negative");
                    log.Add("ok");
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "throw new", "(\"negative\");", "Fail", expected: "ok");

        Assert.Contains("if (x < 0)\n        {\n            Fail();\n            return;\n        }", text);
    }

    [Fact]
    public async Task ExpressionBodiedProperty_InstanceMethod()
    {
        using var ws = Create("""
            public class Rect
            {
                public int W = 2;
                public int H = 3;

                public int Area => W * H;
            }

            public static class Program
            {
                public static string Run() => new Rect().Area.ToString();
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "W * H", "W * H", "Multiply", expected: "6");

        Assert.Contains("public int Area => Multiply();", text);
        Assert.Contains("private int Multiply()", text);
    }

    [Fact]
    public async Task PrimaryConstructorParameter_UsedDirectlyByAnInstanceMethod()
    {
        using var ws = Create("""
            public class Scaler(int factor)
            {
                public int Scale(int x)
                {
                    var scaled = x * factor;
                    return scaled;
                }
            }

            public static class Program
            {
                public static string Run() => new Scaler(3).Scale(5).ToString();
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var scaled", "* factor;", "Multiply", expected: "15");

        Assert.Contains("var scaled = Multiply(x);", text);
        Assert.Contains("private int Multiply(int x)", text);
    }

    [Fact]
    public async Task LambdaCapturingThis_InstanceMethod()
    {
        using var ws = Create("""
            public class Clicker
            {
                private int _clicks;

                public System.Func<int> Make()
                {
                    System.Func<int> click = () => ++_clicks;
                    return click;
                }
            }

            public static class Program
            {
                public static string Run()
                {
                    var click = new Clicker().Make();
                    click();
                    return click().ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "System.Func<int> click", "++_clicks;", "CreateClick", expected: "2");

        Assert.Contains("private System.Func<int> CreateClick()", text);
    }

    [Fact]
    public async Task RefLocal_PassedByRef()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var numbers = new[] { 1, 2, 3 };
                    ref var first = ref numbers[0];
                    first = 10;
                    first += 5;
                    return numbers[0].ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "first = 10;", "first += 5;", "Set", expected: "15");

        Assert.Contains("Set(ref first);", text);
        Assert.Contains("private static void Set(ref int first)", text);
    }

    [Fact]
    public async Task InParameter_StaysIn()
    {
        using var ws = Create("""
            public struct Pair
            {
                public int A;
                public int B;
            }

            public static class Program
            {
                public static string Run() => Sum(new Pair { A = 2, B = 3 }).ToString();

                private static int Sum(in Pair p)
                {
                    var total = p.A + p.B;
                    return total;
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var total", "p.B;", "Add", expected: "5");

        Assert.Contains("var total = Add(p);", text);
        Assert.Contains("private static int Add(in Pair p)", text);
    }

    [Fact]
    public async Task DeclaredInSelectionAndOverwrittenAfter_DeclaredAtTheCall()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var a = 1;
                    var b = a + 1;
                    System.Console.Write(b);
                    b = 7;
                    return b.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "var b", "Console.Write(b);", "Show", expected: "7");

        Assert.Contains("int b;\n        Show(a);", text);
    }

    [Fact]
    public async Task ConstantConvertedByItsTarget_ReturnsTheTargetType()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    byte small = 200;
                    return small.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "200", "200", "Limit", expected: "200");

        Assert.Contains("byte small = Limit();", text);
        Assert.Contains("private static byte Limit()", text);
    }

    [Fact]
    public async Task SelectionStartingAndEndingInsideStatements_SelectsThem()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    var items = new[] { 3, 1, 4 };
                    var count = items.Length;
                    var first = items[0];
                    return $"{count}/{first}";
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "= items.Length", "var first", "Measure", expected: "3/3");

        Assert.Contains("var (count, first) = Measure(items);", text);
    }

    [Fact]
    public async Task UsingDeclarationWithCodeAfterIt_IsRefused()
    {
        using var ws = Create("""
            public static class Program
            {
                public static string Run()
                {
                    using var stream = new System.IO.MemoryStream();
                    stream.WriteByte(1);
                    return stream.Length.ToString();
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "using var", "WriteByte(1);", "Open"));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("'using'", ex.Message);
    }

    [Theory]
    [InlineData("System.Console")]
    [InlineData("var x")]
    public async Task TopLevelStatements_AreRefused(string from)
    {
        using var ws = Create("""
            var x = 1;
            System.Console.WriteLine(x);
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, from, "(x);", "Print"));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("Top-level", ex.Message);
    }

    [Fact]
    public async Task AwaitExpression_AwaitedCallWithoutParentheses()
    {
        using var ws = Create("""
            using System.Threading.Tasks;

            public static class Program
            {
                public static string Run() => RunAsync().GetAwaiter().GetResult();

                private static Task<int> LoadAsync(int x) => Task.FromResult(x + 1);

                private static async Task<string> RunAsync()
                {
                    var x = 4;
                    var loaded = await LoadAsync(x) * 2;
                    return loaded.ToString();
                }
            }
            """);

        var text = await ExtractAndCompareAsync(ws, "await LoadAsync(x)", "LoadAsync(x)", "LoadNextAsync", expected: "10");

        Assert.Contains("var loaded = await LoadNextAsync(x) * 2;", text);
        Assert.Contains("private static async Task<int> LoadNextAsync(int x)", text);
        Assert.Contains("return await LoadAsync(x);", text);
    }

    [Fact]
    public async Task ReadOnlyFieldAssignedInConstructor_IsRefused()
    {
        using var ws = Create("""
            public class Person
            {
                private readonly string _name;

                public Person(string name)
                {
                    var trimmed = name.Trim();
                    _name = trimmed;
                }

                public string Name => _name;
            }

            public static class Program
            {
                public static string Run() => new Person(" Ada ").Name;
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ExtractAsync(ws, "var trimmed", "_name = trimmed;", "Assign"));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("'_name'", ex.Message);
    }

    [Fact]
    public async Task Preview_ShowsCallAndMethod_WritesNothing()
    {
        const string source = """
            public static class Program
            {
                public static string Run()
                {
                    var a = 1;
                    var b = a + 1;
                    return b.ToString();
                }
            }
            """;
        using var ws = Create(source);

        var result = await ExtractAsync(ws, "var b", "a + 1;", "Next", preview: true);

        Assert.True(result.Success, result.Error?.Message);
        var change = Assert.Single(result.PendingChanges!);
        Assert.Contains("var b = Next(a);", change.AfterSnippet);
        Assert.Contains("private static int Next(int a)", change.AfterSnippet);
        Assert.Null(result.Warnings);
        Assert.Equal(Lf(source), ws.ReadFile("Program.cs"));
    }

    private static TestWorkspace Create(string source) => TestWorkspace.Create(("Program.cs", Lf(source)));

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    /// <summary>Runs the program, extracts, checks that the result compiles and runs the same, and returns the new text.</summary>
    private static async Task<string> ExtractAndCompareAsync(
        TestWorkspace ws, string from, string to, string name, string? expected = null)
    {
        var before = Run(ws);
        if (expected != null)
            Assert.Equal(expected, before);

        var result = await ExtractAsync(ws, from, to, name);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
        Assert.Equal(before, Run(ws));
        return ws.ReadFile("Program.cs");
    }

    private static Task<RefactoringResult> ExtractAsync(
        TestWorkspace ws, string from, string to, string name, bool? makeStatic = null, bool preview = false)
    {
        var text = ws.ReadFile("Program.cs");
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found");
        var end = text.IndexOf(to, start, StringComparison.Ordinal) + to.Length;
        var (startLine, startColumn) = Position(text, start);
        var (endLine, endColumn) = Position(text, end);

        return new ExtractMethodOperation(ws.Context).ExecuteAsync(
            new ExtractMethodParams
            {
                SourceFile = ws.PathOf("Program.cs"),
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn,
                MethodName = name,
                MakeStatic = makeStatic,
                Preview = preview
            },
            Ct);
    }

    private static (int Line, int Column) Position(string text, int offset)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset == 0)
            lineStart = 0;
        return (text[..lineStart].Count(c => c == '\n') + 1, offset - lineStart + 1);
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(ExtractMethodFlowTests), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(ws.EmitDiskAssembly()));
            return (string)assembly.GetType("Program", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }
}
