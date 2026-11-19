using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// move_type_to_namespace changes a type's namespace by renaming the namespace declaration around
/// it, which moves everything that declaration declares. When the type shares it with anything
/// else, the operation is refused with NamespaceBlockShared and says to give the type a file of its
/// own first. Another class used to be refused as RoslynError (4003), and an enum or delegate was
/// not noticed at all: it moved along, and the code using it no longer compiled.
/// </summary>
public sealed class MoveTypeToNamespaceSharedDeclarationTests
{
    [Theory]
    [InlineData("public sealed class Other\n{\n}\n", "Other")]
    [InlineData("public enum Kind\n{\n    A,\n}\n", "Kind")]
    [InlineData("public delegate void Handler();\n", "Handler")]
    public async Task TypeSharingItsFileScopedNamespace_IsRefused_AndNothingIsWritten(string other, string otherName)
    {
        var models = $"namespace App;\n\npublic sealed class Widget\n{{\n}}\n\n{other}";
        const string uses = "namespace App;\n\ninternal static class Uses\n{\n    public static object Make() => new Widget();\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models), ("Uses.cs", uses));

        var refused = await Assert.ThrowsAsync<RefactoringException>(() => new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Widget", TargetNamespace = "App.Parts" },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.NamespaceBlockShared, refused.ErrorCode);
        Assert.Contains(otherName, refused.Message, StringComparison.Ordinal);
        Assert.Contains("move_type_to_file", refused.Message, StringComparison.Ordinal);
        Assert.Equal(models, ws.ReadFile("Models.cs"));
        Assert.Equal(uses, ws.ReadFile("Uses.cs"));
    }

    [Fact]
    public async Task TypeSharingItsBlockNamespace_WithANestedNamespace_IsRefused()
    {
        const string models = "namespace App\n{\n    public sealed class Widget\n    {\n    }\n\n    namespace Inner\n    {\n        public sealed class Deep\n        {\n        }\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        var refused = await Assert.ThrowsAsync<RefactoringException>(() => new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Widget", TargetNamespace = "App.Parts" },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.NamespaceBlockShared, refused.ErrorCode);
        Assert.Contains("Inner", refused.Message, StringComparison.Ordinal);
        Assert.Equal(models, ws.ReadFile("Models.cs"));
    }

    [Fact]
    public async Task TypeAloneInItsNamespaceDeclaration_NextToAnotherDeclaration_IsMoved()
    {
        // Two block namespaces in one file: the type's own declaration holds nothing else.
        const string models = "namespace App\n{\n    public sealed class Widget\n    {\n    }\n}\n\nnamespace App\n{\n    public sealed class Other\n    {\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        var result = await new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Widget", TargetNamespace = "App.Parts" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(models.Replace("namespace App\n{\n    public sealed class Widget", "namespace App.Parts\n{\n    public sealed class Widget", StringComparison.Ordinal), ws.ReadFile("Models.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
