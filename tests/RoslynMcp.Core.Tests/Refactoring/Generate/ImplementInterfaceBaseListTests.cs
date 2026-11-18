using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// implement_interface on a type that does not list the interface yet. The interface used
/// to be left out of the base list, so implicit stubs implemented nothing and explicit ones
/// failed with CS0540; members the type already had were generated a second time.
/// </summary>
public sealed class ImplementInterfaceBaseListTests
{
    private const string Shapes = """
        using System;

        namespace App
        {
            public interface IShape
            {
                double Area();
                string Name { get; }
            }

            public abstract class Figure
            {
            }

            public class Circle : Figure
            {
                public double Area() => 3.14;
            }

            public class Square
            {
            }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeWithoutTheInterface_GetsItAddedToTheBaseList(bool explicitImplementation)
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", Shapes));

        var result = await ImplementAsync(ws, "Square", "IShape", explicitImplementation);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Shapes.cs").ReplaceLineEndings("\n");
        Assert.Contains("    public class Square : IShape\n    {\n", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task TypeWithBaseClass_KeepsItAndSkipsMembersItAlreadyHas()
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", Shapes));

        var result = await ImplementAsync(ws, "Circle", "IShape", explicitImplementation: false);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Shapes.cs");
        Assert.Contains("public class Circle : Figure, IShape", text);
        Assert.Contains("public string Name", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"public double Area\("));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task NotAnInterface_IsRejected()
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", Shapes));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ImplementAsync(ws, "Square", "Figure", explicitImplementation: false));

        Assert.Equal(ErrorCodes.InterfaceNotFound, ex.ErrorCode);
        Assert.Equal(Shapes, ws.ReadFile("Shapes.cs"));
    }

    private static Task<RefactoringResult> ImplementAsync(
        TestWorkspace ws, string typeName, string interfaceName, bool explicitImplementation) =>
        new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Shapes.cs"),
                TypeName = typeName,
                InterfaceName = interfaceName,
                ExplicitImplementation = explicitImplementation
            },
            TestContext.Current.CancellationToken);
}
