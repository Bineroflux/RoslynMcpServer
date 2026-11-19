using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// The layout of a type moved into another file (move_type_to_file) and of the file
/// extract_interface creates.
/// </summary>
public sealed class MovedTypeLayoutTests
{
    [Fact]
    public async Task MoveTypeToFile_IntoAnExistingBlockNamespace_IndentsTheTypeLikeTheBlocksMembers()
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
                using System;

                namespace App;

                public class Keep
                {
                }

                /// <summary>
                /// Moved.
                /// </summary>
                [Serializable]
                public class Moved
                {
                    // The text.
                    private const string Text = @"line one
                  line two";

                    public string Get() => Text;
                }
                """)));

        var result = await MoveAsync(ws, "Models.cs", "Contacts.cs");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App
            {
                using System;

                public class Contact
                {
                    public DateTime Born;
                }

                /// <summary>
                /// Moved.
                /// </summary>
                [Serializable]
                public class Moved
                {
                    // The text.
                    private const string Text = @"line one
              line two";

                    public string Get() => Text;
                }
            }
            """), ws.ReadFile("Contacts.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MoveTypeToFile_FromABlockNamespace_IntoANewFile_PutsTheTypeAtColumnZero()
    {
        using var ws = TestWorkspace.Create(("Models.cs", Lf("""
            namespace App
            {
                public class Keep
                {
                }

                /// <summary>
                /// Moved.
                /// </summary>
                public class Moved
                {
                    public string Text = @"a
                b";
                }
            }
            """)));

        var result = await MoveAsync(ws, "Models.cs", "Moved.cs");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App;

            /// <summary>
            /// Moved.
            /// </summary>
            public class Moved
            {
                public string Text = @"a
                b";
            }
            """), ws.ReadFile("Moved.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData(
        new[] { "Home", "Bark" },
        "using System;\n\nnamespace App;\n\npublic interface IDog\n{\n    Uri Home();\n    string Bark();\n}\n")]
    [InlineData(
        new[] { "Bark" },
        "namespace App;\n\npublic interface IDog\n{\n    string Bark();\n}\n")]
    public async Task ExtractInterface_IntoANewFile_HasABlankLineAfterTheUsingsAndTheNamespace_AndAFinalLineBreak(string[] members, string expected)
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Lf("""
            using System;
            using System.Text;

            namespace App;

            public class Dog
            {
                public Uri Home() => new Uri("https://example.com");

                public string Bark() => new StringBuilder().ToString();
            }
            """)));

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                InterfaceName = "IDog",
                Members = members,
                TargetFile = ws.PathOf("IDog.cs")
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, ws.ReadFile("IDog.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static Task<RefactoringResult> MoveAsync(TestWorkspace ws, string source, string target) =>
        new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf(source), SymbolName = "Moved", TargetFile = ws.PathOf(target) },
            TestContext.Current.CancellationToken);

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";
}
