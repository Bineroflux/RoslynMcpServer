using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Type parameter constraints of generated generic methods. Stubs used to drop them, so an
/// implicit interface implementation of <c>T Clamp&lt;T&gt;(T) where T : struct, ...</c>
/// did not implement the interface method (CS0425), and an interface extracted from a
/// constrained method no longer matched the class (CS0425). Overrides and explicit
/// implementations inherit constraints and may only restate <c>class</c>/<c>struct</c>
/// (and <c>default</c> for <c>T?</c>).
/// </summary>
public sealed class GenericConstraintTests
{
    private const string Interface = """
        using System;

        namespace App;

        public interface IRange
        {
            T Clamp<T>(T value) where T : struct, IComparable<T>;
            U Make<U>() where U : class, IDisposable, new();
            void Use<V>(V value) where V : notnull;
            T? Find<T>(string key);
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementInterface_ConstrainedMethods_Compiles(bool explicitImplementation)
    {
        using var ws = TestWorkspace.Create(("IRange.cs", Interface), ("Range.cs", """
            using System;

            namespace App;

            public class Range : IRange
            {
            }
            """));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Range.cs"),
                TypeName = "Range",
                InterfaceName = "IRange",
                ExplicitImplementation = explicitImplementation
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Range.cs");
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
        if (explicitImplementation)
        {
            Assert.Contains("T App.IRange.Clamp<T>(T value)\n        where T : struct", text.ReplaceLineEndings("\n"));
            Assert.Contains("where U : class", text);
            Assert.Contains("where T : default", text);
            Assert.DoesNotContain("IComparable", text);
        }
        else
        {
            Assert.Contains("where T : struct, System.IComparable<T>", text);
            Assert.Contains("where U : class, System.IDisposable, new()", text);
            Assert.Contains("where V : notnull", text);
        }
    }

    [Fact]
    public async Task GenerateOverrides_ConstrainedMethods_Compiles()
    {
        using var ws = TestWorkspace.Create(("Repository.cs", """
            using System;

            namespace App;

            public class Repository
            {
                public virtual T Pick<T>(T first) where T : class, IComparable<T> => first;
                public virtual T? Find<T>(string key) => default;
                public virtual T Box<T>(T value) where T : struct => value;
            }

            public class Cache : Repository
            {
            }
            """));

        var result = await new GenerateOverridesOperation(ws.Context).ExecuteAsync(
            new GenerateOverridesParams
            {
                SourceFile = ws.PathOf("Repository.cs"),
                TypeName = "Cache",
                Members = ["Pick", "Find", "Box"]
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Repository.cs");
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
        var cache = text[text.IndexOf("class Cache", StringComparison.Ordinal)..];
        Assert.Contains("where T : class", cache);
        Assert.Contains("where T : default", cache);
        Assert.Contains("where T : struct", cache);
        Assert.DoesNotContain("IComparable", cache);
    }

    [Fact]
    public async Task ExtractInterface_ConstrainedMethod_CopiesConstraints()
    {
        using var ws = TestWorkspace.Create(("Range.cs", """
            using System;

            namespace App;

            public class Range
            {
                public T Clamp<T>(T value) where T : struct, IComparable<T> => value;
            }
            """));

        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams { SourceFile = ws.PathOf("Range.cs"), TypeName = "Range", InterfaceName = "IRange" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Range.cs");
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
        Assert.Contains("T Clamp<T>(T value)\n        where T : struct, System.IComparable<T>;", text.ReplaceLineEndings("\n"));
    }
}
