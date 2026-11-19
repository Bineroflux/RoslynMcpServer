using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// change_signature changes the whole override / implementation family of a method and the
/// calls to every member. It used to change only the one method, which never compiles when
/// the method overrides, is overridden by, or implements another.
/// </summary>
public sealed class ChangeSignatureFamilyTests
{
    private const string Library = """
        namespace Lib;

        public interface IShape
        {
            double Area(double scale);
        }

        public abstract class Shape : IShape
        {
            public abstract double Area(double scale);

            public double Twice(double scale) => Area(scale: scale) * 2;
        }

        public class Square : Shape
        {
            public double Side { get; set; }

            /// <summary>The area.</summary>
            /// <param name="factor">Scale factor.</param>
            public override double Area(double factor) => Side * Side * factor;
        }

        public class Ring : IShape
        {
            double IShape.Area(double scale) => scale;
        }
        """;

    private const string Application = """
        using Lib;

        namespace App;

        public class Circle : Square
        {
            public override double Area(double factor) => base.Area(factor) + base.Area(factor: 1);
        }

        public static class Use
        {
            public static double All(IShape shape, Square square, Circle circle) =>
                shape.Area(2) + square.Area(factor: 3) + circle.Area(4);
        }
        """;

    [Fact]
    public async Task AddedOptionalParameter_ReachesEveryMember_AndTheCallsLeaveItOut()
    {
        using var ws = CreateShapes();
        var appBefore = File.ReadAllText(ws.PathOf("App", "Circle.cs"));

        var result = await ChangeAsync(ws, "Area", line: 10, sourceProject: "Lib", Add("rounding", "int", "0"));

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));
        var app = File.ReadAllText(ws.PathOf("App", "Circle.cs"));
        Assert.Contains("double Area(double scale, int rounding = 0);", lib);
        Assert.Contains("public abstract double Area(double scale, int rounding = 0);", lib);
        Assert.Contains("public override double Area(double factor, int rounding = 0) =>", lib);
        // An explicit implementation gets no default value (CS1066).
        Assert.Contains("double IShape.Area(double scale, int rounding) => scale;", lib);
        Assert.Contains("Area(scale: scale) * 2", lib);
        Assert.Equal(appBefore.Replace("Area(double factor)", "Area(double factor, int rounding = 0)"), app);
        Assert.Equal(0, result.ReferencesUpdated);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task AddedRequiredParameter_ReachesEveryMemberAndCall_AcrossProjects()
    {
        using var ws = CreateShapes();

        var result = await ChangeAsync(ws, "Area", line: 10, sourceProject: "Lib", new ParameterChange { Name = "rounding", Type = "int" });

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));
        var app = File.ReadAllText(ws.PathOf("App", "Circle.cs"));
        Assert.Contains("public override double Area(double factor, int rounding) =>", lib);
        Assert.Contains("double IShape.Area(double scale, int rounding) => scale;", lib);
        Assert.Contains("Area(scale: scale, rounding: default /* TODO: rounding */) * 2", lib);
        Assert.Contains("base.Area(factor, default /* TODO: rounding */) + base.Area(factor: 1, rounding: default /* TODO: rounding */)", app);
        Assert.Contains(
            "shape.Area(2, default /* TODO: rounding */) + square.Area(factor: 3, rounding: default /* TODO: rounding */) + circle.Area(4, default /* TODO: rounding */)",
            app);
        Assert.Equal(6, result.ReferencesUpdated);
        Assert.Equal(6, result.Warnings!.Count);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task StartingFromAnOverride_ChangesTheBaseAndInterfaceToo()
    {
        using var ws = CreateShapes();

        var result = await ChangeAsync(ws, "Area", line: 7, sourceProject: "App", Add("unit", "string", "\"m\""));

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));
        Assert.Contains("    double Area(double scale, string unit = \"m\");", lib);
        Assert.Contains("double IShape.Area(double scale, string unit) => scale;", lib);
        Assert.Contains("Area(scale: scale) * 2", lib);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Rename_RenamesThatPositionInEveryMember_WithBodiesDocsAndNamedArguments()
    {
        using var ws = CreateShapes();

        var result = await ChangeAsync(ws, "Area", line: 10, sourceProject: "Lib",
            new ParameterChange { OriginalName = "scale", Name = "multiplier" });

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));
        var app = File.ReadAllText(ws.PathOf("App", "Circle.cs"));
        Assert.Contains("public override double Area(double multiplier) => Side * Side * multiplier;", lib);
        Assert.Contains("<param name=\"multiplier\">Scale factor.</param>", lib);
        Assert.Contains("double IShape.Area(double multiplier) => multiplier;", lib);
        Assert.Contains("Area(multiplier: scale) * 2", lib);
        Assert.Contains("base.Area(multiplier) + base.Area(multiplier: 1)", app);
        Assert.Contains("square.Area(multiplier: 3)", app);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task WithoutRename_EachMemberKeepsItsOwnParameterName()
    {
        using var ws = CreateShapes();

        var result = await ChangeAsync(ws, "Area", line: 10, sourceProject: "Lib",
            Keep("scale"), new ParameterChange { Name = "label", Type = "string?" });

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));
        var app = File.ReadAllText(ws.PathOf("App", "Circle.cs"));
        Assert.Contains("public override double Area(double factor, string? label) => Side * Side * factor;", lib);
        Assert.Contains("square.Area(factor: 3, label: default /* TODO: label */)", app);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Preview_ListsEveryDeclaration_AndWritesNothing()
    {
        using var ws = CreateShapes();
        var before = File.ReadAllText(ws.PathOf("Lib", "Shapes.cs"));

        var result = await ChangeAsync(ws, "Area", line: 10, sourceProject: "Lib", preview: true,
            new ParameterChange { Name = "rounding", Type = "int" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
        Assert.Equal(before, File.ReadAllText(ws.PathOf("Lib", "Shapes.cs")));
        var declarations = result.PendingChanges!.Where(c => c.Description.StartsWith("Change the parameters", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, declarations.Count);
        Assert.Contains(declarations, c => c.BeforeSnippet == "Area(double factor)" && c.AfterSnippet == "Area(double factor, int rounding)");
        Assert.Contains(result.PendingChanges!, c => c.Description == "Update 1 call site" && c.File == ws.PathOf("Lib", "Shapes.cs"));
        Assert.Contains(result.PendingChanges!, c => c.Description == "Update 5 call sites" && c.File == ws.PathOf("App", "Circle.cs"));
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
        public class Widget : System.IDisposable
        {
            public void Dispose() { }
        }
        """, "Dispose", "System.IDisposable.Dispose()")]
    public async Task MemberOfAFamilyDeclaredInMetadata_IsRefused(string source, string method, string metadataMember)
    {
        using var ws = TestWorkspace.Create(("Widget.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Widget.cs"),
                MethodName = method,
                Parameters = [Add("flag", "bool", "false")]
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.FamilyMemberNotInSource, ex.ErrorCode);
        Assert.Contains($"'{metadataMember}'", ex.Message);
        Assert.Equal(source, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task GenericBase_WritesTheTypeAsEachOverrideSeesIt()
    {
        const string source = """
            using System.Collections.Generic;

            namespace App;

            public class User { }

            public abstract class Repository<T>
            {
                public abstract void Save(T item);
            }

            public class UserRepository : Repository<User>
            {
                public override void Save(User item) { }
            }
            """;
        using var ws = TestWorkspace.Create(("Repo.cs", source));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Repo.cs"),
                MethodName = "Save",
                Line = 9,
                Parameters = [Keep("item"), Add("others", "IEnumerable<T>", "null")]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Repo.cs");
        Assert.Contains("public abstract void Save(T item, IEnumerable<T> others = null);", text);
        Assert.Contains("public override void Save(User item, IEnumerable<User> others = null) { }", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task TypeParameterWithoutCounterpartInTheBase_IsRefused()
    {
        const string source = """
            using System.Collections.Generic;

            namespace App;

            public abstract class Base<T>
            {
                public abstract void Put(T item);
            }

            public class Derived<U> : Base<List<U>>
            {
                public override void Put(List<U> item) { }
            }
            """;
        using var ws = TestWorkspace.Create(("Base.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Base.cs"),
                MethodName = "Put",
                Line = 12,
                Parameters = [Keep("item"), Add("extra", "U", "default")]
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.TypeNotExpressible, ex.ErrorCode);
        Assert.Equal(source, ws.ReadFile("Base.cs"));
    }

    [Fact]
    public async Task NullableDisabledFile_GetsNoAnnotations_LfWithoutBomStaysThatWay()
    {
        var contract = """
            namespace App;

            public interface INamer
            {
                string Name(int id);
            }
            """.ReplaceLineEndings("\n");
        var legacy = """
            #nullable disable
            namespace App;

            public class LegacyNamer : INamer
            {
                public string Name(int id) => id.ToString();
            }
            """.ReplaceLineEndings("\n");
        using var ws = TestWorkspace.Create(("INamer.cs", contract), ("LegacyNamer.cs", legacy));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("INamer.cs"),
                MethodName = "Name",
                Parameters = [Keep("id"), Add("prefix", "string?", "null")]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("string Name(int id, string? prefix = null);", ws.ReadFile("INamer.cs"));
        Assert.Contains("public string Name(int id, string prefix = null)", ws.ReadFile("LegacyNamer.cs"));
        foreach (var file in new[] { "INamer.cs", "LegacyNamer.cs" })
        {
            var bytes = File.ReadAllBytes(ws.PathOf(file));
            Assert.False(bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), $"{file}: a byte order mark was added.");
            Assert.DoesNotContain((byte)'\r', bytes);
        }

        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task PartialMethod_ChangesBothParts_AndRecursiveCalls()
    {
        const string source = """
            namespace App;

            public partial class Counter
            {
                public partial int Count(int from);
            }

            public partial class Counter
            {
                public partial int Count(int from) => from <= 0 ? 0 : 1 + Count(from - 1);
            }
            """;
        using var ws = TestWorkspace.Create(("Counter.cs", source));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Counter.cs"),
                MethodName = "Count",
                Line = 5,
                Parameters = [new ParameterChange { OriginalName = "from", Name = "start" }, Add("step", "int", "1")]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Counter.cs");
        Assert.Contains("public partial int Count(int start, int step = 1);", text);
        Assert.Contains("public partial int Count(int start, int step = 1) => start <= 0 ? 0 : 1 + Count(start - 1);", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtensionAndParamsCalls_KeepTheirReceiverAndExtraArguments()
    {
        const string source = """
            namespace App;

            public static class Text
            {
                public static string Join(this string separator, params string[] parts) => string.Join(separator, parts);

                public static string Use() => ",".Join("a", "b") + Join("-", "c") + ";".Join();
            }
            """;
        using var ws = TestWorkspace.Create(("Text.cs", source));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Text.cs"),
                MethodName = "Join",
                Parameters =
                [
                    new ParameterChange { OriginalName = "separator", Name = "separator", NewPosition = 0 },
                    new ParameterChange { Name = "trim", Type = "bool", DefaultValue = "false", NewPosition = 1 },
                    new ParameterChange { OriginalName = "parts", Name = "parts", NewPosition = 2 }
                ]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Text.cs");
        Assert.Contains("public static string Join(this string separator, bool trim = false, params string[] parts)", text);
        // Several expanded params arguments cannot be named, so that call passes the new default;
        // a single one can.
        Assert.Contains("\",\".Join(false, \"a\", \"b\") + Join(\"-\", parts: \"c\") + \";\".Join()", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MultiLineParameterList_KeepsItsLayoutAndLineEndings()
    {
        var source = """
            namespace App;

            public class Mailer
            {
                public void Send(
                    string to,
                    string body)
                {
                }

                public void Use() => Send("a", "b");
            }
            """.ReplaceLineEndings("\r\n");
        using var ws = TestWorkspace.Create(("Mailer.cs", source));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Mailer.cs"),
                MethodName = "Send",
                Parameters = [Keep("to"), Keep("body"), Add("cc", "string?", "null")]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var expected = source
            .Replace("string body)", "string body,\r\n        string? cc = null)");
        Assert.Equal(expected, ws.ReadFile("Mailer.cs"));
    }

    private static TestWorkspace CreateShapes() => TestWorkspace.CreateSolution(
        new TestWorkspace.ProjectSpec("Lib", [("Shapes.cs", Library)]),
        new TestWorkspace.ProjectSpec("App", [("Circle.cs", Application)], References: ["Lib"]));

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws,
        string method,
        int line,
        string sourceProject,
        params ParameterChange[] parameters) =>
        ChangeAsync(ws, method, line, sourceProject, preview: false, parameters);

    private static Task<RefactoringResult> ChangeAsync(
        TestWorkspace ws,
        string method,
        int line,
        string sourceProject,
        bool preview,
        params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf(sourceProject, sourceProject == "Lib" ? "Shapes.cs" : "Circle.cs"),
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
