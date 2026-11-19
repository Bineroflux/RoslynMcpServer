using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// Where pull_members_up puts the members (the part of a partial target, <c>targetFile</c>) and
/// what the target needs for them.
/// </summary>
public sealed class PullMembersUpTargetTests
{
    private static readonly string BasePart = Lf("""
        namespace App;

        public partial class Base
        {
            public int Id;
        }
        """);

    private static readonly string BaseMembers = Lf("""
        namespace App;

        public partial class Base
        {
            public int Size() => 1;
        }
        """);

    private static readonly string Derived = Lf("""
        namespace App;

        public class Derived : Base
        {
            public int Twice() => Size() * 2;
        }
        """);

    [Fact]
    public async Task TargetFile_PutsTheMembersIntoThatPartOfAPartialTarget_TheDefaultIsTheFirstPart()
    {
        using var ws = TestWorkspace.Create(("Base.cs", BasePart), ("Base.Members.cs", BaseMembers), ("Derived.cs", Derived));

        var result = await PullAsync(ws, ["Twice"], targetFile: ws.PathOf("Base.Members.cs"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(BasePart, ws.ReadFile("Base.cs"));
        Assert.Equal(Lf("""
            namespace App;

            public partial class Base
            {
                public int Size() => 1;

                public int Twice() => Size() * 2;
            }
            """), ws.ReadFile("Base.Members.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());

        using var other = TestWorkspace.Create(("Base.cs", BasePart), ("Base.Members.cs", BaseMembers), ("Derived.cs", Derived));
        var byDefault = await PullAsync(other, ["Twice"]);

        Assert.True(byDefault.Success, byDefault.Error?.Message);
        Assert.Contains("public int Twice() => Size() * 2;", other.ReadFile("Base.cs"));
        Assert.Equal(BaseMembers, other.ReadFile("Base.Members.cs"));
    }

    [Fact]
    public async Task TargetFile_WithoutAPartOfTheTarget_OrRelative_IsRefused()
    {
        using var ws = TestWorkspace.Create(("Base.cs", BasePart), ("Base.Members.cs", BaseMembers), ("Derived.cs", Derived));

        var elsewhere = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, ["Twice"], targetFile: ws.PathOf("Derived.cs")));
        var relative = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, ["Twice"], targetFile: "Base.cs"));

        Assert.Equal(ErrorCodes.InvalidTargetPath, elsewhere.ErrorCode);
        Assert.Contains("declares no part of 'App.Base'", elsewhere.Message);
        Assert.Contains(ws.PathOf("Base.Members.cs"), elsewhere.Message);
        Assert.Equal(ErrorCodes.InvalidTargetPath, relative.ErrorCode);
        Assert.Equal(Derived, ws.ReadFile("Derived.cs"));
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> PullAsync(
        TestWorkspace ws,
        string[] members,
        string file = "Derived.cs",
        string type = "Derived",
        string? targetType = null,
        string? targetFile = null,
        bool makeAbstract = false) =>
        new PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams
            {
                SourceFile = ws.PathOf(file),
                TypeName = type,
                Members = members,
                TargetType = targetType,
                TargetFile = targetFile,
                MakeAbstract = makeAbstract
            },
            TestContext.Current.CancellationToken);
}
