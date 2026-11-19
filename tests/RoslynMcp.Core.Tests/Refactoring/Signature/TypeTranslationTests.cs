using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// A type the request writes in the starting member's terms is translated into each family
/// member's own: <c>TSelf*</c> in <c>IParse&lt;TSelf&gt;</c> is <c>Num*</c> in
/// <c>Num : IParse&lt;Num&gt;</c>. A type parameter inside a pointer, a function pointer or a
/// generic containing type used to be refused (TypeNotExpressible).
/// </summary>
public sealed class TypeTranslationTests
{
    private const string Parsers = """
        namespace App;

        public unsafe interface IParse<TSelf> where TSelf : unmanaged, IParse<TSelf>
        {
            static abstract TSelf Parse(string text);
        }

        public unsafe struct Num : IParse<Num>
        {
            public static Num Parse(string text) => default;
        }

        public class Outer<T>
        {
            public class Inner { }

            public class Pair<U> { }
        }
        """;

    private const string Stores = """
        namespace App;

        public unsafe interface IStore<TItem> where TItem : unmanaged
        {
            void Visit(int index);
        }

        public unsafe class IntStore : IStore<int>
        {
            public void Visit(int index) { }
        }

        public unsafe class Store<TValue> : IStore<TValue> where TValue : unmanaged
        {
            public void Visit(int index) { }
        }

        public class Outer<T>
        {
            public class Inner { }
        }

        public static unsafe class Use
        {
            public static void Run(IntStore store) => store.Visit(1);
        }
        """;

    [Theory]
    [InlineData("TSelf*", "Num*")]
    [InlineData("TSelf*[]", "Num*[]")]
    [InlineData("delegate*<TSelf, TSelf*>", "delegate*<Num, Num*>")]
    [InlineData("delegate*<ref TSelf, in TSelf, out TSelf, ref readonly TSelf>", "delegate*<ref Num, in Num, out Num, ref readonly Num>")]
    [InlineData("delegate* unmanaged[Cdecl]<TSelf, void>", "delegate* unmanaged[Cdecl]<Num, void>")]
    [InlineData("Outer<TSelf>.Inner", "Outer<Num>.Inner")]
    [InlineData("Outer<TSelf>.Pair<TSelf[]>?", "Outer<Num>.Pair<Num[]>?")]
    [InlineData("(TSelf First, TSelf[] Others)?", "(Num First, Num[] Others)?")]
    [InlineData("TSelf?", "Num?")]
    public async Task ChangeReturnType_TranslatesTheTypeParameterWhereverItOccurs(string newType, string translated)
    {
        using var ws = TestWorkspace.Create([("Parsers.cs", Parsers)], [], allowUnsafe: true);

        var result = await new ChangeReturnTypeOperation(ws.Context).ExecuteAsync(
            new ChangeReturnTypeParams { SourceFile = ws.PathOf("Parsers.cs"), MemberName = "Parse", Line = 5, NewType = newType },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Parsers.cs");
        Assert.Contains($"    static abstract {newType} Parse(string text);", text);
        Assert.Contains($"    public static {translated} Parse(string text) => default;", text);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ChangeSignature_WritesNewParameterTypesInEachMembersTerms()
    {
        using var ws = TestWorkspace.Create([("Stores.cs", Stores)], [], allowUnsafe: true);

        var result = await new ChangeSignatureOperation(ws.Context).ExecuteAsync(
            new ChangeSignatureParams
            {
                SourceFile = ws.PathOf("Stores.cs"),
                MethodName = "Visit",
                Line = 5,
                Parameters =
                [
                    new ParameterChange { Name = "callback", Type = "delegate*<TItem*, void>", DefaultValue = "null" },
                    new ParameterChange { Name = "slot", Type = "Outer<TItem[]>.Inner?", DefaultValue = "null" }
                ]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Stores.cs");
        Assert.Contains("    void Visit(int index, delegate*<TItem*, void> callback = null, Outer<TItem[]>.Inner? slot = null);", text);
        Assert.Contains("    public void Visit(int index, delegate*<int*, void> callback = null, Outer<int[]>.Inner? slot = null) { }", text);
        Assert.Contains("    public void Visit(int index, delegate*<TValue*, void> callback = null, Outer<TValue[]>.Inner? slot = null) { }", text);
        Assert.Contains("store.Visit(1);", text);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
