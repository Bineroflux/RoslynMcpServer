using System.Runtime.Loader;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// generate_tostring advertised <c>format: "interpolated" | "stringbuilder"</c> but always
/// generated an interpolated string. Both formats must produce the same text; anything
/// else is rejected.
/// </summary>
public sealed class GenerateToStringFormatTests
{
    private const string Person = """
        namespace App;

        public class Person
        {
            public string Name { get; set; } = "";
            public int Age { get; set; }
        }
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("interpolated")]
    [InlineData("stringbuilder")]
    [InlineData("StringBuilder")]
    public async Task BothFormats_ProduceTheSameText(string? format)
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        await GenerateAsync(ws, format);

        var text = ws.ReadFile("Person.cs");
        var usesBuilder = string.Equals(format, "stringbuilder", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(usesBuilder, text.Contains("StringBuilder"));
        Assert.Equal(!usesBuilder, text.Contains("return $\""));
        Assert.Equal("Person { Name = Ada, Age = 36 }", RunToString(ws, "App.Person", ("Name", "Ada"), ("Age", 36)));
    }

    [Fact]
    public async Task StringBuilder_IsLaidOutAndNamedLikeTheSurroundingCode()
    {
        // LF file without `using System.Text;`: the type name stays qualified, the method
        // is indented like its neighbours, and no CRLF sneaks in.
        using var ws = TestWorkspace.Create(("Person.cs", Person.ReplaceLineEndings("\n")));

        await GenerateAsync(ws, "stringbuilder");

        var text = ws.ReadFile("Person.cs");
        Assert.DoesNotContain("\r", text);
        Assert.Contains("""
                public override string ToString()
                {
                    var builder = new System.Text.StringBuilder();
                    builder.Append("Person { Name = ");
                    builder.Append(Name);
                    builder.Append(", Age = ");
                    builder.Append(Age);
                    builder.Append(" }");
                    return builder.ToString();
                }
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task StringBuilder_UsesTheShortNameWhenAUsingAllowsIt()
    {
        using var ws = TestWorkspace.Create(("Person.cs", "using System.Text;\n\n" + Person));

        await GenerateAsync(ws, "stringbuilder");

        Assert.Contains("var builder = new StringBuilder();", ws.ReadFile("Person.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task StringBuilder_LocalDoesNotShadowAnAppendedMember()
    {
        using var ws = TestWorkspace.Create(("Part.cs", """
            namespace App;

            public class Part
            {
                public string builder { get; set; } = "";
                public string builder1 { get; set; } = "";
            }
            """));

        await GenerateAsync(ws, "stringbuilder", "Part");

        Assert.Contains("var builder2 = ", ws.ReadFile("Part.cs"));
        Assert.Equal(
            "Part { builder = a, builder1 = b }",
            RunToString(ws, "App.Part", ("builder", "a"), ("builder1", "b")));
    }

    [Fact]
    public async Task UnknownFormat_IsRejected()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => GenerateAsync(ws, "concat"));

        Assert.Equal(ErrorCodes.InvalidParameterValue, ex.ErrorCode);
        Assert.Contains("interpolated, stringbuilder", ex.Message);
        Assert.Equal(Person, ws.ReadFile("Person.cs"));
    }

    private static async Task GenerateAsync(TestWorkspace ws, string? format, string typeName = "Person")
    {
        var result = await new GenerateToStringOperation(ws.Context).ExecuteAsync(
            new GenerateToStringParams { SourceFile = ws.PathOf(typeName + ".cs"), TypeName = typeName, Format = format },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
    }

    private static string? RunToString(TestWorkspace ws, string typeName, params (string Property, object Value)[] values)
    {
        var context = new AssemblyLoadContext(nameof(GenerateToStringFormatTests), isCollectible: true);
        try
        {
            var type = context.LoadFromStream(new MemoryStream(ws.EmitDiskAssembly())).GetType(typeName, throwOnError: true)!;
            var instance = Activator.CreateInstance(type)!;
            foreach (var (property, value) in values)
            {
                type.GetProperty(property)!.SetValue(instance, value);
            }

            return instance.ToString();
        }
        finally
        {
            context.Unload();
        }
    }
}
