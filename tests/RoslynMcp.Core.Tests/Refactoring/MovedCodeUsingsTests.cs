using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Hierarchy;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Code moved to another file takes the usings only it used out of the file it left
/// (pull_members_up, extract_base_class, move_type_to_file); usings that were unused before, or
/// that the code staying behind uses, stay.
/// </summary>
public sealed class MovedCodeUsingsTests
{
    private static readonly string Square = Lf("""
        using System;
        using System.Linq;
        using System.Text;
        using static System.Math;

        namespace App;

        public class Square : Base
        {
            public DateTime Created = DateTime.MinValue;

            public string Describe(int a, int b) => new StringBuilder().Append(Max(a, b)).ToString();
        }
        """);

    private static readonly string SquareAfter = Lf("""
        using System;
        using System.Linq;

        namespace App;

        public class Square : Base
        {
            public DateTime Created = DateTime.MinValue;
        }
        """);

    [Fact]
    public async Task PullMembersUp_RemovesTheUsingsOnlyThePulledMembersUsed()
    {
        using var ws = TestWorkspace.Create(("Base.cs", Lf("namespace App;\n\npublic class Base\n{\n}")), ("Square.cs", Square));
        PullMembersUpParams Params(bool preview) => new()
        {
            SourceFile = ws.PathOf("Square.cs"),
            TypeName = "Square",
            Members = ["Describe"],
            Preview = preview
        };

        var preview = await new PullMembersUpOperation(ws.Context).ExecuteAsync(Params(preview: true), TestContext.Current.CancellationToken);
        var result = await new PullMembersUpOperation(ws.Context).ExecuteAsync(Params(preview: false), TestContext.Current.CancellationToken);

        AssertListed(preview, ws.PathOf("Square.cs"), "the moved code");
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.UsingDirectivesRemoved);
        Assert.Equal(SquareAfter, ws.ReadFile("Square.cs"));
        Assert.StartsWith("using System.Text;\nusing static System.Math;\n\nnamespace App;", ws.ReadFile("Base.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractBaseClass_IntoAnotherFile_RemovesTheUsingsOnlyTheExtractedMembersUsed()
    {
        using var ws = TestWorkspace.Create(("Square.cs", Square.Replace("public class Square : Base", "public class Square", StringComparison.Ordinal)));
        ExtractBaseClassParams Params(bool preview) => new()
        {
            SourceFile = ws.PathOf("Square.cs"),
            TypeName = "Square",
            BaseClassName = "Base",
            Members = ["Describe"],
            TargetFile = ws.PathOf("Base.cs"),
            Preview = preview
        };

        var preview = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(Params(preview: true), TestContext.Current.CancellationToken);
        var result = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(Params(preview: false), TestContext.Current.CancellationToken);

        AssertListed(preview, ws.PathOf("Square.cs"), "the extracted members");
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.UsingDirectivesRemoved);
        Assert.Equal(SquareAfter, ws.ReadFile("Square.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MoveTypeToFile_RemovesTheUsingsOnlyTheMovedTypeUsed()
    {
        using var ws = TestWorkspace.Create(("Models.cs", Lf("""
            using System;
            using System.Linq;
            using System.Text;
            using static System.Math;

            namespace App;

            public class Keep
            {
                public DateTime When = DateTime.MinValue;
            }

            public class Moved
            {
                public string Text(int a, int b) => new StringBuilder().Append(Max(a, b)).ToString();
            }
            """)));
        MoveTypeToFileParams Params(bool preview) => new()
        {
            SourceFile = ws.PathOf("Models.cs"),
            SymbolName = "Moved",
            TargetFile = ws.PathOf("Moved.cs"),
            Preview = preview
        };

        var preview = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(Params(preview: true), TestContext.Current.CancellationToken);
        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(Params(preview: false), TestContext.Current.CancellationToken);

        AssertListed(preview, ws.PathOf("Models.cs"), "the moved type");
        Assert.Contains(preview.PendingChanges!, c => c.File == ws.PathOf("Moved.cs") && c.ChangeType == Contracts.Enums.ChangeKind.Create);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.UsingDirectivesRemoved);
        Assert.Equal(Lf("""
            using System;
            using System.Linq;

            namespace App;

            public class Keep
            {
                public DateTime When = DateTime.MinValue;
            }
            """), ws.ReadFile("Models.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static void AssertListed(RefactoringResult preview, string file, string usedBy)
    {
        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Equal(2, preview.UsingDirectivesRemoved);
        Assert.Contains(preview.PendingChanges!, c => c.File == file && c.Description == $"Remove 'using System.Text;', which only {usedBy} used" && c.StartLine == 3);
        Assert.Contains(preview.PendingChanges!, c => c.File == file && c.Description == $"Remove 'using static System.Math;', which only {usedBy} used" && c.StartLine == 4);
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";
}
