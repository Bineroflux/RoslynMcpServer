using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// change_signature wrote a parameter without a default value after one with a default value
/// (<c>M(int a = 0, int b)</c>, CS1737). Such a list is now refused before anything is
/// changed, unless the later parameter is the trailing <c>params</c> array.
/// </summary>
public sealed class ChangeSignatureOptionalOrderTests
{
    private const string Source = """
        namespace App;

        public class Calc
        {
            public int Sum(int a, int b) => a + b;

            public int All(int first, params int[] rest) => first + rest.Length;

            public int Use() => Sum(1, 2) + All(1, 2, 3);
        }
        """;

    [Theory]
    [InlineData("b")]
    [InlineData("c")]
    public async Task RequiredAfterOptional_IsRefused(string required)
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));
        ParameterChange[] parameters = required == "b"
            ? [new() { OriginalName = "a", Name = "a", DefaultValue = "0" }, new() { OriginalName = "b", Name = "b" }]
            : [new() { OriginalName = "a", Name = "a" }, new() { OriginalName = "b", Name = "b", DefaultValue = "0" }, new() { Name = "c", Type = "int" }];

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, "Sum", parameters));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
        Assert.Contains($"Parameter '{required}' has no defaultValue", ex.Message);
        Assert.Contains("CS1737", ex.Message);
        Assert.Equal(Source, ws.ReadFile("Calc.cs"));
    }

    [Fact]
    public async Task OptionalMovedBehindTheRequiredOne_IsAccepted()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Sum",
            new() { OriginalName = "b", Name = "b" },
            new() { OriginalName = "a", Name = "a", DefaultValue = "0" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Sum(int b, int a = 0) => a + b;", ws.ReadFile("Calc.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ParamsArrayAfterOptional_IsAccepted()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "All",
            new() { OriginalName = "first", Name = "first", DefaultValue = "0" },
            new() { OriginalName = "rest", Name = "rest" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int All(int first = 0, params int[] rest)", ws.ReadFile("Calc.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static Task<RefactoringResult> ChangeAsync(TestWorkspace ws, string method, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams { SourceFile = ws.PathOf("Calc.cs"), MethodName = method, Parameters = parameters },
            TestContext.Current.CancellationToken);
}
