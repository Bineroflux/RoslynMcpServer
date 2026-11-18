using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Organize;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Generated code takes the line ending of the file it is written into. It used to be
/// CRLF throughout (NormalizeWhitespace's default, hard-coded CRLF trivia and the
/// formatter's platform NewLine), so editing an LF file left it with mixed line endings,
/// including on original lines next to the insertion.
/// </summary>
public sealed class LineEndingTests
{
    private const string FileName = "Code.cs";

    private const string Source = """
        using System;

        namespace App
        {
            public interface IShape
            {
                double Area();
            }

            public abstract class Animal
            {
                public virtual string Describe() => "animal";
            }

            public class Circle : IShape
            {
            }

            public class Dog : Animal
            {
                private readonly int _size;
                public StringBuilder Log() => null;
            }
        }
        """;

    public static TheoryData<string> Operations => new()
    {
        "implement_interface",
        "generate_overrides",
        "generate_constructor",
        "add_missing_usings",
        "extract_interface"
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task LfFileWithoutBom_GetsLfLinesAndKeepsEveryOriginalLine(string operation)
    {
        var original = Source.ReplaceLineEndings("\n");
        using var ws = TestWorkspace.Create((FileName, original));

        var result = await RunAsync(operation, ws);

        Assert.True(result.Success, result.Error?.Message);
        var bytes = await File.ReadAllBytesAsync(ws.PathOf(FileName), TestContext.Current.CancellationToken);
        Assert.False(bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "A byte order mark was added.");
        Assert.DoesNotContain((byte)'\r', bytes);
        AssertOnlyLinesInserted(original, ws.ReadFile(FileName));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CrLfFile_GetsCrLfLines(string operation)
    {
        var original = Source.ReplaceLineEndings("\r\n");
        using var ws = TestWorkspace.Create((FileName, original));

        var result = await RunAsync(operation, ws);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Equal(text.Split('\n').Length, text.Split("\r\n").Length);
        AssertOnlyLinesInserted(original, text);
    }

    [Fact]
    public async Task NewFile_UsesEditorConfigEndOfLine()
    {
        using var ws = TestWorkspace.Create(
            [(FileName, Source.ReplaceLineEndings("\r\n"))],
            [],
            editorConfig: "root = true\n\n[*.cs]\nend_of_line = lf\n");

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = ws.PathOf(FileName),
                TypeName = "Dog",
                InterfaceName = "IDog",
                TargetFile = ws.PathOf("IDog.cs"),
                AddInterfaceToType = false
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("IDog.cs");
        Assert.Contains("interface IDog", text);
        Assert.DoesNotContain('\r', text);
    }

    private static Task<RefactoringResult> RunAsync(string operation, TestWorkspace ws)
    {
        var ct = TestContext.Current.CancellationToken;
        var file = ws.PathOf(FileName);
        return operation switch
        {
            "implement_interface" => new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
                new ImplementInterfaceParams { SourceFile = file, TypeName = "Circle", InterfaceName = "IShape" }, ct),
            "generate_overrides" => new GenerateOverridesOperation(ws.Context).ExecuteAsync(
                new GenerateOverridesParams { SourceFile = file, TypeName = "Dog", Members = ["Describe"] }, ct),
            "generate_constructor" => new GenerateConstructorOperation(ws.Context).ExecuteAsync(
                new GenerateConstructorParams { SourceFile = file, TypeName = "Dog" }, ct),
            "add_missing_usings" => new AddMissingUsingsOperation(ws.Context).ExecuteAsync(
                new AddMissingUsingsParams { SourceFile = file }, ct),
            "extract_interface" => new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
                new ExtractInterfaceParams { SourceFile = file, TypeName = "Dog", InterfaceName = "IDog", AddInterfaceToType = false }, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    /// <summary>
    /// Every original line, line ending included, is still there in order: the operation
    /// only inserted lines and touched nothing else.
    /// </summary>
    private static void AssertOnlyLinesInserted(string original, string updated)
    {
        var updatedLines = updated.Split('\n');
        var next = 0;
        foreach (var line in original.Split('\n'))
        {
            while (next < updatedLines.Length && updatedLines[next] != line)
            {
                next++;
            }

            Assert.True(next < updatedLines.Length, $"Original line was changed or lost: '{line}'\n--- updated ---\n{updated}");
            next++;
        }

        Assert.True(updatedLines.Length > original.Split('\n').Length, "Nothing was inserted.");
    }
}
