using System.Runtime.Loader;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Convert;

/// <summary>
/// convert_property ToAutoProperty replaced any accessors with <c>{ get; set; }</c> and left the
/// backing field behind, so a computed getter or a validating setter was lost and every other use
/// of the field went on using a field the property no longer stored into. It now converts only
/// trivial accessors, removes the field and points its other uses at the property.
/// </summary>
public sealed class ConvertToAutoPropertyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task TrivialAccessors_FieldRemoved_UsesGoThroughTheProperty(string newline)
    {
        using var ws = Create(newline, """
            public class Counter
            {
                private int _count = 4;

                public int Count
                {
                    get { return _count; }
                    set { _count = value; }
                }

                public void Bump() => _count++;

                public bool Same(Counter other) => other._count == _count;
            }

            public static class Program
            {
                public static string Run()
                {
                    var counter = new Counter();
                    counter.Bump();
                    counter.Count += 2;
                    return counter.Count + "/" + counter.Same(counter);
                }
            }
            """);
        var before = Run(ws);

        var result = await ConvertAsync(ws, "Count");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        Assert.Equal(3, result.ReferencesUpdated);
        Assert.StartsWith(With(newline, """
            public class Counter
            {
                public int Count { get; set; } = 4;

                public void Bump() => Count++;

                public bool Same(Counter other) => other.Count == Count;
            }
            """), ws.ReadFile("Program.cs"));
        Assert.Equal("7/True", before);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task ExpressionBodiedOverReadonlyField_GetOnly_ConstructorStillAssigns()
    {
        using var ws = Create("\n", """
            public class Person
            {
                private readonly string _name;

                public Person(string name)
                {
                    _name = name;
                }

                public string Name => _name;
            }

            public static class Program
            {
                public static string Run() => new Person("Ada").Name;
            }
            """);
        var before = Run(ws);

        var result = await ConvertAsync(ws, "Name");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Program.cs");
        Assert.StartsWith("public class Person\n{\n    public Person(string name)\n    {\n        Name = name;\n    }\n\n    public string Name { get; }\n}\n", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task GetOnlyOverFieldWrittenInAMethod_GetsAPrivateSetter()
    {
        using var ws = Create("\n", """
            public class Sum
            {
                private int _total;

                public int Total { get => _total; }

                public void Add(int x) => _total += x;
            }

            public static class Program
            {
                public static string Run()
                {
                    var sum = new Sum();
                    sum.Add(3);
                    return sum.Total.ToString();
                }
            }
            """);
        var before = Run(ws);

        var result = await ConvertAsync(ws, "Total");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("    public int Total { get; private set; }\n", text);
        Assert.Contains("    public void Add(int x) => Total += x;\n", text);
        Assert.DoesNotContain("_total", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task FieldDeclaredWithOthers_OnlyItGoes_InitAccessorKept()
    {
        using var ws = Create("\n", """
            public class Box
            {
                private int _width = 1, _height = 2;

                public int Width { get => _width; init => _width = value; }

                public int Area() => _width * _height;
            }

            public static class Program
            {
                public static string Run() => new Box { Width = 5 }.Area().ToString();
            }
            """);
        var before = Run(ws);

        var result = await ConvertAsync(ws, "Width");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("    private int _height = 2;\n\n    public int Width { get; init; } = 1;\n", text);
        Assert.Contains("public int Area() => Width * _height;", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task NameOf_NamesTheProperty_AndIsReported()
    {
        using var ws = Create("\n", """
            public class Item
            {
                private string _label = "";

                public string Label { get { return _label; } set { _label = value; } }

                public string Which() => nameof(_label);
            }
            """);

        var result = await ConvertAsync(ws, "Label");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public string Which() => nameof(Label);", ws.ReadFile("Program.cs"));
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("nameof(_label) now names the property", warning);
    }

    [Theory]
    [InlineData("get { return _x * 2; } set { _x = value; }", "", "computes its value")]
    [InlineData("get { return _x; } set { if (value < 0) throw new System.ArgumentException(); _x = value; }", "", "does more than store value")]
    [InlineData("get { return _x; } set { _x = value; Changed(); }", "", "does more than store value")]
    [InlineData("get { return _x; } set { _x = value; }", "public void Bump() => System.Threading.Interlocked.Increment(ref _x);", "used as a variable")]
    [InlineData("get { return _x; }", "", "")]
    public async Task AccessorsOrUsesAPropertyCannotTakeOver_AreRefused(string accessors, string member, string reason)
    {
        using var ws = Create("\n", $$"""
            public class Thing
            {
                private int _x;

                public int X { {{accessors}} }

                private void Changed() { }

                {{member}}
            }
            """);

        if (reason.Length == 0)
        {
            Assert.True((await ConvertAsync(ws, "X")).Success);
            return;
        }

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, "X"));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("private volatile int _x;", "public int X { get { return _x; } set { _x = value; } }", "", "volatile")]
    [InlineData("internal int _x;", "public int X { get { return _x; } }", "public static class Other { public static void Set(Thing t) => t._x = 1; }", "has no setter")]
    [InlineData("private Point _x;", "public Point X { get { return _x; } set { _x = value; } }", "public void Move() => _x.Left = 1;", "assigns a member")]
    [InlineData("private static int _x = 1;", "public static int X { get { return _x; } }", "private static int Twice = _x * 2;", "Another initializer")]
    public async Task FieldsAPropertyCannotReplace_AreRefused(string field, string property, string other, string reason)
    {
        using var ws = Create("\n", $$"""
            public struct Point
            {
                public int Left;
            }

            public class Thing
            {
                {{field}}

                {{property}}

                {{(other.StartsWith("public static class", StringComparison.Ordinal) ? "" : other)}}
            }

            {{(other.StartsWith("public static class", StringComparison.Ordinal) ? other : "")}}
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, "X"));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(reason, ex.Message);
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;

    private static TestWorkspace Create(string newline, string source) => TestWorkspace.Create(("Program.cs", With(newline, source)));

    private static Task<RefactoringResult> ConvertAsync(TestWorkspace ws, string property) =>
        new ConvertPropertyOperation(ws.Context).ExecuteAsync(
            new ConvertPropertyParams { SourceFile = ws.PathOf("Program.cs"), PropertyName = property, Direction = "ToAutoProperty" },
            Ct);

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(ConvertToAutoPropertyTests), isCollectible: true);
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
