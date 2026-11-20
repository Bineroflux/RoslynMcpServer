using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// pull_members_up with user-defined operators: declared static abstract in an interface with a
/// self type parameter (generic math style), the operators the compiler wants together pulled up
/// with them, and refused where C# can't declare them (a base class, an interface without a self
/// type parameter, before C# 11).
/// </summary>
public sealed class PullMembersUpOperatorTests
{
    private static readonly string Interface = Lf("""
        namespace Core;

        public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
        {
        }
        """);

    [Fact]
    public async Task BinaryAndUnaryOperators_AreDeclaredStaticAbstract_WithTheSelfTypeParameterForTheType()
    {
        using var ws = TestWorkspace.Create(
            ("IAddable.cs", Interface),
            ("Money.cs", Lf("""
                using System.Linq;
                using Core;

                namespace App;

                public readonly struct Money : IAddable<Money>
                {
                    public Money(decimal amount) => Amount = amount;

                    public decimal Amount { get; }

                    /// <summary>Adds two amounts.</summary>
                    public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);

                    public static Money operator +(Money left, decimal right) => new(left.Amount + right);

                    public static Money operator -(Money value)
                    {
                        return new(-value.Amount);
                    }

                    public static Money[] operator *(Money value, int count) => Enumerable.Repeat(value, count).ToArray();
                }

                public static class Sums
                {
                    public static T Sum<T>(T first, T second) where T : IAddable<T> => first + second;

                    public static T Negate<T>(T value) where T : IAddable<T> => -value;
                }
                """)));

        var result = await PullAsync(ws, "Money.cs", "Money", ["operator +", "op_UnaryNegation", "*"]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace Core;

            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
                /// <summary>Adds two amounts.</summary>
                static abstract TSelf operator +(TSelf left, TSelf right);

                static abstract TSelf operator +(TSelf left, decimal right);

                static abstract TSelf operator -(TSelf value);

                static abstract TSelf[] operator *(TSelf value, int count);
            }
            """), ws.ReadFile("IAddable.cs"));
        Assert.Contains("    public static Money operator -(Money value)\n    {\n        return new(-value.Amount);\n    }\n", ws.ReadFile("Money.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ACheckedOperator_AndItsUncheckedVersion_GoTogether()
    {
        const string money = """
            using Core;

            namespace App;

            public sealed class Money : IAddable<Money>
            {
                public int Cents;

                public static Money operator +(Money a, Money b) => new() { Cents = a.Cents + b.Cents };

                public static Money operator checked +(Money a, Money b) => new() { Cents = checked(a.Cents + b.Cents) };
            }
            """;
        const string expected = """
            namespace Core;

            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
                static abstract TSelf operator +(TSelf a, TSelf b);

                static abstract TSelf operator checked +(TSelf a, TSelf b);
            }
            """;
        using var ws = TestWorkspace.Create(("IAddable.cs", Interface), ("Money.cs", Lf(money)));
        using var other = TestWorkspace.Create(("IAddable.cs", Interface), ("Money.cs", Lf(money)));

        var fromChecked = await PullAsync(ws, "Money.cs", "Money", ["operator checked +"]);
        var fromUnchecked = await PullAsync(other, "Money.cs", "Money", ["+"]);

        Assert.True(fromChecked.Success, fromChecked.Error?.Message);
        Assert.Equal(Lf(expected), ws.ReadFile("IAddable.cs"));
        Assert.Equal(
            ["'App.Money.operator +(App.Money, App.Money)' is pulled up too: 'operator checked +' needs its unchecked version declared with it (CS9025)."],
            fromChecked.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());

        Assert.True(fromUnchecked.Success, fromUnchecked.Error?.Message);
        Assert.Equal(Lf(expected), other.ReadFile("IAddable.cs"));
        Assert.Equal(
            ["'App.Money.operator checked +(App.Money, App.Money)' is pulled up too: it is the checked version of 'operator +', which code in a checked context calls instead."],
            fromUnchecked.Warnings);
        Assert.Empty(other.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ComparisonOperators_BothOfAPairNamed_OrTheMissingOnePulledToo_AndCallsThatChangeAreReported()
    {
        using var ws = TestWorkspace.Create(("Money.cs", Lf("""
            public interface IOrdered<TSelf> where TSelf : IOrdered<TSelf>
            {
            }

            public sealed class Money : IOrdered<Money>
            {
                public int Cents;

                public static bool operator <(Money a, Money b) => a.Cents < b.Cents;

                public static bool operator >(Money a, Money b) => a.Cents > b.Cents;

                public static bool operator ==(Money? a, Money? b) => a?.Cents == b?.Cents;

                public static bool operator !=(Money? a, Money? b) => !(a == b);

                public static bool operator true(Money a) => a.Cents != 0;

                public static bool operator false(Money a) => a.Cents == 0;

                public override bool Equals(object? obj) => obj is Money other && other.Cents == Cents;

                public override int GetHashCode() => Cents;
            }

            public static class Use
            {
                public static bool Same<T>(T a, T b) where T : class, IOrdered<T> => a == b;
            }
            """)));

        var result = await PullAsync(ws, "Money.cs", "Money", ["<", ">", "operator ==", "true"], targetType: "IOrdered");

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(Lf("""
            public interface IOrdered<TSelf> where TSelf : IOrdered<TSelf>
            {
                static abstract bool operator <(TSelf a, TSelf b);

                static abstract bool operator >(TSelf a, TSelf b);

                static abstract bool operator ==(TSelf? a, TSelf? b);

                static abstract bool operator !=(TSelf? a, TSelf? b);

                static abstract bool operator true(TSelf a);

                static abstract bool operator false(TSelf a);
            }
            """), ws.ReadFile("Money.cs"));
        Assert.Equal(
            [
                "'Money.operator !=(Money?, Money?)' is pulled up too: the compiler requires 'operator ==' and 'operator !=' to be declared together (CS0216).",
                "'Money.operator false(Money)' is pulled up too: the compiler requires 'operator true' and 'operator false' to be declared together (CS0216).",
                $"{ws.PathOf("Money.cs")}(39,74): 'a == b' now calls 'IOrdered<TSelf>.operator ==(TSelf?, TSelf?)' instead of the built-in operator (a reference comparison)."
            ],
            result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Conversions_AreDeclaredWithTheSelfTypeParameterAsTheirSourceOrTarget()
    {
        using var ws = TestWorkspace.Create(("Money.cs", Lf("""
            public interface IConvertible<TSelf> where TSelf : IConvertible<TSelf>
            {
            }

            public sealed class Money : IConvertible<Money>
            {
                public decimal Amount;

                public static explicit operator Money(int cents) => new() { Amount = cents / 100m };

                public static explicit operator Money(string text) => new() { Amount = decimal.Parse(text) };

                public static implicit operator decimal(Money money) => money.Amount;
            }

            public static class Use
            {
                public static T FromCents<T>(int cents) where T : IConvertible<T> => (T)cents;

                public static decimal ToDecimal<T>(T value) where T : IConvertible<T> => value;
            }
            """)));

        var notFound = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Money.cs", "Money", ["explicit operator long"], targetType: "IConvertible"));
        var result = await PullAsync(ws, "Money.cs", "Money", ["explicit operator Money", "implicit"], targetType: "IConvertible");

        Assert.Equal(ErrorCodes.MemberNotFound, notFound.ErrorCode);
        Assert.Contains("Its members: Amount, explicit operator Money, implicit operator decimal.", notFound.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(Lf("""
            public interface IConvertible<TSelf> where TSelf : IConvertible<TSelf>
            {
                static abstract explicit operator TSelf(int cents);

                static abstract explicit operator TSelf(string text);

                static abstract implicit operator decimal(TSelf money);
            }
            """), ws.ReadFile("Money.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task IntoANonGenericInterface_AnOperatorTakingTheInterfaceMoves_OneTakingOnlyTheTypeIsRefused()
    {
        var source = Lf("""
            public interface IShape
            {
            }

            public sealed class Square : IShape
            {
                public static IShape operator +(Square a, IShape b) => a;

                public static Square operator -(Square a) => a;
            }
            """);
        using var ws = TestWorkspace.Create(("Shapes.cs", source));

        var unary = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Shapes.cs", "Square", ["-"], targetType: "IShape"));
        Assert.Equal(source, ws.ReadFile("Shapes.cs"));
        var result = await PullAsync(ws, "Shapes.cs", "Square", ["+"], targetType: "IShape");

        Assert.Equal(ErrorCodes.MemberNotMoveable, unary.ErrorCode);
        Assert.Equal(
            "'Square.operator -(Square)' can't be declared in 'IShape': an operator of an interface must have the interface, or a type parameter " +
            "constrained to it, as an operand. 'IShape' has no type parameter 'Square' passes itself as, which the operator could take in its place " +
            "(interface IAddable<TSelf> where TSelf : IAddable<TSelf>, implemented as IAddable<Square>).",
            unary.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(Lf("""
            public interface IShape
            {
                static abstract IShape operator +(Square a, IShape b);
            }
            """), ws.ReadFile("Shapes.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ASelfTypeParameterWithoutTheConstraint_OrANullableStructOperand_IsRefused()
    {
        var source = Lf("""
            public interface IAddable<T>
            {
            }

            public interface ISummable<TSelf> where TSelf : ISummable<TSelf>
            {
            }

            public struct Money : IAddable<Money>, ISummable<Money>
            {
                public static Money operator +(Money a, Money b) => a;

                public static Money operator -(Money? a, Money b) => b;
            }
            """);
        using var ws = TestWorkspace.Create(("Money.cs", source));

        var unconstrained = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Money.cs", "Money", ["+"], targetType: "IAddable"));
        var nullable = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Money.cs", "Money", ["-"], targetType: "ISummable"));

        Assert.Equal(ErrorCodes.MemberNotMoveable, unconstrained.ErrorCode);
        Assert.EndsWith(
            "'Money' implements it as 'IAddable<Money>', but 'T' is not constrained to the interface (where T : IAddable<T>), so the operator can't take it in place of 'Money'.",
            unconstrained.Message);
        Assert.Equal(ErrorCodes.MemberNotMoveable, nullable.ErrorCode);
        Assert.Equal(
            "'Money.operator -(Money?, Money)' uses 'Money?', which 'ISummable<TSelf>' would have to write 'TSelf?'; for a type parameter that is not " +
            "Nullable<TSelf>, so 'Money' would not implement the member.",
            nullable.Message);
        Assert.Equal(source, ws.ReadFile("Money.cs"));
    }

    [Fact]
    public async Task IntoABaseClass_AStaticOperatorIsRefused_AnInstanceOperatorMovesLikeAMethod()
    {
        var source = Lf("""
            public class Base
            {
                public int Value;
            }

            public class Derived : Base
            {
                public static Derived operator +(Derived a, Derived b) => a;

                public static explicit operator Derived(int value) => new() { Value = value };

                public void operator +=(int amount) { Value += amount; }

                public void operator ++() { Value++; }
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var binary = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["+"], targetType: null));
        var conversion = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "C.cs", "Derived", ["explicit"], targetType: null));
        Assert.Equal(source, ws.ReadFile("C.cs"));
        var result = await PullAsync(ws, "C.cs", "Derived", ["+=", "++"], targetType: null, makeAbstract: true);

        Assert.Equal(ErrorCodes.MemberNotMoveable, binary.ErrorCode);
        Assert.Equal(
            "'Derived.operator +(Derived, Derived)' is an operator, which can't move into the class 'Base': an operator must take the class that " +
            "declares it as an operand (CS0563) and can't be abstract or virtual, so in 'Base' it would need another signature, which would change " +
            "its meaning. To share an operator, declare it in an interface with a self type parameter (interface IAddable<TSelf> where TSelf : " +
            "IAddable<TSelf>) that the types implement, and pull it up into that interface (targetType).",
            binary.Message);
        Assert.Contains("a conversion must convert from or to the class that declares it (CS0556)", conversion.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            public abstract class Base
            {
                public int Value;

                public abstract void operator +=(int amount);

                public abstract void operator ++();
            }

            public class Derived : Base
            {
                public static Derived operator +(Derived a, Derived b) => a;

                public static explicit operator Derived(int value) => new() { Value = value };

                public override void operator +=(int amount) { Value += amount; }

                public override void operator ++() { Value++; }
            }
            """), ws.ReadFile("C.cs"));
        Assert.Contains("'Base' was not abstract and is never instantiated; it is abstract now.", result.Warnings!);
        Assert.Empty(ws.GetDiskCompilationErrors());

        using var whole = TestWorkspace.Create(("C.cs", source.Replace("{ Value++; }", "{ Value++; }\n\n    public void operator checked ++() { checked { Value++; } }")));
        var moved = await PullAsync(whole, "C.cs", "Derived", ["operator checked ++"], targetType: null);

        Assert.True(moved.Success, moved.Error?.Message);
        Assert.StartsWith(Lf("""
            public class Base
            {
                public int Value;

                public void operator ++() { Value++; }

                public void operator checked ++() { checked { Value++; } }
            }
            """), whole.ReadFile("C.cs"));
        Assert.Equal(
            ["'Derived.operator ++()' is pulled up too: 'operator checked ++' needs its unchecked version declared with it (CS9025)."],
            moved.Warnings);
        Assert.Empty(whole.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task InstanceOperators_IntoASelfInterface_AreDeclaredAbstract()
    {
        using var ws = TestWorkspace.Create(("IAddable.cs", Interface), ("Money.cs", Lf("""
            using Core;

            namespace App;

            public sealed class Money : IAddable<Money>
            {
                public int Cents;

                public void operator +=(Money other) => Cents += other.Cents;

                public void operator checked +=(Money other) => Cents = checked(Cents + other.Cents);
            }
            """)));

        var result = await PullAsync(ws, "Money.cs", "Money", ["operator +="]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace Core;

            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
                void operator +=(TSelf other);

                void operator checked +=(TSelf other);
            }
            """), ws.ReadFile("IAddable.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Operators_BeforeCSharp11_AreRefused()
    {
        var source = Lf("""
            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
            }

            public sealed class Money : IAddable<Money>
            {
                public static Money operator +(Money a, Money b) => a;
            }
            """);
        using var ws = TestWorkspace.Create([("Money.cs", source)], [], languageVersion: LanguageVersion.CSharp10);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Money.cs", "Money", ["+"], targetType: "IAddable"));

        Assert.Equal(ErrorCodes.MemberNotMoveable, ex.ErrorCode);
        Assert.Contains("'Money.operator +(Money, Money)' is static; it would be declared static abstract in 'IAddable<TSelf>', which needs C# 11", ex.Message);
        Assert.Equal(source, ws.ReadFile("Money.cs"));
    }

    [Fact]
    public async Task OtherImplementersLackingTheOperator_AndTheInterfaceAsATypeArgument_AreReported()
    {
        using var ws = TestWorkspace.Create(
            ("IAddable.cs", Interface),
            ("Money.cs", Lf("""
                using System.Collections.Generic;
                using Core;

                namespace App;

                public sealed class Money : IAddable<Money>
                {
                    public static Money operator +(Money a, Money b) => a;
                }

                public sealed class Points : IAddable<Points>
                {
                }

                public static class Use
                {
                    public static List<IAddable<Money>> All() => [];
                }
                """)));

        var result = await PullAsync(ws, "Money.cs", "Money", ["+"]);

        Assert.True(result.Success, result.Error?.Message);
        var warnings = string.Join("\n", result.Warnings!);
        Assert.Contains("error CS0535: 'Points' does not implement interface member 'IAddable<Points>.operator +(Points, Points)'", warnings);
        Assert.Contains("error CS8920", warnings);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task AnOperatorTheInterfaceDeclares_IsAConflict_AGenericTypePassingItselfIsTranslated()
    {
        using var ws = TestWorkspace.Create(("Vector.cs", Lf("""
            using System.Collections.Generic;

            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
                static abstract TSelf operator -(TSelf a, TSelf b);
            }

            public sealed class Vector<T> : IAddable<Vector<T>>
            {
                public T[] Items = [];

                public static Vector<T> operator +(Vector<T> a, Vector<T> b) => a;

                public static Vector<T> operator -(Vector<T> a, Vector<T> b) => a;

                public static List<Vector<T>> operator *(Vector<T> a, int count) => [a];
            }
            """)));

        var conflict = await Assert.ThrowsAsync<RefactoringException>(() => PullAsync(ws, "Vector.cs", "Vector", ["-"], targetType: "IAddable"));
        var result = await PullAsync(ws, "Vector.cs", "Vector", ["+", "*"], targetType: "IAddable");

        Assert.Equal(ErrorCodes.MemberConflict, conflict.ErrorCode);
        Assert.Equal("'IAddable<TSelf>' already declares 'IAddable<TSelf>.operator -(TSelf, TSelf)'.", conflict.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith(Lf("""
            using System.Collections.Generic;

            public interface IAddable<TSelf> where TSelf : IAddable<TSelf>
            {
                static abstract TSelf operator -(TSelf a, TSelf b);

                static abstract TSelf operator +(TSelf a, TSelf b);

                static abstract List<TSelf> operator *(TSelf a, int count);
            }
            """), ws.ReadFile("Vector.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Preview_ShowsWhatTheCommitWrites()
    {
        var money = Lf("""
            using Core;

            namespace App;

            public sealed class Money : IAddable<Money>
            {
                public static bool operator ==(Money a, Money b) => true;

                public static bool operator !=(Money a, Money b) => false;

                public override bool Equals(object? obj) => true;

                public override int GetHashCode() => 0;
            }
            """);
        using var ws = TestWorkspace.Create(("IAddable.cs", Interface), ("Money.cs", money));

        var preview = await PullAsync(ws, "Money.cs", "Money", ["=="], preview: true);
        Assert.Equal(Interface, ws.ReadFile("IAddable.cs"));
        var result = await PullAsync(ws, "Money.cs", "Money", ["=="]);

        Assert.Equal(["Add 'operator ==' to 'IAddable'", "Add 'operator !=' to 'IAddable'"], preview.PendingChanges!.Select(c => c.Description));
        Assert.Equal(
            ["static abstract bool operator ==(TSelf a, TSelf b);", "static abstract bool operator !=(TSelf a, TSelf b);"],
            preview.PendingChanges!.Select(c => c.AfterSnippet));
        Assert.Equal(preview.Warnings, result.Warnings);
        var text = ws.ReadFile("IAddable.cs");
        foreach (var change in preview.PendingChanges!)
            Assert.Equal(change.AfterSnippet, text.Split('\n')[change.StartLine!.Value - 1].Trim());
        Assert.Equal(money, ws.ReadFile("Money.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> PullAsync(
        TestWorkspace ws,
        string file,
        string type,
        string[] members,
        string? targetType = "IAddable",
        bool makeAbstract = false,
        bool preview = false) =>
        new PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams
            {
                SourceFile = ws.PathOf(file),
                TypeName = type,
                Members = members,
                TargetType = targetType,
                MakeAbstract = makeAbstract,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
