using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// implement_interface's <c>members</c> and the <c>fields</c> of generate_equals_hashcode and
/// generate_tostring dropped names that matched nothing, so a typo produced less code than
/// asked for and still reported success. generate_constructor, generate_overrides and
/// extract_interface already rejected such names.
/// </summary>
public sealed class UnknownMemberNameTests
{
    private const string Person = """
        namespace App;

        public interface INamed
        {
            string Name { get; }
            void Rename(string name);
        }

        public class Person : INamed
        {
            public string Name { get; set; } = "";
            public int Age { get; set; }
        }
        """;

    [Fact]
    public async Task GenerateEqualsHashCode_RejectsAnUnknownField()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new GenerateEqualsHashCodeOperation(ws.Context).ExecuteAsync(
            new GenerateEqualsHashCodeParams { SourceFile = ws.PathOf("Person.cs"), TypeName = "Person", Fields = ["Name", "Agee"] },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("Agee", ex.Message);
        Assert.Equal(Person, ws.ReadFile("Person.cs"));
    }

    [Fact]
    public async Task GenerateToString_RejectsAnUnknownField()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new GenerateToStringOperation(ws.Context).ExecuteAsync(
            new GenerateToStringParams { SourceFile = ws.PathOf("Person.cs"), TypeName = "Person", Fields = ["Agee"] },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task ImplementInterface_RejectsANameTheInterfaceDoesNotHave()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams { SourceFile = ws.PathOf("Person.cs"), TypeName = "Person", InterfaceName = "INamed", Members = ["Rename", "Renam"] },
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("Renam", ex.Message);
        Assert.Equal(Person, ws.ReadFile("Person.cs"));
    }

    [Fact]
    public async Task ImplementInterface_StillAcceptsAnAlreadyImplementedName()
    {
        using var ws = TestWorkspace.Create(("Person.cs", Person));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams { SourceFile = ws.PathOf("Person.cs"), TypeName = "Person", InterfaceName = "INamed", Members = ["Name", "Rename"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public void Rename(string name)", ws.ReadFile("Person.cs"));
    }
}
