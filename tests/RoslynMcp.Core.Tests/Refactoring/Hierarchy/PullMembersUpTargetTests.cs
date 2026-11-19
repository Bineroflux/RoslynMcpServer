using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Tests.Refactoring.Signature;
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

    [Fact]
    public async Task UsingStaticAndAliasesTheMovedCodeUses_AreAddedToTheTargetsFile()
    {
        using var ws = TestWorkspace.Create(
            ("Base.cs", Lf("""
                using Col = System.Collections.Generic;

                namespace Core;

                public class Base
                {
                }
                """)),
            ("Derived.cs", Lf("""
                using System;
                using Core;
                using static System.Math;
                using static App.Units;
                using Col = System.Collections.Generic;
                using Sb = System.Text.StringBuilder;
                using Unused = System.Text.Encoding;

                namespace App;

                public static class Units
                {
                    public const int Scale = 10;

                    public enum Kind { Small, Large }
                }

                public class Derived : Base
                {
                    public int Bigger(int a, int b) => Max(a, b) * Scale;

                    public string Text(Kind kind) => new Sb().Append(kind).ToString();

                    public Col.List<int> Items() => new Col.List<int> { Abs(-1) };

                    [Obsolete("old")]
                    public int Old() => 1;
                }
                """)));

        var result = await PullAsync(ws, ["Bigger", "Text", "Items", "Old"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System;
            using static System.Math;
            using static App.Units;
            using Col = System.Collections.Generic;
            using Sb = System.Text.StringBuilder;

            namespace Core;

            public class Base
            {
                public int Bigger(int a, int b) => Max(a, b) * Scale;

                public string Text(Kind kind) => new Sb().Append(kind).ToString();

                public Col.List<int> Items() => new Col.List<int> { Abs(-1) };

                [Obsolete("old")]
                public int Old() => 1;
            }
            """), ws.ReadFile("Base.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ANameTheTargetUsesForSomethingElse_IsWrittenQualified()
    {
        using var ws = TestWorkspace.Create(
            ("Base.cs", Lf("""
                using System;
                using Sb = System.Text.Encoding;

                namespace Core;

                public class Base
                {
                    private static int Max(int a, int b) => a;

                    public Sb Encoding => Sb.UTF8;
                }

                public class Helper
                {
                }
                """)),
            ("Tools.cs", Lf("""
                namespace App.Tools;

                public class Helper
                {
                    public override string ToString() => "h";
                }
                """)),
            ("Derived.cs", Lf("""
                using App.Tools;
                using static System.Math;
                using Sb = System.Text.StringBuilder;

                namespace App;

                public class Derived : Core.Base
                {
                    public string Bigger(int a, int b) => new Sb().Append(Max(a, b)).Append(new Helper()).ToString();
                }
                """)));

        var result = await PullAsync(ws, ["Bigger"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(
            "    public string Bigger(int a, int b) => new System.Text.StringBuilder().Append(Math.Max(a, b)).Append(new App.Tools.Helper()).ToString();\n",
            ws.ReadFile("Base.cs"));
        Assert.StartsWith("using System;\nusing Sb = System.Text.Encoding;\n\nnamespace Core;", ws.ReadFile("Base.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MakeAbstract_OfABaseCreatedThroughANewConstraintOrActivator_IsRefused()
    {
        var source = Lf("""
            using System;
            using System.Collections.Generic;

            public class Base { }

            public class Derived : Base
            {
                public void Run() { }
            }

            public static class Factory
            {
                public static T Make<T>() where T : new() => new T();
                public static T Clone<T>(T item) where T : new() => new T();
                public static List<T> Wrap<T>(T item) => [item];
            }

            public sealed class Pool<T> where T : new()
            {
                public T Get() => new T();
            }

            public static class Use
            {
                public static object A() => Factory.Make<Base>();
                public static object B(Base item) => Factory.Clone(item);
                public static object C() => new Pool<Base>().Get();
                public static object D() => Activator.CreateInstance<Base>();
                public static object? E() => Activator.CreateInstance(typeof(Base));
                public static object F(Base item) => Factory.Wrap(item);
                public static List<Base> G() => new();
                public static object? H() => Activator.CreateInstance(Type.GetType("Base")!);
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, ["Run"], file: "C.cs", makeAbstract: true));

        Assert.Equal(ErrorCodes.BaseClassInstantiated, ex.ErrorCode);
        var path = ws.PathOf("C.cs");
        Assert.Equal(
            [$"{path}(25,41)", $"{path}(26,50)", $"{path}(27,37)", $"{path}(28,43)", $"{path}(29,44)"],
            Assert.IsType<List<string>>(ex.Details!["instantiations"]));
        Assert.Contains($"{path}(25,41) (the new() constraint of 'T' in 'Factory.Make<T>()')", ex.Message);
        Assert.Contains($"{path}(27,37) (the new() constraint of 'T' in 'Pool<T>')", ex.Message);
        Assert.Contains($"{path}(28,43) (Activator.CreateInstance)", ex.Message);
        Assert.Equal(source, ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task MakeAbstract_OfABaseOnlyUsedAsAnUnconstrainedTypeArgument_MakesItAbstract()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System.Collections.Generic;

            public class Base { }

            public class Derived : Base
            {
                public void Run() { }
            }

            public static class Use
            {
                public static List<Base> Items() => new() { new Derived() };
            }
            """)));

        var result = await PullAsync(ws, ["Run"], file: "C.cs", makeAbstract: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public abstract class Base", ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task StaticMembers_IntoAnInterface_AreDeclaredStaticAbstract_ImplementersAndTypeArgumentsReported()
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", Lf("""
            using System;
            using System.Collections.Generic;

            public interface IShape
            {
            }

            public sealed class Square : IShape
            {
                /// <summary>The kind.</summary>
                public static string Kind => "square";

                public static Square Create(int side) => new Square();

                public static event Action? Created;

                public static void Raise() => Created?.Invoke();
            }

            public sealed class Circle : IShape
            {
            }

            public static class Use
            {
                public static string KindOf<T>() where T : Square => Square.Kind;

                public static List<IShape> All() => new();
            }
            """)));

        var result = await PullAsync(ws, ["Kind", "Create", "Created"], file: "Shapes.cs", type: "Square", targetType: "IShape");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(Lf("""
            public interface IShape
            {
                /// <summary>The kind.</summary>
                static abstract string Kind { get; }

                static abstract Square Create(int side);

                static abstract event Action? Created;
            }
            """), ws.ReadFile("Shapes.cs"));
        Assert.Contains("public static string Kind => \"square\";", ws.ReadFile("Shapes.cs"));
        var warnings = string.Join("\n", result.Warnings!);
        Assert.Contains("error CS0535: 'Circle' does not implement interface member 'IShape.Kind'", warnings);
        Assert.Contains("error CS0535: 'Circle' does not implement interface member 'IShape.Create(int)'", warnings);
        Assert.Contains("error CS8920", warnings);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task StaticMembers_IntoAnInterface_BeforeCSharp11_AreRefused()
    {
        var source = Lf("""
            public interface IShape
            {
            }

            public sealed class Square : IShape
            {
                public static string Kind => "square";
            }
            """);
        using var ws = TestWorkspace.Create([("Shapes.cs", source)], [], languageVersion: LanguageVersion.CSharp10);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, ["Kind"], file: "Shapes.cs", type: "Square", targetType: "IShape"));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Contains("'Square.Kind' is static; it would be declared static abstract in 'IShape', which needs C# 11", ex.Message);
        Assert.Equal(source, ws.ReadFile("Shapes.cs"));
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
