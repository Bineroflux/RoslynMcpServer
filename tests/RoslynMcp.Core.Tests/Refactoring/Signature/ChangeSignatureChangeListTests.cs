using System.Reflection;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// change_signature's <c>parameters</c> is a list of changes. It used to be the new parameter
/// list itself, so a parameter the request did not list was dropped, and a new parameter with a
/// default value was written into every call. Now unlisted parameters stay, entries change,
/// move, remove or add one parameter each, and a rename that would clash with a name the body
/// already uses is refused.
/// </summary>
public sealed class ChangeSignatureChangeListTests
{
    private const string Source = """
        using System;
        using System.Linq;

        namespace App;

        public interface IScaler
        {
            int Scale(int value, int factor);
        }

        public class Calc : IScaler
        {
            private int total;

            public int Sum(int a, int b) => a + b;

            public int Twice(int value, bool unused) => value * 2;

            public int Digits(int a, int b, int c) => a * 100 + b * 10 + c;

            public int Mix(int a, int b = 2, int c = 3) => a * 100 + b * 10 + c;

            public int Scale(int value, int factor)
            {
                var result = value * factor;
                Func<int, int> twice = x => x * 2;
                return twice(result) / 2;
            }

            public int Add(int amount) { total += amount; return this.total; }

            public int Below(int[] items, int limit) => (from item in items where item < limit select item).Count();

            public bool Sized(object o, int size) => o is string s && s.Length == size;
        }

        public class LoudScaler : IScaler
        {
            public int Scale(int v, int f)
            {
                foreach (var step in new[] { 1 })
                    v += step - 1;
                return v * f;
            }
        }

        public static class Use
        {
            public static int CallSum() => new Calc().Sum(1, 2);
            public static int CallTwice() => new Calc().Twice(4, true);
            public static int CallDigits() => new Calc().Digits(1, 2, 3);
            public static int CallMixDefault() => new Calc().Mix(1);
            public static int CallMixB() => new Calc().Mix(1, 5);
            public static int CallMixNamed() => new Calc().Mix(1, c: 7);
            public static int CallScale() => new Calc().Scale(3, 4) + ((IScaler)new LoudScaler()).Scale(5, 6);
            public static int CallAdd() => new Calc().Add(5);
        }
        """;

    [Fact]
    public async Task UnlistedParameters_Stay()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Sum", new ParameterChange { OriginalName = "a", Name = "first" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Sum(int first, int b) => first + b;", text);
        Assert.Contains("new Calc().Sum(1, 2);", text);
        Assert.Equal(3, Run(ws, "CallSum"));
    }

    [Fact]
    public async Task ListOrder_DoesNotReorder()
    {
        // The old reading took the list as the new order; only newPosition moves a parameter now.
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Sum", Keep("b"), Keep("a"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Source, ws.ReadFile("Calc.cs"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("unused", "bool")]
    public async Task Remove_DropsTheParameterAndItsArguments(string? name, string? type)
    {
        // Fields equal to what the parameter has are accepted, as older clients sent them.
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Twice", new ParameterChange { OriginalName = "unused", Name = name, Type = type, Remove = true });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Twice(int value) => value * 2;", text);
        Assert.Contains("new Calc().Twice(4);", text);
        Assert.Equal(8, Run(ws, "CallTwice"));
    }

    [Theory]
    [InlineData(0, "the name 'flag'")]
    [InlineData(1, "the type 'int'")]
    [InlineData(2, "the defaultValue 'false'")]
    [InlineData(3, "a newPosition")]
    public async Task Remove_WithAnotherChange_IsRefused(int @case, string what)
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));
        ParameterChange change = @case switch
        {
            0 => new ParameterChange { OriginalName = "unused", Name = "flag", Remove = true },
            1 => new ParameterChange { OriginalName = "unused", Type = "int", Remove = true },
            2 => new ParameterChange { OriginalName = "unused", DefaultValue = "false", Remove = true },
            _ => new ParameterChange { OriginalName = "unused", NewPosition = 0, Remove = true }
        };

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, "Twice", change));

        Assert.Equal(ErrorCodes.InvalidParameterValue, ex.ErrorCode);
        Assert.Contains($"parameters[0] removes 'unused' but also gives it {what}.", ex.Message);
        Assert.Equal(Source, ws.ReadFile("Calc.cs"));
    }

    [Fact]
    public async Task NewPosition_MovesOneParameter_TheOthersKeepTheirOrder()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Digits", new ParameterChange { OriginalName = "c", NewPosition = 0 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Digits(int c, int a, int b) => a * 100 + b * 10 + c;", text);
        Assert.Contains("new Calc().Digits(3, 1, 2);", text);
        Assert.Equal(123, Run(ws, "CallDigits"));
    }

    [Fact]
    public async Task NewParameters_GoAtTheEnd_OrAtTheirPosition()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Digits",
            new ParameterChange { Name = "last", Type = "int" },
            new ParameterChange { Name = "second", Type = "string", NewPosition = 1 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Digits(int a, string second, int b, int c, int last)", text);
        Assert.Contains("new Calc().Digits(1, default /* TODO: second */, 2, 3, default /* TODO: last */);", text);
        Assert.Equal(2, result.Warnings!.Count);
    }

    [Fact]
    public async Task NewOptionalParameter_IsLeftOutOfTheCalls_AndLaterPositionalArgumentsAreNamed()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Mix", new ParameterChange { Name = "z", Type = "int", DefaultValue = "0", NewPosition = 1 });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Mix(int a, int z = 0, int b = 2, int c = 3)", text);
        Assert.Contains("new Calc().Mix(1);", text);
        Assert.Contains("new Calc().Mix(1, b: 5);", text);
        Assert.Contains("new Calc().Mix(1, c: 7);", text);
        Assert.Equal(1, result.ReferencesUpdated);
        Assert.Null(result.Warnings);
        Assert.Equal(123, Run(ws, "CallMixDefault"));
        Assert.Equal(153, Run(ws, "CallMixB"));
        Assert.Equal(127, Run(ws, "CallMixNamed"));
    }

    [Fact]
    public async Task ExistingDefault_Stays_WhenTheEntryGivesNone()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Mix", new ParameterChange { OriginalName = "b", Name = "middle" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Mix(int a, int middle = 2, int c = 3) => a * 100 + middle * 10 + c;", ws.ReadFile("Calc.cs"));
        Assert.Equal(123, Run(ws, "CallMixDefault"));
    }

    [Fact]
    public async Task EmptyDefaultValue_RemovesTheDefault_AndCallsThatLeftItOutPassIt()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Mix",
            new ParameterChange { OriginalName = "b", DefaultValue = "" },
            new ParameterChange { OriginalName = "c", DefaultValue = "" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Mix(int a, int b, int c) =>", text);
        Assert.Contains("new Calc().Mix(1, 2, 3);", text);
        Assert.Contains("new Calc().Mix(1, 5, 3);", text);
        Assert.Contains("new Calc().Mix(1, 2, c: 7);", text);
        Assert.Null(result.Warnings);
        Assert.Equal(123, Run(ws, "CallMixDefault"));
        Assert.Equal(153, Run(ws, "CallMixB"));
        Assert.Equal(127, Run(ws, "CallMixNamed"));
    }

    [Fact]
    public async Task EmptyDefaultValue_WithANewType_LeavesAPlaceholder()
    {
        // The old default need not fit the new type.
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Mix",
            new ParameterChange { OriginalName = "b", DefaultValue = "" },
            new ParameterChange { OriginalName = "c", Type = "long", DefaultValue = "" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("public int Mix(int a, int b, long c) =>", text);
        Assert.Contains("new Calc().Mix(1, 2, default /* TODO: c */);", text);
        Assert.Contains("new Calc().Mix(1, 2, c: 7);", text);
        Assert.Equal(2, result.Warnings!.Count(w => w.Contains("TODO: replace the placeholder", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task KeptDefaultValues_CountForTheRequiredAfterOptionalCheck()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, "Mix", new ParameterChange { Name = "d", Type = "int" }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
        Assert.Contains("Parameter 'd' has no defaultValue but comes after 'b'", ex.Message);
    }

    [Theory]
    [InlineData(0, ErrorCodes.ParameterNotFound, "parameters[0].originalName: the method has no parameter 'x'. Its parameters: a, b.")]
    [InlineData(1, ErrorCodes.InvalidParameterValue, "Parameter 'a' is listed twice, in parameters[0] and parameters[1].")]
    [InlineData(2, ErrorCodes.NameConflictScope, "Two parameters would be named 'b': 'a', renamed in parameters[0] and 'b'.")]
    [InlineData(3, ErrorCodes.NameConflictScope, "Two parameters would be named 'a': 'a' and the new parameter of parameters[0].")]
    [InlineData(4, ErrorCodes.InvalidParameterPosition, "parameters[0].newPosition 2 is outside the new parameter list, which has 2 parameters (positions 0 to 1).")]
    [InlineData(5, ErrorCodes.InvalidParameterPosition, "parameters[0] and parameters[1] both ask for newPosition 1.")]
    [InlineData(6, ErrorCodes.MissingRequiredParam, "parameters[0] removes a parameter but names none: give its originalName.")]
    [InlineData(7, ErrorCodes.MissingRequiredParam, "parameters[0] adds a parameter (it has no originalName), which needs a type.")]
    [InlineData(8, ErrorCodes.MissingRequiredParam, "parameters[0] adds a parameter (it has no originalName), which needs a name.")]
    [InlineData(9, ErrorCodes.InvalidParameterPosition, "parameters[0].newPosition must be 0 or more.")]
    public async Task InvalidChangeLists_AreRefused(int @case, string code, string message)
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));
        ParameterChange[] changes = @case switch
        {
            0 => [new ParameterChange { OriginalName = "x", Name = "y" }],
            1 => [new ParameterChange { OriginalName = "a", Name = "x" }, new ParameterChange { OriginalName = "a", NewPosition = 1 }],
            2 => [new ParameterChange { OriginalName = "a", Name = "b" }],
            3 => [new ParameterChange { Name = "a", Type = "int" }],
            4 => [new ParameterChange { OriginalName = "b", NewPosition = 2 }],
            5 => [new ParameterChange { OriginalName = "a", NewPosition = 1 }, new ParameterChange { OriginalName = "b", NewPosition = 1 }],
            6 => [new ParameterChange { Name = "a", Remove = true }],
            7 => [new ParameterChange { Name = "c" }],
            8 => [new ParameterChange { Type = "int" }],
            _ => [new ParameterChange { OriginalName = "a", NewPosition = -1 }]
        };

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ChangeAsync(ws, "Sum", changes));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
        Assert.Equal(Source, ws.ReadFile("Calc.cs"));
    }

    [Theory]
    [InlineData("Scale", "factor", "result", "Calc.Scale(int, int)", "the body declares a local 'result' (line 25)")]
    [InlineData("Scale", "factor", "x", "Calc.Scale(int, int)", "the body declares a lambda or local function parameter 'x' (line 26)")]
    [InlineData("Scale", "value", "step", "LoudScaler.Scale(int, int)", "the body declares a local 'step' (line 41)")]
    [InlineData("Below", "limit", "item", "Calc.Below(int[], int)", "the body declares a query range variable 'item' (line 32)")]
    [InlineData("Sized", "size", "s", "Calc.Sized(object, int)", "the body declares a local 's' (line 34)")]
    [InlineData("Add", "amount", "total", "Calc.Add(int)", "the body uses the field 'App.Calc.total' by that name (line 30), which would then mean the parameter")]
    public async Task RenameToANameTheBodyUses_IsRefused(string method, string parameter, string newName, string member, string clash)
    {
        // Scale renames the parameter at that position in every member of its family, so the
        // local of LoudScaler.Scale counts as well.
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ChangeAsync(ws, method, new ParameterChange { OriginalName = parameter, Name = newName }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Contains($"to '{newName}': {clash}.", ex.Message);
        Assert.Contains($"of 'App.{member}'", ex.Message);
        Assert.Equal(Source, ws.ReadFile("Calc.cs"));
    }

    [Fact]
    public async Task RenameToAnotherParameterNameOfAFamilyMember_IsRefused()
    {
        // LoudScaler.Scale names its parameters (v, f): renaming the first to f there clashes.
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ChangeAsync(ws, "Scale", new ParameterChange { OriginalName = "value", Name = "f" }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Contains("Two parameters of 'App.LoudScaler.Scale(int, int)' would be named 'f'", ex.Message);
    }

    [Fact]
    public async Task RenameToANameUsedOnlyAsAMember_IsAccepted()
    {
        // this.total is looked up as a member, so a parameter named total does not hide it;
        // the unqualified total += amount does, and becomes total += total.
        const string source = """
            namespace App;

            public class Counter
            {
                private int total;

                public int Add(int amount) { this.total += amount; return this.total; }
            }
            """;
        using var ws = TestWorkspace.Create(("Counter.cs", source));

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Counter.cs"),
                MethodName = "Add",
                Parameters = [new ParameterChange { OriginalName = "amount", Name = "total" }]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Add(int total) { this.total += total; return this.total; }", ws.ReadFile("Counter.cs"));
    }

    [Fact]
    public async Task SwappingTwoNames_IsAccepted()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Digits",
            new ParameterChange { OriginalName = "a", Name = "b" },
            new ParameterChange { OriginalName = "b", Name = "a" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Digits(int b, int a, int c) => b * 100 + a * 10 + c;", ws.ReadFile("Calc.cs"));
        Assert.Equal(123, Run(ws, "CallDigits"));
    }

    [Fact]
    public async Task RenameInTheFamily_ChangesEveryMember()
    {
        using var ws = TestWorkspace.Create(("Calc.cs", Source));

        var result = await ChangeAsync(ws, "Scale", new ParameterChange { OriginalName = "factor", Name = "multiplier" });

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Calc.cs");
        Assert.Contains("int Scale(int value, int multiplier);", text);
        Assert.Contains("var result = value * multiplier;", text);
        Assert.Contains("public int Scale(int v, int multiplier)", text);
        Assert.Contains("return v * multiplier;", text);
        Assert.Equal(42, Run(ws, "CallScale"));
    }

    /// <summary>Compiles the files on disk and runs a method of <c>App.Use</c>.</summary>
    private static object? Run(TestWorkspace ws, string method) =>
        Assembly.Load(ws.EmitDiskAssembly()).GetType("App.Use")!.GetMethod(method)!.Invoke(null, null);

    /// <summary>Changes a method of the source; Scale is Calc.Scale (its interface and LoudScaler declare one too).</summary>
    private static Task<RefactoringResult> ChangeAsync(TestWorkspace ws, string method, params ParameterChange[] parameters) =>
        new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Calc.cs"),
                MethodName = method,
                Line = method == "Scale" ? 23 : null,
                Parameters = parameters
            },
            TestContext.Current.CancellationToken);

    private static ParameterChange Keep(string name) => new ParameterChange { OriginalName = name, Name = name };
}
