using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// find_unused_symbols reports source types and members with no reference outside their own
/// declaration, leaves out what something other than a reference uses, and searches each
/// symbol only where it is visible.
/// </summary>
public sealed class FindUnusedSymbolsTests
{
    [Fact]
    public async Task ReportsWhatNothingUses_SelfReferencesDoNotCount()
    {
        const string source = """
            namespace App;

            internal class Calculator
            {
                private int _used;
                private int _unused;

                public int Add(int a, int b) => a + b + _used;

                private int Countdown(int n) => n == 0 ? 0 : Countdown(n - 1);

                private void Helper() { }

                internal static int Twice(int x) => x * 2;
            }

            internal class Orphan
            {
                public static Orphan Create() => new Orphan();
            }

            internal static class Program2
            {
                public static int Run() => new Calculator().Add(1, 2) + Calculator.Twice(3);
            }
            """;
        using var ws = TestWorkspace.Create(("Calculator.cs", source));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        Assert.Equal(
            ["App.Calculator._unused", "App.Calculator.Countdown(int)", "App.Calculator.Helper()", "App.Orphan", "App.Program2"],
            result.Symbols.Select(s => s.FullyQualifiedName));

        var countdown = result.Symbols.Single(s => s.Name == "Countdown");
        Assert.Equal(Contracts.Enums.SymbolKind.Method, countdown.Kind);
        Assert.Equal("App.Calculator", countdown.ContainingType);
        Assert.Equal("private", countdown.Accessibility);
        Assert.Equal(ws.PathOf("Calculator.cs"), countdown.File);
        Assert.Equal(10, countdown.Line);
        Assert.Equal(17, countdown.Column);
    }

    [Fact]
    public async Task NameofCrefAndExtensionMethodCalls_AreUses()
    {
        const string source = """
            namespace App;

            internal static class Extensions
            {
                public static int Doubled(this int x) => x * 2;
            }

            /// <summary>See <see cref="Documented"/>.</summary>
            internal class Documented { }

            internal class Named { }

            internal class Consumer
            {
                public int Use() => 3.Doubled() + nameof(Named).Length;
            }
            """;
        using var ws = TestWorkspace.Create(("Use.cs", source));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        // Documented's only reference is its own documentation comment.
        Assert.Equal(["App.Documented", "App.Consumer"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task CrefFromAnotherType_IsAUse()
    {
        const string source = """
            namespace App;

            internal class Target { }

            /// <summary>Works with <see cref="Target"/>.</summary>
            internal class Documenting { }
            """;
        using var ws = TestWorkspace.Create(("Doc.cs", source));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        Assert.Equal(["App.Documenting"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task PublicSymbols_OnlyWithIncludePublic_ButAPublicMemberOfAnInternalTypeIsInternal()
    {
        const string source = """
            namespace App;

            public class Api
            {
                public void Exposed() { }
                protected void ForDerived() { }
                internal void Hidden() { }
            }

            internal class Internal
            {
                public void LooksPublic() { }
            }

            public sealed class Sealed
            {
                public void Run() => Guarded();
                private protected void Guarded() { }
            }
            """;
        using var ws = TestWorkspace.Create(("Api.cs", source));

        var byDefault = await FindAsync(ws, new FindUnusedSymbolsParams());
        var methods = await FindAsync(ws, new FindUnusedSymbolsParams { KindFilter = "Method" });
        var all = await FindAsync(ws, new FindUnusedSymbolsParams { IncludePublic = true });
        var allMethods = await FindAsync(ws, new FindUnusedSymbolsParams { IncludePublic = true, KindFilter = "Method" });

        Assert.Equal(["App.Api.Hidden()", "App.Internal"], byDefault.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(["App.Api.Hidden()", "App.Internal.LooksPublic()"], methods.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal("public", methods.Symbols.Single(s => s.Name == "LooksPublic").Accessibility);
        Assert.Equal(["App.Api", "App.Internal", "App.Sealed"], all.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(
            ["App.Api.Exposed()", "App.Api.ForDerived()", "App.Api.Hidden()", "App.Internal.LooksPublic()", "App.Sealed.Run()"],
            allMethods.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task ImplicitlyUsedMembers_AreLeftOut()
    {
        const string source = """
            using System;
            using System.Runtime.InteropServices;

            namespace App;

            internal interface IShape { double Area(); event EventHandler Changed; }

            internal abstract class Base
            {
                public abstract void Draw();
                public virtual void Reset() { }
            }

            internal sealed class Square : Base, IShape, IComparable<Square>
            {
                private Square() { }
                public Square(int side) { Side = side; }
                ~Square() { }
                static Square() { }

                public int Side { get; }
                public double Area() => Side * Side;
                public event EventHandler? Changed;
                int IComparable<Square>.CompareTo(Square? other) => 0;
                public override void Draw() { }
                public override void Reset() { }
                public static Square operator +(Square a, Square b) => a;
                public static implicit operator int(Square s) => s.Side;
                public override string ToString() => "square";

                [Obsolete("old")] private void Deprecated() { }
                [STAThread] private void Attributed() { }
                partial void Hook();
                [DllImport("user32.dll")] private static extern int MessageBeep(uint type);
            }

            internal enum Color { Red, Green }

            [StructLayout(LayoutKind.Sequential)]
            internal struct Native { public int Unread; }

            internal static class Entry
            {
                public static void Main() { Use(new Square(2)); }
                private static void Use(IShape shape) { shape.Area(); Color c = Color.Red; _ = new Native(); _ = c; }
            }
            """;
        using var ws = TestWorkspace.Create(
            [("Shapes.cs", source.Replace("internal sealed class Square", "internal sealed partial class Square"))],
            [],
            defaultNamespace: null);

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        // Square.Changed is an implementation (IShape.Changed is never raised or subscribed to),
        // Draw and Reset are never called, Deprecated's [Obsolete] does not count as an attribute,
        // and in a library Main is an ordinary method (it goes with its unused type).
        Assert.Equal(
            ["App.IShape.Changed", "App.Base.Draw()", "App.Base.Reset()", "App.Square.Deprecated()", "App.Entry"],
            result.Symbols.Select(s => s.FullyQualifiedName));

        var attributed = await FindAsync(ws, new FindUnusedSymbolsParams { IncludeAttributed = true });
        Assert.Contains(attributed.Symbols, s => s.Name == "Attributed");
        Assert.Contains(attributed.Symbols, s => s.FullyQualifiedName == "App.Native.Unread");
        Assert.DoesNotContain(attributed.Symbols, s => s.Name == "MessageBeep");
    }

    [Fact]
    public async Task TheEntryPointAndItsType_AreLeftOut()
    {
        const string source = """
            namespace App;

            internal static class Program
            {
                private static void Main() { }
                private static void Unused() { }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", source));
        ws.Context.UpdateSolution(ws.Context.Solution.WithProjectCompilationOptions(
            ws.Context.Solution.ProjectIds[0],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication)));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        Assert.Equal(["App.Program.Unused()"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task InterfaceMembersNobodyCalls_AreReported_EvenWhenImplementedExplicitly()
    {
        const string source = """
            namespace App;

            internal interface IStore { int Load(); void Save(); }

            internal sealed class Store : IStore
            {
                public int Load() => 1;
                void IStore.Save() { }
            }

            internal static class Use
            {
                public static int Run(IStore store) => store.Load() + new Store().Load();
            }
            """;
        using var ws = TestWorkspace.Create(("Store.cs", source));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        Assert.Equal(["App.IStore.Save()", "App.Use"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task InternalsUsedByAFriendProject_AreUsed_OthersAreNot()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", """
                [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("App")]
                namespace Lib;

                internal static class Shared
                {
                    public static int ForFriends() => 1;
                    public static int ForNobody() => 2;
                }

                public static class Api
                {
                    public static int Called() => 3;
                    public static int NotCalled() => 4;
                }
                """)]),
            new TestWorkspace.ProjectSpec("App", [("App.cs", """
                namespace App;

                public static class Client
                {
                    public static int Run() => Lib.Shared.ForFriends() + Lib.Api.Called();
                }
                """)], References: ["Lib"]));

        var lib = await FindAsync(ws, new FindUnusedSymbolsParams { ProjectPath = ws.ProjectPathOf("Lib") });
        var libPublic = await FindAsync(ws, new FindUnusedSymbolsParams { ProjectPath = ws.ProjectPathOf("Lib"), IncludePublic = true });

        Assert.Equal(["Lib.Shared.ForNobody()"], lib.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(["Lib.Shared.ForNobody()", "Lib.Api.NotCalled()"], libPublic.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task ASymbolUsedInOneTargetFramework_IsUsed()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec(
                "Lib",
                [("Lib.cs", """
                    namespace Lib;

                    internal static class Helpers
                    {
                        public static int Modern() => 1;
                        public static int Never() => 2;

                    #if NET9_0
                        public static int Use() => Modern();
                    #endif
                    }

                    public static class Api
                    {
                        public static System.Type HelperType => typeof(Helpers);
                    }
                    """)],
                TargetFrameworks: ["net8_0", "net9_0"]));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        // Use exists in one framework and is unused there; Modern is used there.
        Assert.Equal(["Lib.Helpers.Never()", "Lib.Helpers.Use()"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task GeneratedFiles_DeclareNothingReported_ButTheirReferencesCount()
    {
        using var ws = TestWorkspace.Create(
            ("Model.cs", "namespace App;\n\ninternal class Model\n{\n    public int UsedByGenerated() => 1;\n    public int Unused() => 2;\n}\n"),
            ("Model.g.cs", "// <auto-generated/>\nnamespace App;\n\ninternal static class ModelGenerated\n{\n    public static int Call(Model m) => m.UsedByGenerated();\n}\n"));

        var result = await FindAsync(ws, new FindUnusedSymbolsParams());

        Assert.Equal(["App.Model.Unused()"], result.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task KindFilterSourceFileAndMaxResults_NarrowTheReport_TotalCountCountsAll()
    {
        using var ws = TestWorkspace.Create(
            ("A.cs", "namespace App;\n\npublic class A\n{\n    private int _a;\n    private void M1() { }\n    private void M2() { }\n}\n"),
            ("B.cs", "namespace App;\n\npublic class B\n{\n    private void M3() { }\n}\n"));

        var methods = await FindAsync(ws, new FindUnusedSymbolsParams { KindFilter = "method" });
        var inB = await FindAsync(ws, new FindUnusedSymbolsParams { SourceFile = ws.PathOf("B.cs") });
        var firstTwo = await FindAsync(ws, new FindUnusedSymbolsParams { MaxResults = 2 });
        var again = await FindAsync(ws, new FindUnusedSymbolsParams { MaxResults = 2 });

        Assert.Equal(["App.A.M1()", "App.A.M2()", "App.B.M3()"], methods.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(["App.B.M3()"], inB.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(["App.A._a", "App.A.M1()"], firstTwo.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(4, firstTwo.TotalCount);
        Assert.True(firstTwo.Truncated);
        Assert.Equal(firstTwo.Symbols.Select(s => s.FullyQualifiedName), again.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task ATypeWhoseOnlyUsesAreItsMembersFromOutside_IsUsed_AndKindFilterKeepsThatTrue()
    {
        const string source = """
            namespace App;

            internal static class Constants
            {
                internal static class Limits
                {
                    public const int Max = 10;
                }
            }

            internal static class Holder
            {
                internal static int Value;
            }

            internal static class Use
            {
                public static int Run() => Holder.Value;
            }

            internal static class Imported
            {
                public static int Get() => 1;
            }
            """;
        using var ws = TestWorkspace.Create(
            ("Constants.cs", source),
            ("Other.cs", "using static App.Imported;\n\nnamespace App;\n\ninternal static class Other\n{\n    public static int Max() => Constants.Limits.Max + Get();\n}\n"));

        var classes = await FindAsync(ws, new FindUnusedSymbolsParams { KindFilter = "Class" });

        Assert.Equal(["App.Use", "App.Other"], classes.Symbols.Select(s => s.FullyQualifiedName));
    }

    [Fact]
    public async Task TheMembersOfAnUnusedType_AreNotListedSeparately_UnlessTypesAreFilteredOut()
    {
        using var ws = TestWorkspace.Create(
            ("Orphan.cs", "namespace App;\n\ninternal class OrphanType\n{\n    public void Work() { }\n\n    internal class Inner { public int X; }\n}\n"),
            ("Part1.cs", "namespace App;\n\ninternal partial class PartialThing\n{\n    public int A;\n}\n"),
            ("Part2.cs", "namespace App;\n\ninternal partial class PartialThing\n{\n    public int B;\n}\n"));

        var all = await FindAsync(ws, new FindUnusedSymbolsParams());
        var fields = await FindAsync(ws, new FindUnusedSymbolsParams { KindFilter = "Field" });
        var inPart2 = await FindAsync(ws, new FindUnusedSymbolsParams { SourceFile = ws.PathOf("Part2.cs") });

        Assert.Equal(["App.OrphanType", "App.PartialThing"], all.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(
            ["App.OrphanType.Inner.X", "App.PartialThing.A", "App.PartialThing.B"],
            fields.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(["App.PartialThing"], inPart2.Symbols.Select(s => s.FullyQualifiedName));
        Assert.Equal(ws.PathOf("Part2.cs"), inPart2.Symbols[0].File);
    }

    [Theory]
    [InlineData("Namespace")]
    [InlineData("Local")]
    [InlineData("Banana")]
    public async Task KindFilter_MustNameATypeOrMemberKind(string kindFilter)
    {
        using var ws = TestWorkspace.Create(("A.cs", "namespace App;\n"));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            FindAsync(ws, new FindUnusedSymbolsParams { KindFilter = kindFilter }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
    }

    [Fact]
    public async Task MaxResultsBelowOne_IsRejected()
    {
        using var ws = TestWorkspace.Create(("A.cs", "namespace App;\n"));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            FindAsync(ws, new FindUnusedSymbolsParams { MaxResults = 0 }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    private static async Task<FindUnusedSymbolsResult> FindAsync(TestWorkspace ws, FindUnusedSymbolsParams @params)
    {
        var result = await new FindUnusedSymbolsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }
}
