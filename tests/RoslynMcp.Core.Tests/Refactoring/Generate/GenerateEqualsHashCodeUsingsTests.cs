using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// generate_equals_hashcode wrote <c>EqualityComparer&lt;T&gt;</c> and <c>HashCode</c> as bare
/// names, which compile only with implicit usings or <c>using System.Collections.Generic;</c> /
/// <c>using System;</c>. They are now written fully qualified and shortened as far as the
/// file's usings allow, like other generated type names. The parameter is <c>object?</c> only
/// where nullable annotations are enabled, and a generic type is matched with its type
/// parameters (<c>obj is Box&lt;T&gt; other</c>).
/// </summary>
public sealed class GenerateEqualsHashCodeUsingsTests
{
    [Fact]
    public async Task NoUsings_QualifiesTheFrameworkTypes()
    {
        using var ws = TestWorkspace.Create(("Person.cs", """
            namespace App;

            public class Person
            {
                public string name = "";
                public int age;
            }
            """));

        var result = await GenerateAsync(ws, "Person.cs", "Person");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Person.cs");
        Assert.Contains("""
                    return obj is Person other &&
                           System.Collections.Generic.EqualityComparer<string>.Default.Equals(name, other.name) &&
                           age == other.age;
            """.ReplaceLineEndings(LineEndingOf(text)), text);
        Assert.Contains("return System.HashCode.Combine(name, age);", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task WithUsings_WritesTheShortNames()
    {
        using var ws = TestWorkspace.Create(("Person.cs", """
            using System;
            using System.Collections.Generic;

            namespace App;

            public class Person
            {
                public string name = "";
                public int age;
            }
            """));

        var result = await GenerateAsync(ws, "Person.cs", "Person");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Person.cs");
        Assert.Contains("EqualityComparer<string>.Default.Equals(name, other.name)", text);
        Assert.DoesNotContain("System.Collections.Generic.EqualityComparer", text);
        Assert.Contains("return HashCode.Combine(name, age);", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task NullableDisabled_GenericType_ManyFields_Compiles()
    {
        using var ws = TestWorkspace.Create(("Box.cs", """
            #nullable disable
            namespace App;

            public class Box<T>
            {
                public T a; public T b; public T c; public string d;
                public int e; public int f; public int g; public int h; public int i;
            }
            """));

        var result = await GenerateAsync(ws, "Box.cs", "Box");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Box.cs");
        Assert.Contains("public override bool Equals(object obj)", text);
        Assert.Contains("""
                    return obj is Box<T> other &&
                           System.Collections.Generic.EqualityComparer<T>.Default.Equals(a, other.a) &&
            """.ReplaceLineEndings(LineEndingOf(text)), text);
        Assert.Contains("var hash = new System.HashCode();", text);
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Comparisons_OnePerLine_AlignedUnderTheFirstOperand(string lineEnding)
    {
        using var ws = TestWorkspace.Create(("Pair.cs", """
            using System;
            using System.Collections.Generic;

            namespace App
            {
                public class Outer
                {
                    public class Pair<T>
                    {
                        public T First { get; set; }
                        public DateTime When { get; set; }
                        public int? Count { get; set; }
                    }
                }
            }
            """.ReplaceLineEndings(lineEnding)));

        var result = await GenerateAsync(ws, "Pair.cs", "Pair");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Pair.cs");
        Assert.Contains(
            string.Join(lineEnding,
                "            public override bool Equals(object? obj)",
                "            {",
                "                return obj is Pair<T> other &&",
                "                       EqualityComparer<T>.Default.Equals(First, other.First) &&",
                "                       EqualityComparer<DateTime>.Default.Equals(When, other.When) &&",
                "                       Count == other.Count;",
                "            }"),
            text);
        Assert.Equal(lineEnding == "\n" ? 0 : text.Count(c => c == '\n'), text.Count(c => c == '\r'));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task OneComparison_StaysOnOneLine()
    {
        using var ws = TestWorkspace.Create(("Id.cs", """
            namespace App;

            public class Id
            {
                public int Value;
            }
            """));

        var result = await GenerateAsync(ws, "Id.cs", "Id");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("        return obj is Id other && Value == other.Value;", ws.ReadFile("Id.cs"));
    }

    private static string LineEndingOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static Task<RefactoringResult> GenerateAsync(TestWorkspace ws, string file, string type) =>
        new GenerateEqualsHashCodeOperation(ws.Context).ExecuteAsync(
            new GenerateEqualsHashCodeParams { SourceFile = ws.PathOf(file), TypeName = type },
            TestContext.Current.CancellationToken);
}
