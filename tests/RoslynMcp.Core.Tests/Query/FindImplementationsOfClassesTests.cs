using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// find_implementations on a class and on a virtual, abstract or override member. Roslyn's
/// FindImplementationsAsync only knows interfaces and their members and returned nothing
/// here, although the tool promised derived classes and overrides.
/// </summary>
public sealed class FindImplementationsOfClassesTests
{
    private const string Animals = """
        namespace App;

        public abstract class Animal
        {
            public abstract void Speak();
            public virtual string Name => "";
            public virtual event System.EventHandler? Moved;
        }

        public class Dog : Animal
        {
            public override void Speak() { }
            public override string Name => "Dog";
        }

        public class Puppy : Dog
        {
            public override void Speak() { }
            public override string Name => "Puppy";
            public override event System.EventHandler? Moved;
        }

        public abstract class Repo<T> { public abstract T Get(); }
        public class IntRepo : Repo<int> { public override int Get() => 0; }
        public class CachedRepo : IntRepo { public override int Get() => 1; }

        public struct Point { }
        """;

    // A second project, to cross compilations.
    private const string Plugin = """
        using App;

        namespace Plugins;

        public class PluginDog : Dog { public override void Speak() { } }
        public class PluginRepo : Repo<string> { public override string Get() => ""; }
        """;

    [Theory]
    [InlineData(null, new[] { "App.Dog", "App.Puppy", "Plugins.PluginDog" })]
    [InlineData(true, new[] { "App.Dog", "App.Puppy", "Plugins.PluginDog" })]
    [InlineData(false, new[] { "App.Dog" })]
    public async Task OfAnAbstractClass_AreItsDerivedClasses(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Animal", line: null, transitive));
    }

    [Theory]
    [InlineData(null, new[] { "App.Puppy", "Plugins.PluginDog" })]
    [InlineData(false, new[] { "App.Puppy", "Plugins.PluginDog" })]
    public async Task OfAConcreteClass_AreItsDerivedClasses(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Dog", line: 10, transitive));
    }

    [Theory]
    [InlineData(null, new[] { "App.Dog.Speak()", "App.Puppy.Speak()", "Plugins.PluginDog.Speak()" })]
    [InlineData(false, new[] { "App.Dog.Speak()" })]
    public async Task OfAnAbstractMethod_AreItsOverrides(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Speak", line: 5, transitive));
    }

    [Theory]
    [InlineData(null, new[] { "App.Puppy.Speak()", "Plugins.PluginDog.Speak()" })]
    [InlineData(false, new[] { "App.Puppy.Speak()", "Plugins.PluginDog.Speak()" })]
    public async Task OfAnOverride_AreTheOverridesOfIt(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Speak", line: 12, transitive));
    }

    [Theory]
    [InlineData(null, new[] { "App.Dog.Name", "App.Puppy.Name" })]
    [InlineData(false, new[] { "App.Dog.Name" })]
    public async Task OfAVirtualProperty_AreItsOverrides(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Name", line: 6, transitive));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task OfAVirtualEvent_AreItsOverrides(bool? transitive)
    {
        // Dog does not override Moved, so Puppy's override is a direct one.
        Assert.Equal(["App.Puppy.Moved"], await FindAsync("Moved", line: 7, transitive));
    }

    [Theory]
    [InlineData(null, new[] { "App.IntRepo.Get()", "App.CachedRepo.Get()", "Plugins.PluginRepo.Get()" })]
    [InlineData(false, new[] { "App.IntRepo.Get()", "Plugins.PluginRepo.Get()" })]
    public async Task OfAGenericClassMember_FollowTheConstruction(bool? transitive, string[] expected)
    {
        Assert.Equal(expected, await FindAsync("Get", line: 23, transitive));
    }

    [Fact]
    public async Task OfAStruct_IsEmpty()
    {
        Assert.Empty(await FindAsync("Point", line: null, transitive: null));
    }

    [Fact]
    public async Task MaxResults_CountsAllDerivedClasses()
    {
        using var ws = CreateWorkspace();

        var result = await new FindImplementationsOperation(ws.Context).ExecuteAsync(
            new FindImplementationsParams { SourceFile = ws.PathOf("App", "Animals.cs"), SymbolName = "Animal", MaxResults = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(["App.Dog"], result.Data!.Implementations.Select(i => i.FullyQualifiedName));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("App", [("Animals.cs", Animals)]),
            new TestWorkspace.ProjectSpec("Plugins", [("Plugin.cs", Plugin)], References: ["App"]));

    private static async Task<List<string>> FindAsync(string symbolName, int? line, bool? transitive)
    {
        using var ws = CreateWorkspace();

        var result = await new FindImplementationsOperation(ws.Context).ExecuteAsync(
            new FindImplementationsParams
            {
                SourceFile = ws.PathOf("App", "Animals.cs"),
                SymbolName = symbolName,
                Line = line,
                Transitive = transitive
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(result.Data!.TotalCount, result.Data.Implementations.Count);
        return result.Data.Implementations.Select(i => i.FullyQualifiedName).ToList();
    }
}
