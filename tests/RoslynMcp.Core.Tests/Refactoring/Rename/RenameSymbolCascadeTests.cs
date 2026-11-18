using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// rename_symbol advertised <c>renameImplementations</c> (default true) but never read it;
/// Roslyn's renamer always renames implementations and overrides along with a member, so
/// the parameter was removed and the tool description states that behaviour instead.
/// </summary>
public sealed class RenameSymbolCascadeTests
{
    [Theory]
    [InlineData("IShape.cs", 5, "Area", "Size")]
    [InlineData("Shape.cs", 5, "Describe", "Explain")]
    public async Task RenamingAMember_RenamesItsImplementationsAndOverrides(
        string file, int line, string oldName, string newName)
    {
        using var ws = TestWorkspace.Create(
            ("IShape.cs", """
                namespace App;

                public interface IShape
                {
                    double Area();
                }
                """),
            ("Shape.cs", """
                namespace App;

                public abstract class Shape : IShape
                {
                    public virtual string Describe() => "shape";
                    public abstract double Area();
                }
                """),
            ("Circle.cs", """
                namespace App;

                public sealed class Circle : Shape
                {
                    public override string Describe() => "circle";
                    public override double Area() => 3.14;
                }
                """));

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf(file), SymbolName = oldName, NewName = newName, Line = line },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        foreach (var name in new[] { "Shape.cs", "Circle.cs" })
        {
            Assert.Contains(newName + "()", ws.ReadFile(name));
            Assert.DoesNotContain(oldName + "()", ws.ReadFile(name));
        }

        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
