using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Hierarchy;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Generated stubs and signatures compile in a project without implicit usings, and with
/// members and parameters whose names are keywords. <c>NotImplementedException</c> and
/// <c>ArgumentNullException</c> were bare names that need <c>using System;</c>; a parameter
/// declared as <c>@event</c> came out as <c>int event</c>.
/// </summary>
public sealed class GeneratedKeywordNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Handler = """
        namespace App;

        public interface IHandler
        {
            int @class { get; }
            void Handle(int @event, string @params);
            T Echo<T>(T @in);
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementInterface_NoUsingSystem_QualifiesTheExceptionAndEscapesNames(bool explicitImplementation)
    {
        using var ws = TestWorkspace.Create(("IHandler.cs", Handler), ("Handler.cs", """
            namespace App;

            public class Handler : IHandler
            {
            }
            """));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Handler.cs"),
                TypeName = "Handler",
                InterfaceName = "IHandler",
                ExplicitImplementation = explicitImplementation
            },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Handler.cs");
        Assert.Contains("throw new System.NotImplementedException();", text);
        Assert.Contains(explicitImplementation ? "int IHandler.@class" : "public int @class", text);
        Assert.Contains("Handle(int @event, string @params)", text);
        Assert.Contains("Echo<T>(T @in)", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task ImplementInterface_WithUsingSystem_WritesTheShortName()
    {
        using var ws = TestWorkspace.Create(("IHandler.cs", Handler), ("Handler.cs", """
            using System;

            namespace App;

            public class Handler : IHandler
            {
            }
            """));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams { SourceFile = ws.PathOf("Handler.cs"), TypeName = "Handler", InterfaceName = "IHandler" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Handler.cs");
        Assert.Contains("throw new NotImplementedException();", text);
        Assert.DoesNotContain("System.NotImplementedException", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task GenerateOverrides_KeywordParameters_AreEscaped()
    {
        using var ws = TestWorkspace.Create(("Base.cs", """
            namespace App;

            public abstract class Base
            {
                public virtual int On(int @event) => @event;
                public abstract void Off(string @class);
            }

            public class Derived : Base
            {
            }
            """));

        var result = await new GenerateOverridesOperation(ws.Context).ExecuteAsync(
            new GenerateOverridesParams { SourceFile = ws.PathOf("Base.cs"), TypeName = "Derived", Members = ["On", "Off"] },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Base.cs");
        Assert.Contains("public override int On(int @event)", text);
        Assert.Contains("return base.On(@event);", text);
        Assert.Contains("public override void Off(string @class)", text);
        Assert.Contains("throw new System.NotImplementedException();", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task ExtractInterface_KeywordNames_AreEscaped()
    {
        using var ws = TestWorkspace.Create(("Handler.cs", """
            namespace App;

            public class Handler
            {
                public int @class => 1;
                public void Handle(int @event) { }
            }
            """));

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams { SourceFile = ws.PathOf("Handler.cs"), TypeName = "Handler", InterfaceName = "IHandler" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Handler.cs");
        Assert.Contains("int @class { get; }", text);
        Assert.Contains("void Handle(int @event);", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task PullMembersUp_MakeAbstract_KeepsKeywordNames()
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", """
            namespace App;

            public abstract class Shape
            {
            }

            public class Square : Shape
            {
                public int Scale(int @checked) => @checked * 2;
            }
            """));

        var result = await new PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams { SourceFile = ws.PathOf("Shapes.cs"), TypeName = "Square", Members = ["Scale"], MakeAbstract = true },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Shapes.cs");
        Assert.Contains("public abstract int Scale(int @checked);", text);
        Assert.Contains("public override int Scale(int @checked) => @checked * 2;", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task GenerateConstructor_KeywordMembersAndNullChecks_NoUsingSystem()
    {
        using var ws = TestWorkspace.Create(("Order.cs", """
            namespace App;

            public class Order
            {
                private readonly string _class;
                private readonly int @event;

                public string Describe() => _class + @event;
            }
            """));

        var result = await new GenerateConstructorOperation(ws.Context).ExecuteAsync(
            new GenerateConstructorParams { SourceFile = ws.PathOf("Order.cs"), TypeName = "Order", AddNullChecks = true },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Order.cs");
        Assert.Contains("public Order(string @class, int @event)", text);
        Assert.Contains("throw new System.ArgumentNullException(nameof(@class));", text);
        Assert.Contains("_class = @class;", text);
        Assert.Contains("this.@event = @event;", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("throw", "System.ArgumentNullException.ThrowIfNull(@class);")]
    [InlineData("guard", "if (@class is null)")]
    public async Task AddNullChecks_KeywordParameter_NoUsingSystem(string style, string expected)
    {
        using var ws = TestWorkspace.Create(("Printer.cs", """
            namespace App;

            public class Printer
            {
                public int Print(string @class)
                {
                    return @class.Length;
                }
            }
            """));

        var result = await new AddNullChecksOperation(ws.Context).ExecuteAsync(
            new AddNullChecksParams { SourceFile = ws.PathOf("Printer.cs"), MethodName = "Print", Style = style },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Printer.cs");
        Assert.Contains(expected, text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData(null, "return $\"Point {{ class = {@class}, event = {@event} }}\";")]
    [InlineData("stringbuilder", "builder.Append(@event);")]
    public async Task GenerateToString_KeywordMembers_AreEscaped(string? format, string expected)
    {
        using var ws = TestWorkspace.Create(("Point.cs", """
            namespace App;

            public class Point
            {
                public int @class;
                public int @event;
            }
            """));

        var result = await new GenerateToStringOperation(ws.Context).ExecuteAsync(
            new GenerateToStringParams { SourceFile = ws.PathOf("Point.cs"), TypeName = "Point", Format = format },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(expected, ws.ReadFile("Point.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task GenerateEqualsHashCode_KeywordMembers_AreEscaped()
    {
        using var ws = TestWorkspace.Create(("Point.cs", """
            namespace App;

            public class Point
            {
                public int @class;
                public string @event = "";
            }
            """));

        var result = await new GenerateEqualsHashCodeOperation(ws.Context).ExecuteAsync(
            new GenerateEqualsHashCodeParams { SourceFile = ws.PathOf("Point.cs"), TypeName = "Point" },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Point.cs");
        Assert.Contains("@class == other.@class", text);
        Assert.Contains("Equals(@event, other.@event)", text);
        Assert.Contains("System.HashCode.Combine(@class, @event)", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task ExtractMethod_KeywordLocal_BecomesAnEscapedParameter()
    {
        const string source = """
            namespace App;

            public static class Logger
            {
                public static void Log()
                {
                    var @class = 1;
                    System.Console.WriteLine(@class);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Logger.cs", source));
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("WriteLine", StringComparison.Ordinal));

        var result = await new ExtractMethodOperation(ws.Context).ExecuteAsync(
            new ExtractMethodParams
            {
                SourceFile = ws.PathOf("Logger.cs"),
                StartLine = line + 1,
                StartColumn = lines[line].IndexOf('S') + 1,
                EndLine = line + 1,
                EndColumn = lines[line].Length + 1,
                MethodName = "Print"
            },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Logger.cs");
        Assert.Contains("Print(@class);", text);
        Assert.Contains("private static void Print(int @class)", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("@class")]
    [InlineData("class")]
    public async Task IntroduceParameter_KeywordLocal_StaysEscaped(string variableName)
    {
        using var ws = TestWorkspace.Create(("Logger.cs", """
            namespace App;

            public static class Logger
            {
                public static int Log()
                {
                    int @class = 1;
                    return @class + 1;
                }
            }
            """));

        var result = await new IntroduceParameterOperation(ws.Context).ExecuteAsync(
            new IntroduceParameterParams { SourceFile = ws.PathOf("Logger.cs"), VariableName = variableName, Line = 7 },
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Log(int @class)", ws.ReadFile("Logger.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }
}
