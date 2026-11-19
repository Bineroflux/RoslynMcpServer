using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Convert;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Convert;

/// <summary>
/// convert_property wrote the converted property with <c>NormalizeWhitespace</c>'s CRLF line
/// breaks and no indentation, and put a hard-coded CRLF before the backing field, so an LF
/// file got mixed line endings and the property started at column 0 without its documentation
/// comment. The generated code now takes the file's line ending and indentation.
/// </summary>
public sealed class ConvertPropertyLayoutTests
{
    private const string Source = """
        namespace App
        {
            public class Person
            {
                /// <summary>The age.</summary>
                public int Age { get; set; }

                public string Name { get; } = "";
            }
        }
        """;

    private const string FullProperty = """
        namespace App
        {
            public class Person
            {
                private int _age;

                /// <summary>The age.</summary>
                public int Age
                {
                    get
                    {
                        return _age;
                    }

                    set
                    {
                        _age = value;
                    }
                }

                public string Name { get; } = "";
            }
        }
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ToFullProperty_UsesTheFilesLineEndingAndIndentation(string lineEnding)
    {
        using var ws = TestWorkspace.Create(("Person.cs", Source.ReplaceLineEndings(lineEnding)));

        var result = await ConvertAsync(ws, "Age", "ToFullProperty");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(FullProperty.ReplaceLineEndings(lineEnding), ws.ReadFile("Person.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ToAutoProperty_UsesTheFilesLineEndingAndIndentation(string lineEnding)
    {
        using var ws = TestWorkspace.Create(("Person.cs", FullProperty.ReplaceLineEndings(lineEnding)));

        var result = await ConvertAsync(ws, "Age", "ToAutoProperty");

        // The round trip: the backing field goes with the accessors.
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Source.ReplaceLineEndings(lineEnding), ws.ReadFile("Person.cs"));
    }

    [Fact]
    public async Task ToFullProperty_KeepsTheHeader_FieldTakesTheInitializerAsWritten()
    {
        using var ws = TestWorkspace.Create(("Person.cs", """
            namespace App
            {
                public class Person
                {
                    [System.ComponentModel.Description( "age" )]
                    public  int Age { get; private set; } = 4*10;
                }

                public static class Program
                {
                    public static string Run() => new Person().Age.ToString();
                }
            }
            """.ReplaceLineEndings("\n")));
        var before = Run(ws);

        var result = await ConvertAsync(ws, "Age", "ToFullProperty");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Person.cs");
        Assert.Contains("        private int _age = 4*10;\n", text);
        Assert.Contains("        [System.ComponentModel.Description( \"age\" )]\n        public  int Age\n        {\n            get\n", text);
        Assert.Contains("            private set\n", text);
        Assert.Equal("40", before);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task ToAutoProperty_KeepsTheHeader()
    {
        using var ws = TestWorkspace.Create(("Person.cs", """
            namespace App
            {
                public class Person
                {
                    private int _age;

                    [System.Obsolete]
                    public  int Age
                    {
                        get { return _age; }
                        private set { _age = value; }
                    }
                }
            }
            """.ReplaceLineEndings("\n")));

        var result = await ConvertAsync(ws, "Age", "ToAutoProperty");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("        [System.Obsolete]\n        public  int Age { get; private set; }\n    }\n", ws.ReadFile("Person.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(ConvertPropertyLayoutTests), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(ws.EmitDiskAssembly()));
            return (string)assembly.GetType("App.Program", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    private static Task<RefactoringResult> ConvertAsync(TestWorkspace ws, string property, string direction) =>
        new ConvertPropertyOperation(ws.Context).ExecuteAsync(
            new ConvertPropertyParams { SourceFile = ws.PathOf("Person.cs"), PropertyName = property, Direction = direction },
            TestContext.Current.CancellationToken);
}
