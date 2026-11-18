using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// End-to-end coverage of the member stubs implement_interface, generate_overrides and
/// extract_interface emit, asserting that what lands on disk compiles. Previously:
/// implement_interface marked every member <c>override</c> (CS0115), emitted the
/// property/indexer accessors as extra <c>get_Item</c>-style methods, turned indexers
/// into a property named <c>this[]</c> and skipped members of base interfaces; generate_overrides always emitted <c>public</c>
/// (CS0507), dropped <c>ref</c>/<c>out</c>/<c>in</c>/<c>scoped</c>, ignored
/// <c>callBase</c> for properties, emitted members twice when an intermediate base
/// already overrode them, and offered inaccessible internal members of other
/// assemblies; extract_interface produced a property named <c>this[]</c>, dropped
/// parameter modifiers, and exposed non-public setters.
/// </summary>
public sealed class MemberGenerationTests
{
    #region implement_interface

    private const string RepositoryInterface = """
        using System;

        namespace App;

        public interface IRepository : IDisposable
        {
            string Name { get; set; }
            int this[int index] { get; set; }
            event EventHandler Changed;
            bool TryFind(int id, out string name);
            void Touch(ref int counter, in long stamp);
            void Fill(scoped Span<int> buffer);
            T Echo<T>(T value);
        }
        """;

    private const string EmptyRepository = """
        using System;

        namespace App;

        public class Repository : IRepository
        {
        }
        """;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ImplementInterface_AllMemberKinds_Compiles(bool explicitImplementation, bool throwNotImplemented)
    {
        using var ws = TestWorkspace.Create(("IRepository.cs", RepositoryInterface), ("Repository.cs", EmptyRepository));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Repository.cs"),
                TypeName = "Repository",
                InterfaceName = "IRepository",
                ExplicitImplementation = explicitImplementation,
                ThrowNotImplemented = throwNotImplemented
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Repository.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());
        Assert.DoesNotContain("override", text);
        Assert.DoesNotContain("get_Item", text);
        Assert.DoesNotContain("get_Name", text);
        Assert.DoesNotContain("add_Changed", text);
        Assert.Contains(explicitImplementation ? "int App.IRepository.this[int index]" : "public int this[int index]", text);
        Assert.Contains("out string name", text);
        Assert.Contains("ref int counter, in long stamp", text);
        Assert.Contains("scoped System.Span<int> buffer", text);

        // Members inherited from base interfaces need implementing too.
        Assert.Contains(explicitImplementation ? "void System.IDisposable.Dispose()" : "public void Dispose()", text);
    }

    #endregion

    #region generate_overrides

    private const string BaseClass = """
        using System;

        namespace App;

        public abstract class Widget
        {
            private string _title = "";

            protected virtual void OnChanged(string name) { }
            public virtual bool TryGet(int key, out string value) { value = ""; return false; }
            protected internal virtual void Touch(ref int counter, in long stamp) { }
            public virtual string Title { get => _title; set => _title = value; }
            public virtual int Count { get; protected set; }
            protected virtual int this[int index] { get => index; set { } }
            public abstract void Fill(scoped Span<int> buffer);
            public abstract int Size { get; }
        }
        """;

    private const string EmptyDerived = """
        using System;

        namespace App;

        public class Button : Widget
        {
        }
        """;

    [Fact]
    public async Task GenerateOverrides_KeepsAccessibilityModifiersAndBaseCalls()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", BaseClass), ("Button.cs", EmptyDerived));

        var result = await GenerateOverridesAsync(ws, "Button.cs", "Button");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Button.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());

        Assert.Contains("protected override void OnChanged(string name)", text);
        Assert.Contains("protected internal override void Touch(ref int counter, in long stamp)", text);
        Assert.Contains("base.Touch(ref counter, in stamp);", text);
        Assert.Contains("return base.TryGet(key, out value);", text);
        Assert.Contains("public override void Fill(scoped System.Span<int> buffer)", text);

        // callBase applies to properties and indexers too, instead of `return default;`.
        Assert.Contains("return base.Title;", text);
        Assert.Contains("base.Title = value;", text);
        Assert.Contains("protected set", text);
        Assert.Contains("protected override int this[int index]", text);
        Assert.Contains("return base[index];", text);
        Assert.Contains("base[index] = value;", text);
    }

    [Fact]
    public async Task GenerateOverrides_CrossAssemblyProtectedInternal_EmitsProtected()
    {
        var library = TestWorkspace.CompileLibrary("Lib", """
            namespace Lib;

            public abstract class LibraryBase
            {
                protected internal virtual void Touch() { }
                protected internal virtual int Level { get; set; }
                internal virtual void Hidden() { }
            }
            """);
        using var ws = TestWorkspace.Create(
            [("Derived.cs", """
                namespace App;

                public class Derived : Lib.LibraryBase
                {
                }
                """)],
            [library]);

        var result = await GenerateOverridesAsync(ws, "Derived.cs", "Derived");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Derived.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());
        Assert.Contains("protected override void Touch()", text);
        Assert.Contains("protected override int Level", text);
        Assert.DoesNotContain("Hidden", text);
    }

    [Fact]
    public async Task GenerateOverrides_MemberOverriddenByIntermediateBase_EmittedOnce()
    {
        using var ws = TestWorkspace.Create(("Hierarchy.cs", """
            namespace App;

            public class First
            {
                public virtual void Run() { }
                public override string ToString() => "First";
            }

            public class Second : First
            {
                public override void Run() { }
            }

            public class Third : Second
            {
            }
            """));

        var result = await GenerateOverridesAsync(ws, "Hierarchy.cs", "Third");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Hierarchy.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());
        var third = text[text.IndexOf("class Third", StringComparison.Ordinal)..];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(third, @"override void Run\("));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(third, @"override string\?? ToString\("));
    }

    #endregion

    #region extract_interface

    [Fact]
    public async Task ExtractInterface_IndexerAndParameterModifiers_Compiles()
    {
        using var ws = TestWorkspace.Create(("Store.cs", """
            namespace App;

            public class Store
            {
                public int this[int index] { get => index; set { } }
                public string Name { get; private set; } = "";
                public int Id { get; init; }
                public bool TryParse(string text, out int value) { value = 0; return false; }
                public void Bump(ref int counter, in long stamp) { }
            }
            """));

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = ws.PathOf("Store.cs"),
                TypeName = "Store",
                InterfaceName = "IStore"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Store.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());
        Assert.Contains("int this[int index] { get; set; }", text);
        Assert.Contains("string Name { get; }", text);
        Assert.Contains("int Id { get; init; }", text);
        Assert.Contains("bool TryParse(string text, out int value);", text);
        Assert.Contains("void Bump(ref int counter, in long stamp);", text);
    }

    #endregion

    private static Task<RefactoringResult> GenerateOverridesAsync(TestWorkspace ws, string file, string typeName) =>
        new GenerateOverridesOperation(ws.Context).ExecuteAsync(
            new GenerateOverridesParams { SourceFile = ws.PathOf(file), TypeName = typeName },
            TestContext.Current.CancellationToken);
}
