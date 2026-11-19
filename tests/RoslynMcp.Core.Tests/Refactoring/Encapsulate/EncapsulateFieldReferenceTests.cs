using System.Runtime.Loader;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Encapsulate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Encapsulate;

/// <summary>
/// encapsulate_field: code outside the declaring type uses the property, code inside it keeps
/// using the (renamed) field. The rename used to touch only the declaration, a reference in
/// another type of the same file counted as inside, and several references in one document
/// were replaced at spans of an older tree.
/// </summary>
public sealed class EncapsulateFieldReferenceTests
{
    private const string Person = """
        namespace Lib;

        /// <summary>A person; see <see cref="name"/>.</summary>
        public partial class Person
        {
            public string name = "";
            public int Age;

            public Person(int id)
            {
                name = "p" + id;
            }

            public string Label() => name + "/" + this.Age;

            public bool SameName(Person other) => other.name == name;

            public static string Field() => nameof(name);

            public class Nested
            {
                public static string Peek(Person p) => p.name;
            }
        }

        /// <summary>Uses <see cref="Person.name"/>.</summary>
        public class Badge
        {
            public static string For(Person p) => p.name + "#" + p.Age;
        }
        """;

    private const string PersonExtra = """
        namespace Lib;

        public partial class Person
        {
            public void Rename(string value) => name = value;
        }
        """;

    private const string Program = """
        using Lib;

        public static class Program
        {
            public static string Run()
            {
                var person = new Person(7) { name = "Ada", Age = 41 };
                person.name = person.name + "!";
                person.Rename(person.name + person.name.Length);
                _ = nameof(person.name);
                var old = person is { name: "x" } ? "x" : person.name;
                return old + "|" + person.Label() + "|" + Badge.For(person) + "|" + Person.Nested.Peek(person) + "|" + person.SameName(person);
            }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RenamedField_InsideKeepsTheField_OutsideUsesTheProperty()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Lf(Person)), ("Person.Extra.cs", Lf(PersonExtra)), ("Program.cs", Lf(Program)));
        var before = Run(ws);

        var result = await EncapsulateAsync(ws, "Person.cs", "name");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        var person = ws.ReadFile("Person.cs");
        Assert.Contains("private string _name = \"\";\n\n    public string Name\n", person);
        Assert.Contains("_name = \"p\" + id;", person);
        Assert.Contains("public string Label() => _name + \"/\" + this.Age;", person);
        Assert.Contains("other._name == _name", person);
        Assert.Contains("nameof(_name)", person);
        Assert.Contains("<see cref=\"Name\"/>", person);
        Assert.Contains("public static string Peek(Person p) => p._name;", person);
        Assert.Contains("<see cref=\"Person.Name\"/>", person);
        Assert.Contains("public static string For(Person p) => p.Name + \"#\" + p.Age;", person);
        Assert.Contains("public void Rename(string value) => _name = value;", ws.ReadFile("Person.Extra.cs"));
        var program = ws.ReadFile("Program.cs");
        Assert.Contains("new Person(7) { Name = \"Ada\", Age = 41 };", program);
        Assert.Contains("person.Name = person.Name + \"!\";", program);
        Assert.Contains("person.Rename(person.Name + person.Name.Length);", program);
        Assert.Contains("_ = nameof(person.Name);", program);
        Assert.Contains("person is { Name: \"x\" } ? \"x\" : person.Name;", program);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task StaticReadOnlyAndSharedDeclarationFields()
    {
        using var ws = TestWorkspace.Create(("Counter.cs", Lf("""
            public class Counter
            {
                public static int created;
                public readonly int Id;
                public int first = 1, a = 2, last = 3;

                public Counter(int id)
                {
                    Id = id;
                    created++;
                }

                public int Sum() => first + a + last + Id;
            }

            public static class Program
            {
                public static string Run()
                {
                    var counter = new Counter(5);
                    Counter.created += 10;
                    counter.a = counter.a * 10;
                    return Counter.created + "|" + counter.Id + "|" + counter.Sum();
                }
            }
            """)));
        var before = Run(ws);

        var created = await EncapsulateAsync(ws, "Counter.cs", "created");
        var id = await EncapsulateAsync(ws, "Counter.cs", "Id");
        var a = await EncapsulateAsync(ws, "Counter.cs", "a");

        Assert.True(created.Success && id.Success && a.Success);
        Assert.Null(created.Warnings ?? id.Warnings ?? a.Warnings);
        var text = ws.ReadFile("Counter.cs");
        Assert.Contains("private static int _created;", text);
        Assert.Contains("public static int Created\n", text);
        Assert.Contains("_created++;", text);
        Assert.Contains("Counter.Created += 10;", text);
        Assert.Contains("private readonly int _id;", text);
        Assert.Contains("public int Id\n    {\n        get\n        {\n            return _id;\n        }\n    }", text);
        Assert.Contains("_id = id;", text);
        Assert.Contains("counter.Id", text);
        Assert.Contains("public int first = 1;\n    private int _a = 2;\n    public int last = 3;\n", text);
        Assert.Contains("public int Sum() => first + _a + last + _id;", text);
        Assert.Contains("counter.A = counter.A * 10;", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task SharedDeclaration_PartsKeepTheirLayout()
    {
        using var ws = TestWorkspace.Create(("Counter.cs", Lf("""
            public class Counter
            {
                public int first=1, second = 2, a = 3, last = 4; // counts

                public int Sum() => first + second + a + last;
            }
            """)));

        var result = await EncapsulateAsync(ws, "Counter.cs", "a");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("""
                public int first=1, second = 2;
                private int _a = 3;
                public int last = 4; // counts

                public int A
            """.ReplaceLineEndings("\n"), ws.ReadFile("Counter.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task UsesThatNeedTheField_StayAndAreReported()
    {
        using var ws = TestWorkspace.Create(("Holder.cs", Lf("""
            public struct Point
            {
                public int X;

                public void Shift() => X++;
            }

            public class Holder
            {
                public int count;
                public Point point;
            }

            public static class Use
            {
                public static void Run(Holder holder)
                {
                    System.Threading.Interlocked.Increment(ref holder.count);
                    holder.point.X = 3;
                    holder.point.Shift();
                }
            }
            """)));

        var count = await EncapsulateAsync(ws, "Holder.cs", "count");
        var point = await EncapsulateAsync(ws, "Holder.cs", "point");

        Assert.True(count.Success && point.Success);
        Assert.Contains(count.Warnings!, w => w.Contains("Holder.cs(18,", StringComparison.Ordinal) && w.Contains("by reference", StringComparison.Ordinal));
        Assert.Contains(count.Warnings!, w => w.Contains("CS0122", StringComparison.Ordinal));
        Assert.Contains(point.Warnings!, w => w.Contains("'holder.point.X = 3'", StringComparison.Ordinal) && w.Contains("assigns a member", StringComparison.Ordinal));
        Assert.Contains(point.Warnings!, w => w.Contains("'Shift'", StringComparison.Ordinal) && w.Contains("copy", StringComparison.Ordinal));
        var text = ws.ReadFile("Holder.cs");
        Assert.Contains("Increment(ref holder._count);", text);
        Assert.Contains("holder._point.X = 3;", text);
        Assert.Contains("holder.Point.Shift();", text);
    }

    [Theory]
    [InlineData("protected internal int _count;", "protected internal int Count")]
    [InlineData("private Secret _secret = new();", "internal Secret Secret")]
    [InlineData("protected Secret _secret = new();", "private protected Secret Secret")]
    public async Task PropertyAccessibility_FieldsOwnButNoMoreVisibleThanItsType(string field, string property)
    {
        using var ws = TestWorkspace.Create(("Box.cs", Lf($$"""
            internal class Secret
            {
            }

            public class Box
            {
                {{field}}
            }
            """)));

        var result = await EncapsulateAsync(ws, "Box.cs", field.Contains("_count") ? "_count" : "_secret");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(property, ws.ReadFile("Box.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ReadOnlyRequested_WritesOutsideAreReported()
    {
        using var ws = TestWorkspace.Create(("Box.cs", Lf("""
            public class Box
            {
                public int size;
            }

            public static class Use
            {
                public static void Grow(Box box) => box.size = 3;
            }
            """)));

        var result = await new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
            new EncapsulateFieldParams { SourceFile = ws.PathOf("Box.cs"), FieldName = "size", ReadOnly = true },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Warnings!, w => w.Contains("CS0200", StringComparison.Ordinal));
        Assert.Contains("box.Size = 3", ws.ReadFile("Box.cs"));
    }

    [Fact]
    public async Task Preview_DescribesTheChange_WritesNothing()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Lf(Person)), ("Person.Extra.cs", Lf(PersonExtra)), ("Program.cs", Lf(Program)));

        var result = await new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
            new EncapsulateFieldParams { SourceFile = ws.PathOf("Person.cs"), FieldName = "name", Preview = true },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("private string _name", result.PendingChanges![0].AfterSnippet);
        Assert.Contains("public string Name", result.PendingChanges[0].AfterSnippet);
        Assert.Contains(result.PendingChanges, c => c.File.EndsWith("Program.cs", StringComparison.Ordinal));
        Assert.Equal(Lf(Person), ws.ReadFile("Person.cs"));
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private static Task<RefactoringResult> EncapsulateAsync(TestWorkspace ws, string file, string field) =>
        new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
            new EncapsulateFieldParams { SourceFile = ws.PathOf(file), FieldName = field },
            Ct);

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(EncapsulateFieldReferenceTests), isCollectible: true);
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
