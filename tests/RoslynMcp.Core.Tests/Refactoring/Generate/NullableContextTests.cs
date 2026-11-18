using RoslynMcp.Contracts.Models;
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
        Assert.Contains("System.EventHandler<string?>? Changed", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
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
