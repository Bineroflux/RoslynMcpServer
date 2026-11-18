using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// generate_tostring built its interpolated string from text tokens whose source text
/// held the literal braces unescaped (<c>$"Person { Name = {Name} }}"</c>), so the
/// generated method never compiled.
/// </summary>
public sealed class GenerateToStringTests
{
    [Fact]
    public async Task GenerateToString_EscapesLiteralBraces()
    {
        using var ws = TestWorkspace.Create(("Person.cs", """
            namespace App;

            public class Person
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }
            """));

        var result = await new GenerateToStringOperation(ws.Context).ExecuteAsync(
            new GenerateToStringParams { SourceFile = ws.PathOf("Person.cs"), TypeName = "Person" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("return $\"Person {{ Name = {Name}, Age = {Age} }}\";", ws.ReadFile("Person.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
