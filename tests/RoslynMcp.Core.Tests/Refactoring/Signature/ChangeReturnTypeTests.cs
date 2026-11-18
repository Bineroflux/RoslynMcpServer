using System.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// change_return_type changes the declared type of a method, property or indexer on its whole
/// override / implementation family, and reports what stops compiling instead of rewriting it.
/// </summary>
public sealed class ChangeReturnTypeTests
{
    private const string Library = """
        namespace Lib;

        public interface ICounter
        {
            int Count();
        }

        public abstract class CounterBase : ICounter
        {
            public abstract int Count();
        }

        public class Fixed : CounterBase
        {
            public override int Count() => 3;
        }

        public class Explicit : ICounter
        {
            int ICounter.Count() { return 4; }
        }
        """;

    private const string Application = """
        using Lib;

        namespace App;

        public class Doubled : Fixed
        {
            public override int Count() => base.Count() * 2;
        }

        public static class Use
        {
            public static int Sum(ICounter counter, Doubled doubled)
            {
                int total = counter.Count();
                return total + doubled.Count();
            }
        }
        """;

    [Fact]
    public async Task ChangesEveryMemberOfTheFamily_AcrossProjects_AndReportsTheBrokenCallers()
    {
        using var ws = CreateCounters();

        var result = await ChangeAsync(ws, ws.PathOf("Lib", "Counters.cs"), "Count", "long", line: 10);

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Counters.cs"));
        var app = File.ReadAllText(ws.PathOf("App", "Use.cs"));
        Assert.Contains("    long Count();", lib);
        Assert.Contains("public abstract long Count();", lib);
        Assert.Contains("public override long Count() => 3;", lib);
        Assert.Contains("long ICounter.Count() { return 4; }", lib);
        Assert.Contains("public override long Count() => base.Count() * 2;", app);

        // Widening returns still compile; the int callers do not.
        var warnings = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Warnings);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.StartsWith(ws.PathOf("App", "Use.cs") + "(", w));
        Assert.Contains(warnings, w => w.Contains("(14,", StringComparison.Ordinal) && w.Contains("error CS0266", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("(15,", StringComparison.Ordinal) && w.Contains("error CS0266", StringComparison.Ordinal));
        AssertWarningsAreTheCompilerErrors(ws, warnings);
    }

    [Fact]
    public async Task Preview_ReportsDeclarationsAndWarnings_AndWritesNothing()
    {
        using var ws = CreateCounters();
        var before = File.ReadAllText(ws.PathOf("App", "Use.cs"));

        var result = await ChangeAsync(ws, ws.PathOf("App", "Use.cs"), "Count", "string", line: 7, preview: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
        Assert.Equal(before, File.ReadAllText(ws.PathOf("App", "Use.cs")));
        Assert.Equal(5, result.PendingChanges!.Count);
        Assert.Contains(result.PendingChanges!, c => c.BeforeSnippet == "int Count" && c.AfterSnippet == "string Count");
        Assert.Contains(result.Warnings!, w => w.Contains("error CS0029", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoidToValue_AndValueToVoid_AreAllowedWithWarnings()
    {
        const string source = """
            namespace App;

            public class Logger
            {
                public virtual void Write(string text) { System.Console.WriteLine(text); }
                public int Level() { return 1; }
                public int Use() { Write("x"); return Level(); }
            }

            public class Quiet : Logger
            {
                public override void Write(string text) { }
            }
            """;
        using var ws = TestWorkspace.Create(("Logger.cs", source));

        var toValue = await ChangeAsync(ws, ws.PathOf("Logger.cs"), "Write", "bool", line: 5);

        Assert.True(toValue.Success, toValue.Error?.Message);
        Assert.Contains("public virtual bool Write(string text)", ws.ReadFile("Logger.cs"));
        Assert.Contains("public override bool Write(string text) { }", ws.ReadFile("Logger.cs"));
        Assert.Equal(2, toValue.Warnings!.Count(w => w.Contains("error CS0161", StringComparison.Ordinal)));

        var toVoid = await ChangeAsync(ws, ws.PathOf("Logger.cs"), "Level", "void");

        Assert.True(toVoid.Success, toVoid.Error?.Message);
        Assert.Contains("public void Level() { return 1; }", ws.ReadFile("Logger.cs"));
        Assert.Contains(toVoid.Warnings!, w => w.Contains("error CS0127", StringComparison.Ordinal));
        Assert.Contains(toVoid.Warnings!, w => w.Contains("(7,", StringComparison.Ordinal));
        AssertWarningsAreTheCompilerErrors(ws, toValue.Warnings!.Concat(toVoid.Warnings!).ToList());
    }

    [Fact]
    public async Task PropertyAndIndexer_ChangeWithTheirImplementations()
    {
        const string source = """
            namespace App;

            public interface INamed
            {
                string Name { get; }
                int this[int index] { get; }
            }

            public class Person : INamed
            {
                public string Name { get; init; } = "";
                string INamed.Name => Name;
                public int this[int index] => index;
            }
            """;
        using var ws = TestWorkspace.Create(("Person.cs", source));

        var property = await ChangeAsync(ws, ws.PathOf("Person.cs"), "Name", "object", line: 5);
        var indexer = await ChangeAsync(ws, ws.PathOf("Person.cs"), "this", "long", line: 6);

        Assert.True(property.Success, property.Error?.Message);
        Assert.True(indexer.Success, indexer.Error?.Message);
        var text = ws.ReadFile("Person.cs");
        Assert.Contains("    object Name { get; }", text);
        Assert.Contains("    long this[int index] { get; }", text);
        Assert.Contains("object INamed.Name => Name;", text);
        Assert.Contains("public long this[int index] => index;", text);
        // The implicit implementation of Name is the class's own property, which keeps its type.
        Assert.Contains("public string Name { get; init; }", text);
        Assert.Null(property.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task VoidProperty_IsRefused()
    {
        const string source = """
            namespace App;
            public class Person { public string Name { get; set; } = ""; }
            """;
        using var ws = TestWorkspace.Create(("Person.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, ws.PathOf("Person.cs"), "Name", "void"));

        Assert.Equal(ErrorCodes.InvalidReturnType, ex.ErrorCode);
        Assert.Equal(source, ws.ReadFile("Person.cs"));
    }

    [Theory]
    [InlineData("Missing", "not a type that resolves")]
    [InlineData("int", "already has type")]
    [InlineData("List<", "not a type that resolves")]
    public async Task UnresolvableOrUnchangedType_IsRefused(string newType, string message)
    {
        const string source = """
            namespace App;
            public class Box { public int Size() => 1; }
            """;
        using var ws = TestWorkspace.Create(("Box.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, ws.PathOf("Box.cs"), "Size", newType));

        Assert.Equal(ErrorCodes.InvalidReturnType, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("""
        namespace App;
        public class Widget
        {
            public override string ToString() => "w";
        }
        """, "ToString", "object.ToString()")]
    [InlineData("""
        namespace App;
        public class Widget : System.IComparable<Widget>
        {
            public int CompareTo(Widget? other) => 0;
        }
        """, "CompareTo", "System.IComparable<T>.CompareTo(T?)")]
    public async Task MemberOfAFamilyDeclaredInMetadata_IsRefused(string source, string member, string metadataMember)
    {
        using var ws = TestWorkspace.Create(("Widget.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, ws.PathOf("Widget.cs"), member, "object"));

        Assert.Equal(ErrorCodes.FamilyMemberNotInSource, ex.ErrorCode);
        Assert.Contains($"'{metadataMember}'", ex.Message);
        Assert.Equal(source, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task GenericBase_GivesEachOverrideTheTypeItSees()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace App;

            public class User { }

            public abstract class Repository<T>
            {
                public abstract T Get(int id);
            }

            public class UserRepository : Repository<User>
            {
                public override User Get(int id) => new User();
            }
            """;
        using var ws = TestWorkspace.Create(("Repo.cs", source));

        var result = await ChangeAsync(ws, ws.PathOf("Repo.cs"), "Get", "Task<T>", line: 9);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Repo.cs");
        Assert.Contains("public abstract Task<T> Get(int id);", text);
        Assert.Contains("public override Task<User> Get(int id) => new User();", text);
        AssertWarningsAreTheCompilerErrors(ws, result.Warnings!);
    }

    [Fact]
    public async Task NullableContext_IsRespected_AndFilesKeepEncodingAndLineEndings()
    {
        var contract = """
            namespace App;

            public interface ILookup
            {
                string Find(int id);
            }
            """.ReplaceLineEndings("\n");
        var legacy = """
            #nullable disable
            namespace App;

            public class Lookup : ILookup
            {
                public string Find(int id) => null;
            }
            """.ReplaceLineEndings("\r\n");
        using var ws = TestWorkspace.Create(("ILookup.cs", contract), ("Lookup.cs", legacy));
        File.WriteAllText(ws.PathOf("Lookup.cs"), legacy, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await ChangeAsync(ws, ws.PathOf("ILookup.cs"), "Find", "string?");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(contract.Replace("string Find", "string? Find"), ws.ReadFile("ILookup.cs"));
        Assert.Equal(legacy, ws.ReadFile("Lookup.cs"));
        var contractBytes = File.ReadAllBytes(ws.PathOf("ILookup.cs"));
        var legacyBytes = File.ReadAllBytes(ws.PathOf("Lookup.cs"));
        Assert.False(contractBytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "A byte order mark was added.");
        Assert.DoesNotContain((byte)'\r', contractBytes);
        Assert.True(legacyBytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "The byte order mark was dropped.");
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task RefReturn_StaysRef_AndAnAliasIsNotCopiedIntoOtherFiles()
    {
        const string store = """
            using Text = System.String;

            namespace App;

            public class Store
            {
                private object _value = "";
                public virtual ref object Value() => ref _value;
            }
            """;
        const string derived = """
            namespace App;

            public class Derived : Store
            {
                public override ref object Value() => ref base.Value();
            }
            """;
        using var ws = TestWorkspace.Create(("Store.cs", store), ("Derived.cs", derived));

        var result = await ChangeAsync(ws, ws.PathOf("Store.cs"), "Value", "Text");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Matches(@"public virtual ref (Text|string) Value\(\)", ws.ReadFile("Store.cs"));
        Assert.Contains("public override ref string Value() => ref base.Value();", ws.ReadFile("Derived.cs"));
        AssertWarningsAreTheCompilerErrors(ws, result.Warnings!);
    }

    private static void AssertWarningsAreTheCompilerErrors(TestWorkspace ws, IReadOnlyList<string> warnings) =>
        WarningAssert.AreTheCompilerErrors(ws, warnings);

    private static TestWorkspace CreateCounters() => TestWorkspace.CreateSolution(
        new TestWorkspace.ProjectSpec("Lib", [("Counters.cs", Library)]),
        new TestWorkspace.ProjectSpec("App", [("Use.cs", Application)], References: ["Lib"]));

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws,
        string file,
        string member,
        string newType,
        int? line = null,
        bool preview = false) =>
        new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams
            {
                SourceFile = file,
                MemberName = member,
                Line = line,
                NewType = newType,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
