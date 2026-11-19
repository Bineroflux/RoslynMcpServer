using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Generated members only use nullable reference annotations where they are enabled.
/// Stubs for members declared in annotated code (<c>object.Equals(object?)</c>, an
/// annotated interface) used to carry their <c>?</c> into nullable-disabled code, where
/// each one is warning CS8632.
/// </summary>
public sealed class NullableContextTests
{
    private const string AnnotatedInterface = """
        namespace App;

        public interface IStore
        {
            string? Find(string? key);
            event System.EventHandler<string?>? Changed;
        }
        """;

    [Fact]
    public async Task GenerateOverrides_NullableDisabled_EmitsNoAnnotations()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", """
            #nullable disable
            namespace App;

            public class Dog
            {
            }
            """));

        var result = await new GenerateOverridesOperation(ws.Context).ExecuteAsync(
            new GenerateOverridesParams { SourceFile = ws.PathOf("Dog.cs"), TypeName = "Dog" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Dog.cs");
        Assert.Contains("public override bool Equals(object obj)", text);
        Assert.Contains("public override string ToString()", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementInterface_NullableDisabled_EmitsNoAnnotations(bool explicitImplementation)
    {
        using var ws = TestWorkspace.Create(("IStore.cs", AnnotatedInterface), ("Store.cs", """
            #nullable disable
            using System;

            namespace App;

            public class Store : IStore
            {
            }
            """));

        var result = await ImplementAsync(ws, explicitImplementation);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Store.cs");
        Assert.Contains("string key", text);
        Assert.DoesNotContain("?", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task ImplementInterface_NullableEnabled_KeepsAnnotations()
    {
        using var ws = TestWorkspace.Create(("IStore.cs", AnnotatedInterface), ("Store.cs", """
            using System;

            namespace App;

            public class Store : IStore
            {
            }
            """));

        var result = await ImplementAsync(ws, explicitImplementation: false);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Store.cs");
        Assert.Contains("public string? Find(string? key)", text);
        Assert.Contains("EventHandler<string?>? Changed", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("#nullable disable", "string name = store.Find(\"a\");", "System.Collections.Generic.List<string> names = store.All();")]
    [InlineData("#nullable enable", "string? name = store.Find(\"a\");", "System.Collections.Generic.List<string?> names = store.All();")]
    public async Task ExtractVariable_WritesAnnotationsOnlyWhereEnabled(string context, string name, string names)
    {
        using var ws = TestWorkspace.Create(
            ("Store.cs", """
                namespace App;

                public class Store
                {
                    public string? Find(string key) => key;
                    public System.Collections.Generic.List<string?> All() => new();
                }
                """),
            ("Use.cs", $$"""
                {{context}}
                namespace App;

                public static class Use
                {
                    public static int Run(Store store)
                    {
                        return store.Find("a")?.Length ?? store.All().Count;
                    }
                }
                """));

        foreach (var (expression, variable) in new[] { ("store.Find(\"a\")", "name"), ("store.All()", "names") })
        {
            var lines = ws.ReadFile("Use.cs").ReplaceLineEndings("\n").Split('\n');
            var line = Array.FindIndex(lines, l => l.Contains(expression, StringComparison.Ordinal));
            var column = lines[line].IndexOf(expression, StringComparison.Ordinal) + 1;
            var result = await new ExtractVariableOperation(ws.Context).ExecuteAsync(
                new ExtractVariableParams
                {
                    SourceFile = ws.PathOf("Use.cs"),
                    StartLine = line + 1,
                    StartColumn = column,
                    EndLine = line + 1,
                    EndColumn = column + expression.Length,
                    VariableName = variable,
                    UseVar = false
                },
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
        }

        var text = ws.ReadFile("Use.cs");
        Assert.Contains(name, text);
        Assert.Contains(names, text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    private const string AnnotatedStore = """
        namespace App;

        public static class Store
        {
            public static string? Find(string key) => key;
            public static System.Threading.Tasks.Task<int> LoadAsync() => System.Threading.Tasks.Task.FromResult(1);
        }
        """;

    [Theory]
    [InlineData("#nullable disable", "public static int Run(string name, List<int> list)")]
    [InlineData("#nullable enable", "public static int Run(string? name, List<int> list)")]
    public async Task IntroduceParameter_VarLocal_WritesTheTypeLikeGeneratedCode(string context, string signature)
    {
        using var ws = TestWorkspace.Create(("Store.cs", AnnotatedStore), ("Use.cs", $$"""
            {{context}}
            using System.Collections.Generic;

            namespace App;

            public static class Use
            {
                public static int Run()
                {
                    var name = Store.Find("a");
                    var list = new List<int>();
                    return (name ?? "").Length + list.Count;
                }
            }
            """));

        foreach (var (variable, line) in new[] { ("name", 10), ("list", 10) })
        {
            var result = await new IntroduceParameterOperation(ws.Context).ExecuteAsync(
                new IntroduceParameterParams { SourceFile = ws.PathOf("Use.cs"), VariableName = variable, Line = line },
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
        }

        Assert.Contains(signature, ws.ReadFile("Use.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("#nullable disable", "public Order(string note, int id)")]
    [InlineData("#nullable enable", "public Order(string? note, int id)")]
    public async Task GenerateConstructor_MemberFromAnnotatedPart_WritesAnnotationsOnlyWhereEnabled(string context, string signature)
    {
        using var ws = TestWorkspace.Create(
            ("Order.Note.cs", """
                #nullable enable
                namespace App;

                public partial class Order
                {
                    private readonly string? note;

                    public string? Note => note;
                }
                """),
            ("Order.cs", $$"""
                {{context}}
                namespace App;

                public partial class Order
                {
                    private readonly int id;

                    public int Id => id;
                }
                """));

        var result = await new GenerateConstructorOperation(ws.Context).ExecuteAsync(
            new GenerateConstructorParams { SourceFile = ws.PathOf("Order.cs"), TypeName = "Order" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(signature, ws.ReadFile("Order.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("#nullable disable", "private static void Print(string name)")]
    [InlineData("#nullable enable", "private static void Print(string? name)")]
    public async Task ExtractMethod_WritesAnnotationsOnlyWhereEnabled(string context, string signature)
    {
        using var ws = TestWorkspace.Create(("Store.cs", AnnotatedStore), ("Use.cs", $$"""
            {{context}}
            namespace App;

            public static class Use
            {
                public static void Run()
                {
                    var name = Store.Find("a");
                    System.Console.WriteLine(name);
                }
            }
            """));

        var result = await ExtractMethodAsync(ws, "System.Console.WriteLine(name);", "Print");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(signature, ws.ReadFile("Use.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task ExtractMethod_AsyncWithoutTasksUsing_QualifiesTask()
    {
        using var ws = TestWorkspace.Create(("Store.cs", AnnotatedStore), ("Use.cs", """
            namespace App;

            public static class Use
            {
                public static async System.Threading.Tasks.Task RunAsync()
                {
                    System.Console.WriteLine(await Store.LoadAsync());
                }
            }
            """));

        var result = await ExtractMethodAsync(ws, "System.Console.WriteLine(await Store.LoadAsync());", "PrintAsync");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Use.cs");
        Assert.Contains("private static async System.Threading.Tasks.Task PrintAsync()", text);
        Assert.Contains("await PrintAsync();", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    private static Task<RefactoringResult> ExtractMethodAsync(TestWorkspace ws, string statement, string name)
    {
        var lines = ws.ReadFile("Use.cs").ReplaceLineEndings("\n").Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains(statement, StringComparison.Ordinal));
        var column = lines[line].IndexOf(statement, StringComparison.Ordinal) + 1;
        return new ExtractMethodOperation(ws.Context).ExecuteAsync(
            new ExtractMethodParams
            {
                SourceFile = ws.PathOf("Use.cs"),
                StartLine = line + 1,
                StartColumn = column,
                EndLine = line + 1,
                EndColumn = column + statement.Length,
                MethodName = name
            },
            TestContext.Current.CancellationToken);
    }

    private static Task<RefactoringResult> ImplementAsync(TestWorkspace ws, bool explicitImplementation) =>
        new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Store.cs"),
                TypeName = "Store",
                InterfaceName = "IStore",
                ExplicitImplementation = explicitImplementation
            },
            TestContext.Current.CancellationToken);
}
