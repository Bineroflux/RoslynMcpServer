using System.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// pull_members_up moves members into the base class (or declares them in an interface), keeping
/// the code compiling: accessibility, dependencies left behind, conflicts, generics and usings.
/// </summary>
public sealed class PullMembersUpTests
{
    [Fact]
    public async Task Members_MoveWithTheirComments_PrivateBecomesProtected_TheDerivedCopiesGo()
    {
        const string source = """
            using System;

            namespace App;

            public class Animal
            {
                public string Name = "";
            }

            public class Dog : Animal
            {
                private int _legs = 4, _tails = 1;

                /// <summary>Barks.</summary>
                [Obsolete("old")]
                public string Bark() => Name + " barks";

                private int Legs() => _legs;

                public event EventHandler? Barked;

                public event EventHandler Wagged { add { } remove { } }

                public int this[int index] => index * _tails;

                public int Tails => _tails;
            }
            """;
        using var ws = TestWorkspace.Create(("Animals.cs", Lf(source)));

        var result = await PullAsync(ws, "Animals.cs", "Dog", ["Bark", "Legs", "_legs", "_tails", "Barked", "Wagged", "this", "Tails"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System;

            namespace App;

            public class Animal
            {
                public string Name = "";
                protected int _legs = 4, _tails = 1;

                public event EventHandler? Barked;

                /// <summary>Barks.</summary>
                [Obsolete("old")]
                public string Bark() => Name + " barks";

                protected int Legs() => _legs;

                public event EventHandler Wagged { add { } remove { } }

                public int this[int index] => index * _tails;

                public int Tails => _tails;
            }

            public class Dog : Animal
            {
            }
            """), ws.ReadFile("Animals.cs"));
        Assert.Equal(
            ["'App.Dog._legs' was private; it is protected in 'Animal'.", "'App.Dog.Legs()' was private; it is protected in 'Animal'."],
            result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task RemovingEveryMemberButTheLast_LeavesNoBlankLineAfterTheBrace()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public class Base
            {
            }

            public class Derived : Base
            {
                private int _legs = 4;

                public int Legs() => _legs;

                /// <summary>Kept.</summary>
                public string Fetch() => "stick";

                public int Twice() => Legs() * 2;
            }
            """)));

        var result = await PullAsync(ws, "C.cs", "Derived", ["_legs", "Legs", "Twice"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.EndsWith(Lf("""
            public class Derived : Base
            {
                /// <summary>Kept.</summary>
                public string Fetch() => "stick";
            }
            """), ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task OneOfSeveralVariables_MovesAlone()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public class Base { }

            public class Derived : Base
            {
                public int A = 1, B = 2, C = 3;
            }
            """)));

        var result = await PullAsync(ws, "C.cs", "Derived", ["B"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            public class Base
            {
                public int B = 2;
            }

            public class Derived : Base
            {
                public int A = 1, C = 3;
            }
            """), ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task AMemberUsingWhatStaysBehind_IsRefusedWithTheDependencies()
    {
        var source = Lf("""
            public class Base { }

            public class Derived : Base
            {
                private int _count;
                private int Helper() => 1;
                public int Total() => _count + Helper();
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["Total"]));
        var together = await PullAsync(ws, "C.cs", "Derived", ["Total", "_count", "Helper"]);

        Assert.Equal(ErrorCodes.MemberDependsOnDerived, ex.ErrorCode);
        Assert.Contains("'Derived._count', 'Derived.Helper()'", ex.Message);
        Assert.Equal(["_count", "Helper"], (List<string>)ex.Details!["dependencies"]);
        Assert.True(together.Success, together.Error?.Message);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ConflictsWithTheBase_AreRefused_AnOverloadIsNot()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public class Base
            {
                public virtual void Log(string message) { }
                public void Write(int value) { }
            }

            public class Derived : Base
            {
                public override void Log(string message) { }
                public void Write(int other) { }
                public void Write(string text) { }
            }
            """)));

        var overrides = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["Log"]));
        var sameSignature = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["Write"]));

        Assert.Equal(ErrorCodes.MemberConflict, overrides.ErrorCode);
        Assert.Contains("which 'Base' declares itself", overrides.Message);
        Assert.Equal(ErrorCodes.MemberConflict, sameSignature.ErrorCode);
        Assert.Contains("already declares 'Base.Write(int)'", sameSignature.Message);
    }

    [Fact]
    public async Task MakeAbstract_DeclaresAbstractMembers_KeepsOverrides_AndMakesTheBaseAbstract()
    {
        const string source = """
            using System;

            public class Shape
            {
            }

            public class Circle : Shape
            {
                public double Radius { get; set; }

                /// <summary>The area.</summary>
                public virtual double Area() => Math.PI * Radius * Radius;

                public string Name { get; protected set; } = "circle";

                public event EventHandler? Changed;

                public T Convert<T>(T value) where T : struct => value;

                public double this[int factor] => Area() * factor;
            }

            public class Square : Shape
            {
            }
            """;
        using var ws = TestWorkspace.Create(("Shapes.cs", Lf(source)));

        var result = await PullAsync(ws, "Shapes.cs", "Circle", ["Area", "Name", "Changed", "Convert", "this"], makeAbstract: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System;

            public abstract class Shape
            {
                public abstract event EventHandler? Changed;

                /// <summary>The area.</summary>
                public abstract double Area();

                public abstract string Name { get; protected set; }

                public abstract T Convert<T>(T value) where T : struct;

                public abstract double this[int factor] { get; }
            }

            public class Circle : Shape
            {
                public double Radius { get; set; }

                /// <summary>The area.</summary>
                public override double Area() => Math.PI * Radius * Radius;

                public override string Name { get; protected set; } = "circle";

                public override event EventHandler? Changed;

                public override T Convert<T>(T value) => value;

                public override double this[int factor] => Area() * factor;
            }

            public class Square : Shape
            {
            }
            """), ws.ReadFile("Shapes.cs"));
        Assert.Contains("'Shape' was not abstract and is never instantiated; it is abstract now.", result.Warnings!);
        Assert.Contains(result.Warnings!, w => w.Contains("error CS0534", StringComparison.Ordinal) && w.Contains("Square", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task MakeAbstract_OfAnInstantiatedBase_IsRefused()
    {
        var source = Lf("""
            public class Base { }

            public class Derived : Base
            {
                public void Run() { }
            }

            public static class Factory
            {
                public static Base Make() => new Base();
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["Run"], makeAbstract: true));

        Assert.Equal(ErrorCodes.BaseClassInstantiated, ex.ErrorCode);
        Assert.Contains("C.cs(10,", ex.Message);
        Assert.Equal(source, ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task ASiblingWithTheSameMember_IsReportedAsHidingIt()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public class Base { }
            public class Left : Base { public int Size() => 1; }
            public class Right : Base { public int Size() => 2; }
            """)));

        var result = await PullAsync(ws, "C.cs", "Left", ["Size"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(["'Right.Size()' has the name of the pulled-up 'Size' and now hides it (CS0108); make it 'new', an override, or pull it up as well."], result.Warnings);
    }

    [Fact]
    public async Task GenericDerivedType_IsTranslatedIntoTheBasesTypeParameters()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System.Collections.Generic;

            public class Repository<T>
            {
            }

            public class Cache<U> : Repository<U>
            {
                public List<U> Items = new List<U>();
                public U? First() => Items.Count > 0 ? Items[0] : default(U);
            }

            public class Numbers : Repository<int>
            {
                public int Sum(List<int> values) => values.Count;
            }

            public class Mixed<V> : Repository<int>
            {
                public V? Value() => default;
            }
            """)));

        var cache = await PullAsync(ws, "C.cs", "Cache", ["Items", "First"]);
        var numbers = await PullAsync(ws, "C.cs", "Numbers", ["Sum"]);
        var mixed = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Mixed", ["Value"]));

        Assert.True(cache.Success, cache.Error?.Message);
        Assert.True(numbers.Success, numbers.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("    public List<T> Items = new List<T>();\n", text);
        Assert.Contains("    public T? First() => Items.Count > 0 ? Items[0] : default(T);\n", text);
        Assert.Contains("    public int Sum(List<int> values) => values.Count;\n", text);
        Assert.Equal(ErrorCodes.TypeNotExpressible, mixed.ErrorCode);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task IntoAnInterface_AddsSignatures_KeepsTheImplementation_OtherImplementersReported()
    {
        const string source = """
            using System;

            public interface IShape
            {
                double Area();
            }

            public class Square : IShape
            {
                public double Side { get; private set; }

                /// <summary>The area.</summary>
                public double Area() => Side * Side;

                public string Describe(string prefix = ">") => prefix + Side;

                public event EventHandler? Resized;

                private int Hidden() => 1;
            }

            public class Dot : IShape
            {
                public double Area() => 0;
            }
            """;
        using var ws = TestWorkspace.Create(("Shapes.cs", Lf(source)));

        var hidden = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Shapes.cs", "Square", ["Hidden"], targetType: "IShape"));
        var result = await PullAsync(ws, "Shapes.cs", "Square", ["Side", "Describe", "Resized"], targetType: "IShape");

        Assert.Equal(ErrorCodes.MemberNotPublic, hidden.ErrorCode);
        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Shapes.cs");
        Assert.Contains("""
            public interface IShape
            {
                double Area();

                double Side { get; }

                string Describe(string prefix = ">");

                event EventHandler? Resized;
            }
            """.ReplaceLineEndings("\n"), text);
        Assert.Contains("public double Side { get; private set; }", text);
        Assert.Contains(result.Warnings!, w => w.Contains("error CS0535", StringComparison.Ordinal) && w.Contains("Dot", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task BaseInAnotherFileAndNamespace_GetsTheUsingsTheMovedCodeNeeds()
    {
        using var ws = TestWorkspace.Create(
            ("Base.cs", Lf("""
                namespace Core;

                public class Base
                {
                }
                """)),
            ("Derived.cs", Lf("""
                using System.Linq;
                using System.Text;
                using Core;

                namespace App;

                public class Derived : Base
                {
                    public string Join(int[] values) => new StringBuilder().Append(values.Where(v => v > 0).Count()).ToString();
                }
                """)));

        var result = await PullAsync(ws, "Derived.cs", "Derived", ["Join"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System.Linq;
            using System.Text;

            namespace Core;

            public class Base
            {
                public string Join(int[] values) => new StringBuilder().Append(values.Where(v => v > 0).Count()).ToString();
            }
            """), ws.ReadFile("Base.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task BaseInAnotherProject_PrivateProtectedBecomesProtected_AppOnlyTypesAreRefused()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Base.cs", "namespace Lib;\n\npublic class Base\n{\n}\n")]),
            new TestWorkspace.ProjectSpec("App", [("Derived.cs", Lf("""
                namespace App;

                public class Options { }

                public class Derived : Lib.Base
                {
                    private protected int Count() => 1;
                    public Options Make() => new Options();
                }
                """))], References: ["Lib"]));

        var appOnly = await Assert.ThrowsAsync<RefactoringException>(() => Pull(ws.Context, ws.PathOf("App", "Derived.cs"), "Derived", ["Make"]));
        var result = await Pull(ws.Context, ws.PathOf("App", "Derived.cs"), "Derived", ["Count"]);

        Assert.Equal(ErrorCodes.MemberNotMoveable, appOnly.ErrorCode);
        Assert.Contains("'App.Options' from App, which 'Lib' does not reference", appOnly.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace Lib;\n\npublic class Base\n{\n    protected int Count() => 1;\n}\n", File.ReadAllText(ws.PathOf("Lib", "Base.cs")));
        Assert.Equal(["'App.Derived.Count()' was private protected; it is protected in 'Base', which is in another assembly."], result.Warnings);
    }

    [Fact]
    public async Task BaseCallsThatWouldChangeMeaning_AreRefused_OverridesOfFartherMembersMove()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public class Base
            {
                public virtual string Name() => "base";
            }

            public class Derived : Base
            {
                public string Both() => base.Name() + "!";
                public override string ToString() => "derived";
            }
            """)));

        var baseCall = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["Both"]));
        var toString = await PullAsync(ws, "C.cs", "Derived", ["ToString"]);

        Assert.Equal(ErrorCodes.MemberNotMoveable, baseCall.ErrorCode);
        Assert.True(toString.Success, toString.Error?.Message);
        Assert.Contains("    public override string ToString() => \"derived\";\n}\n\npublic class Derived", ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task MakeAbstract_OfAnOverrideOfAFartherMember_DeclaresItAbstractOverride()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public abstract class Base { }

            public class Derived : Base
            {
                public override string ToString() => "derived";
            }
            """)));

        var result = await PullAsync(ws, "C.cs", "Derived", ["ToString"], makeAbstract: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            public abstract class Base
            {
                public abstract override string ToString();
            }

            public class Derived : Base
            {
                public override string ToString() => "derived";
            }
            """), ws.ReadFile("C.cs"));
        Assert.Null(result.Warnings);
    }

    [Fact]
    public async Task Preview_DescribesTheChanges_AndWritesNothing()
    {
        var source = Lf("""
            public class Base { }

            public class Derived : Base
            {
                public int Value() => 1;
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var result = await PullAsync(ws, "C.cs", "Derived", ["Value"], preview: true);

        Assert.True(result.Preview);
        Assert.Equal(source, ws.ReadFile("C.cs"));
        Assert.Equal(["Add 'Value' to 'Base'", "Remove the moved declaration"], result.PendingChanges!.Select(c => c.Description));
        Assert.Equal("public int Value() => 1;", result.PendingChanges![0].AfterSnippet);
        Assert.Equal("public int Value() => 1;", result.PendingChanges![1].BeforeSnippet);
    }

    [Fact]
    public async Task CrlfFileWithBom_AndLfFileWithout_KeepTheirEncodingAndLineEndings()
    {
        const string baseText = "public class Base\r\n{\r\n}\r\n";
        const string derivedText = "public class Derived : Base\n{\n    /// <summary>One.</summary>\n    public int One() => 1;\n\n    public int Two() => 2;\n}\n";
        using var ws = TestWorkspace.Create(("Base.cs", baseText), ("Derived.cs", derivedText));
        File.WriteAllText(ws.PathOf("Base.cs"), baseText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await PullAsync(ws, "Derived.cs", "Derived", ["One"]);

        Assert.True(result.Success, result.Error?.Message);
        var baseBytes = File.ReadAllBytes(ws.PathOf("Base.cs"));
        Assert.True(baseBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("public class Base\r\n{\r\n    /// <summary>One.</summary>\r\n    public int One() => 1;\r\n}\r\n", Encoding.UTF8.GetString(baseBytes[3..]));
        var derivedBytes = File.ReadAllBytes(ws.PathOf("Derived.cs"));
        Assert.False(derivedBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("public class Derived : Base\n{\n    public int Two() => 2;\n}\n", Encoding.UTF8.GetString(derivedBytes));
    }

    [Fact]
    public async Task MultiTargetedProject_EditsTheFileOnce_AndKeepsItsDocumentsInStep()
    {
        using var ws = TestWorkspace.CreateSolution(new TestWorkspace.ProjectSpec(
            "Lib",
            [("C.cs", "public class Base\n{\n}\n\npublic class Derived : Base\n{\n    public int One() => 1;\n}\n")],
            TargetFrameworks: ["net8", "net9"]));

        var result = await Pull(ws.Context, ws.PathOf("Lib", "C.cs"), "Derived", ["One"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal([ws.PathOf("Lib", "C.cs")], result.Changes!.FilesModified);
        Assert.Equal("public class Base\n{\n    public int One() => 1;\n}\n\npublic class Derived : Base\n{\n}\n", File.ReadAllText(ws.PathOf("Lib", "C.cs")));
        var texts = ws.Context.Solution.GetDocumentIdsWithFilePath(ws.PathOf("Lib", "C.cs"))
            .Select(id => ws.Context.Solution.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken).Result.ToString())
            .Distinct();
        Assert.Single(texts);
    }

    [Fact]
    public async Task NoBaseClassUnknownTargetAndUnknownMember_AreErrors()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            public interface IThing { }
            public class Alone : IThing { public void Run() { } }
            """)));

        var noBase = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Alone", ["Run"]));
        var unknownTarget = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Alone", ["Run"], targetType: "IOther"));
        var unknownMember = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Alone", ["Walk"], targetType: "IThing"));

        Assert.Equal(ErrorCodes.BaseClassNotFound, noBase.ErrorCode);
        Assert.Equal(ErrorCodes.InterfaceNotFound, unknownTarget.ErrorCode);
        Assert.Equal(ErrorCodes.MemberNotFound, unknownMember.ErrorCode);
        Assert.Contains("Its members: Run", unknownMember.Message);
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> PullAsync(
        TestWorkspace ws,
        string file,
        string type,
        string[] members,
        string? targetType = null,
        bool makeAbstract = false,
        bool preview = false) =>
        Pull(ws.Context, ws.PathOf(file), type, members, targetType, makeAbstract, preview);

    private static Task<RefactoringResult> Pull(
        WorkspaceContext context,
        string file,
        string type,
        string[] members,
        string? targetType = null,
        bool makeAbstract = false,
        bool preview = false) =>
        new PullMembersUpOperation(context).ExecuteAsync(
            new PullMembersUpParams
            {
                SourceFile = file,
                TypeName = type,
                Members = members,
                TargetType = targetType,
                MakeAbstract = makeAbstract,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
