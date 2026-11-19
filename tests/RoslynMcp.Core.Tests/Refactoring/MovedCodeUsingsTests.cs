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

    private static readonly string Dog = Lf("""
        // Header
        global using System.Collections.Generic;
        using System;
        using System.Linq;
        using System.Text;
        using static System.Math;
        using Sb = System.Text.StringBuilder;

        namespace App;

        public class Keep
        {
            public DateTime When = DateTime.MinValue;
        }

        public class Dog
        {
            public Uri Home() => new Uri("https://example.com");

            public string Bark(int a, int b) => new Sb().Append(Max(a, b)).Append(new StringBuilder()).ToString();

            public List<int> Legs() => new();
        }
        """);

    private static readonly string DogUsings = Lf("""
        using System;
        using System.Text;
        using static System.Math;
        using Sb = System.Text.StringBuilder;

        namespace App;
        """);

    private static readonly string DogLeft = Lf("""
        // Header
        global using System.Collections.Generic;
        using System;
        using System.Linq;

        namespace App;
        """);

    [Fact]
    public async Task MoveTypeToFile_IntoANewFile_CopiesOnlyTheUsingsTheTypeNeeds_WithOneBlankLineAfterTheNamespace()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Dog));

        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Dog.cs"), SymbolName = "Dog", TargetFile = ws.PathOf("Dog.Moved.cs") },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(DogUsings + Lf("""

            public class Dog
            {
                public Uri Home() => new Uri("https://example.com");

                public string Bark(int a, int b) => new Sb().Append(Max(a, b)).Append(new StringBuilder()).ToString();

                public List<int> Legs() => new();
            }
            """), ws.ReadFile("Dog.Moved.cs"));
        Assert.StartsWith(DogLeft + "\npublic class Keep", ws.ReadFile("Dog.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractBaseClass_IntoANewFile_CopiesOnlyTheUsingsTheMembersNeed()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Dog));

        var result = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                BaseClassName = "Animal",
                Members = ["Home", "Bark"],
                TargetFile = ws.PathOf("Animal.cs")
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(DogUsings + "\npublic class Animal\n", ws.ReadFile("Animal.cs"));
        Assert.StartsWith(DogLeft, ws.ReadFile("Dog.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractInterface_IntoANewFile_CopiesOnlyTheUsingsTheInterfaceNeeds()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Dog));

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                InterfaceName = "IDog",
                Members = ["Home", "Bark"],
                TargetFile = ws.PathOf("IDog.cs")
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith("using System;\n\nnamespace App;", ws.ReadFile("IDog.cs"));
        Assert.Contains("Uri Home();", ws.ReadFile("IDog.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MoveTypeToFile_InAMultiTargetedProject_KeepsAUsingOnlyAnotherFrameworksCodeNeeds()
    {
        using var ws = TestWorkspace.CreateSolution(new TestWorkspace.ProjectSpec(
            "Lib",
            [("Models.cs", Lf("""
                using System.Linq;
                using System.Text;

                namespace App;

                public class Keep
                {
                }

                public class Moved
                {
                #if NET11
                    public int Count(int[] values) => values.Count();
                #endif

                    public string Text() => new StringBuilder().ToString();
                }
                """))],
            TargetFrameworks: ["net10", "net11"]));

        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Lib", "Models.cs"), SymbolName = "Moved", TargetFile = ws.PathOf("Lib", "Moved.cs") },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith("using System.Linq;\nusing System.Text;\n\nnamespace App;\n\npublic class Moved", File.ReadAllText(ws.PathOf("Lib", "Moved.cs")));
        Assert.Equal(2, result.UsingDirectivesRemoved);
        Assert.Equal(Lf("""
            namespace App;

            public class Keep
            {
            }
            """), File.ReadAllText(ws.PathOf("Lib", "Models.cs")));
    }

    [Fact]
    public async Task MoveTypeToFile_IntoAnExistingFile_AddsTheUsingsTheTypeNeeds_WhereTheFileHasItsUsings()
    {
        using var ws = TestWorkspace.Create(
            ("Contacts.cs", Lf("""
                namespace App
                {
                    using System;

                    public class Contact
                    {
                        public DateTime Born;
                    }
                }
                """)),
            ("Models.cs", Lf("""
                using System.Collections.Generic;
                using System.Text;
                using static System.Math;
                using Sb = System.Text.StringBuilder;

                namespace App;

                public class Keep
                {
                }

                public class Moved
                {
                    public string Text(int a, int b) => new Sb().Append(Max(a, b)).Append(new StringBuilder()).Append(new List<int>().Count).ToString();
                }
                """)));

        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Moved", TargetFile = ws.PathOf("Contacts.cs") },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(Lf("""
            namespace App
            {
                using System;
                using System.Collections.Generic;
                using System.Text;
                using static System.Math;
                using Sb = System.Text.StringBuilder;

                public class Contact
            """), ws.ReadFile("Contacts.cs"));
        Assert.Contains("public class Moved", ws.ReadFile("Contacts.cs"));
        Assert.Equal(4, result.UsingDirectivesRemoved);
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
