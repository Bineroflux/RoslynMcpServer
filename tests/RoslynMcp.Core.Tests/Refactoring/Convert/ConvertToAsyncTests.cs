using System.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Convert;

/// <summary>
/// convert_to_async renames the method with every reference, makes it async around what its body
/// waits for, awaits it in async callers and reports the callers it cannot await.
/// </summary>
public sealed class ConvertToAsyncTests
{
    private const string Store = """
        using System.Threading.Tasks;

        namespace Lib;

        public class Store
        {
            /// <summary>Loads; see <see cref="Load(int)"/>.</summary>
            public int Load(int id)
            {
                return FetchAsync(id).Result;
            }

            public string Name => nameof(Load);

            private static Task<int> FetchAsync(int id) => Task.FromResult(id * 2);
        }
        """;

    private const string Callers = """
        using System;
        using System.Threading.Tasks;
        using Lib;

        namespace App;

        public static class Use
        {
            public static async Task<int> TwiceAsync(Store store)
            {
                var first = store.Load(1);
                return first + store.Load(2).CompareTo(0);
            }

            public static int Sync(Store store)
            {
                return store.Load(3);
            }

            public static Func<int, int> Group(Store store) => store.Load;

            public static async Task LambdaAsync(Store store)
            {
                Func<int> later = () => store.Load(4);
                Func<Task<int>> deferred = async () => store.Load(5) + 1;
                await Task.Yield();
            }

            public static async Task<int?> MaybeAsync(Store? store)
            {
                await Task.Yield();
                return store?.Load(6);
            }
        }
        """;

    [Fact]
    public async Task RenamesEveryReference_AwaitsAsyncCallers_AndReportsTheOthers()
    {
        using var ws = CreateStoreSolution();

        var result = await ConvertAsync(ws, ws.PathOf("Lib", "Store.cs"), "Load");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("LoadAsync", result.Symbol!.Name);
        Assert.Equal(Lf("""
            using System.Threading.Tasks;

            namespace Lib;

            public class Store
            {
                /// <summary>Loads; see <see cref="LoadAsync(int)"/>.</summary>
                public async Task<int> LoadAsync(int id)
                {
                    return await FetchAsync(id);
                }

                public string Name => nameof(LoadAsync);

                private static Task<int> FetchAsync(int id) => Task.FromResult(id * 2);
            }
            """), File.ReadAllText(ws.PathOf("Lib", "Store.cs")));

        var use = File.ReadAllText(ws.PathOf("App", "Use.cs"));
        Assert.Contains("var first = await store.LoadAsync(1);", use);
        Assert.Contains("return first + (await store.LoadAsync(2)).CompareTo(0);", use);
        Assert.Contains("return store.LoadAsync(3);", use);
        Assert.Contains("public static Func<int, int> Group(Store store) => store.LoadAsync;", use);
        Assert.Contains("Func<int> later = () => store.LoadAsync(4);", use);
        Assert.Contains("Func<Task<int>> deferred = async () => await store.LoadAsync(5) + 1;", use);
        Assert.Contains("return store?.LoadAsync(6);", use);

        var warnings = result.Warnings!;
        var path = ws.PathOf("App", "Use.cs");
        Assert.Contains($"{path}(17,22): not awaited: 'Use.Sync' is not async, so the call returns 'Task<int>' without waiting for it.", warnings);
        Assert.Contains($"{path}(24,39): not awaited: the lambda around it is not async, so the call returns 'Task<int>' without waiting for it.", warnings);
        Assert.Contains(warnings, w => w.StartsWith($"{path}(32,23): not awaited: it is called with '?.'", StringComparison.Ordinal));
        Assert.Equal(3, warnings.Count(w => w.Contains(": not awaited: ", StringComparison.Ordinal)));
        Assert.Contains(warnings, w => w.StartsWith($"{path}(17,16): error CS0029", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith($"{path}(20,", StringComparison.Ordinal) && w.Contains("error CS0407", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, warnings);
    }

    [Fact]
    public async Task AwaitsWaitsAndDiscardedTasks_OfTheBodyOnly_ParenthesizedWhereNeeded()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace App;

            public class Worker
            {
                public int Run(Task<string> pending)
                {
                    SendAsync();
                    _ = SendAsync();
                    SendAsync().Wait();
                    var text = FetchAsync().GetAwaiter().GetResult();
                    var configured = FetchAsync()
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                    var length = FetchAsync().Result.Length;
                    var task = FetchAsync();
                    Func<int> later = () => FetchAsync().Result.Length;
                    int Local() => FetchAsync().Result.Length;
                    return length + text.Length + configured.Length + task.Result.Length + pending.Result!.Length + later() + Local();
                }

                private static Task SendAsync() => Task.CompletedTask;

                private static Task<string> FetchAsync() => Task.FromResult("x");
            }
            """;
        using var ws = TestWorkspace.Create(("Worker.cs", Lf(source)));

        var result = await ConvertAsync(ws, ws.PathOf("Worker.cs"), "Run");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System;
            using System.Threading.Tasks;

            namespace App;

            public class Worker
            {
                public async Task<int> RunAsync(Task<string> pending)
                {
                    await SendAsync();
                    _ = SendAsync();
                    await SendAsync();
                    var text = await FetchAsync();
                    var configured = await FetchAsync()
                        .ConfigureAwait(false);
                    var length = (await FetchAsync()).Length;
                    var task = FetchAsync();
                    Func<int> later = () => FetchAsync().Result.Length;
                    int Local() => FetchAsync().Result.Length;
                    return length + text.Length + configured.Length + (await task).Length + (await pending)!.Length + later() + Local();
                }

                private static Task SendAsync() => Task.CompletedTask;

                private static Task<string> FetchAsync() => Task.FromResult("x");
            }
            """), ws.ReadFile("Worker.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task CallersThatCannotAwait_AreNamedByKind()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace App;

            public class Meter
            {
                private readonly int _start = Read();

                public Meter() => _ = Read();

                public int Current => Read();

                public static int Read() => Task.FromResult(1).Result;

                public static async Task<int> TotalAsync()
                {
                    async Task<int> Twice() => Read() * 2;
                    int Once() => Read();
                    await Task.Yield();
                    return await Twice() + Once();
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Meter.cs", Lf(source)));

        var result = await ConvertAsync(ws, ws.PathOf("Meter.cs"), "Read");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Meter.cs");
        Assert.Contains("async Task<int> Twice() => await ReadAsync() * 2;", text);
        Assert.Contains("int Once() => ReadAsync();", text);
        var path = ws.PathOf("Meter.cs");
        Assert.Equal(
            [
                $"{path}(7,35): not awaited: it is in a field initializer, which cannot be async, so the call returns 'Task<int>' without waiting for it.",
                $"{path}(9,27): not awaited: it is in a constructor, which cannot be async, so the call returns 'Task<int>' without waiting for it.",
                $"{path}(11,27): not awaited: it is in a property, indexer or event accessor, which cannot be async, so the call returns 'Task<int>' without waiting for it.",
                $"{path}(18,23): not awaited: local function 'Once' is not async, so the call returns 'Task<int>' without waiting for it."
            ],
            result.Warnings!.Where(w => w.Contains("not awaited", StringComparison.Ordinal)));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task WaitWhereAwaitIsNotAllowed_IsLeftAndReported()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace App;

            public class Gate
            {
                private readonly object _sync = new();

                public int Read()
                {
                    var first = FetchAsync().Result;
                    lock (_sync)
                    {
                        return first + FetchAsync().Result;
                    }
                }

                public int Locked()
                {
                    lock (_sync)
                    {
                        return FetchAsync().Result;
                    }
                }

                private static Task<int> FetchAsync() => Task.FromResult(1);
            }
            """;
        using var ws = TestWorkspace.Create(("Gate.cs", Lf(source)));

        var result = await ConvertAsync(ws, ws.PathOf("Gate.cs"), "Read");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Gate.cs");
        Assert.Contains("var first = await FetchAsync();", text);
        Assert.Contains("return first + FetchAsync().Result;", text);
        Assert.Equal(
            [$"{ws.PathOf("Gate.cs")}(14,41): not awaited: await is not allowed in the body of a lock statement."],
            result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());

        var locked = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, ws.PathOf("Gate.cs"), "Locked"));
        Assert.Equal(ErrorCodes.NoAsyncCalls, locked.ErrorCode);
        Assert.Contains("await is not allowed in the body of a lock statement", locked.Message);
    }

    [Fact]
    public async Task VoidBecomesTask_UsingAddedOnlyWhereTaskIsNotInScope_RecursionAndSyncCallersHandled()
    {
        const string saver = """
            namespace App;

            public class Saver
            {
                public void Save(int depth)
                {
                    System.Threading.Tasks.Task.Delay(1).Wait();
                    if (depth > 0)
                        Save(depth - 1);
                }
            }
            """;
        const string closer = """
            namespace App;

            public class Closer
            {
                public void Close(Saver saver)
                {
                    saver.Save(1);
                }

                public Task CloseAsync(Saver saver)
                {
                    saver.Save(2);
                    return Task.CompletedTask;
                }
            }
            """;
        using var ws = TestWorkspace.Create(
            ("Saver.cs", Lf(saver)),
            ("Closer.cs", Lf(closer)),
            ("Usings.cs", Lf("global using System.Threading.Tasks;")));

        var result = await ConvertAsync(ws, ws.PathOf("Saver.cs"), "Save");

        Assert.True(result.Success, result.Error?.Message);

        // The global using makes Task visible, so no using is added.
        Assert.Equal(Lf("""
            namespace App;

            public class Saver
            {
                public async Task SaveAsync(int depth)
                {
                    await System.Threading.Tasks.Task.Delay(1);
                    if (depth > 0)
                        await SaveAsync(depth - 1);
                }
            }
            """), ws.ReadFile("Saver.cs"));
        Assert.Equal(0, result.UsingDirectivesAdded);
        Assert.Contains("        saver.SaveAsync(1);", ws.ReadFile("Closer.cs"));
        Assert.Contains("        saver.SaveAsync(2);", ws.ReadFile("Closer.cs"));

        // Still compiles: the callers run the task without waiting for it, which only the warnings say.
        Assert.Equal(
            [
                $"{ws.PathOf("Closer.cs")}(7,15): not awaited: 'Closer.Close' is not async, so the call returns 'Task' without waiting for it.",
                $"{ws.PathOf("Closer.cs")}(12,15): not awaited: 'Closer.CloseAsync' is not async, so the call returns 'Task' without waiting for it."
            ],
            result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task UsingIsAdded_AndAFileKeepsItsLineEndingsAndByteOrderMark()
    {
        var crlf = """
            // Header.
            namespace App;

            public class Clock
            {
                public long Now() => System.Threading.Tasks.Task.FromResult(1L).Result;
            }
            """.ReplaceLineEndings("\r\n") + "\r\n";
        var lf = """
            namespace App;

            public class Watch
            {
                public long Read(Clock clock) => clock.Now();
            }
            """.ReplaceLineEndings("\n") + "\n";
        using var ws = TestWorkspace.Create(("Clock.cs", crlf), ("Watch.cs", lf));
        File.WriteAllText(ws.PathOf("Clock.cs"), crlf, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await ConvertAsync(ws, ws.PathOf("Clock.cs"), "Now");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Equal("""
            // Header.
            using System.Threading.Tasks;

            namespace App;

            public class Clock
            {
                public async Task<long> NowAsync() => await System.Threading.Tasks.Task.FromResult(1L);
            }
            """.ReplaceLineEndings("\r\n") + "\r\n", ws.ReadFile("Clock.cs"));
        Assert.Equal(lf.Replace("clock.Now()", "clock.NowAsync()"), ws.ReadFile("Watch.cs"));

        var clockBytes = File.ReadAllBytes(ws.PathOf("Clock.cs"));
        var watchBytes = File.ReadAllBytes(ws.PathOf("Watch.cs"));
        Assert.True(clockBytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "The byte order mark was dropped.");
        Assert.False(watchBytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "A byte order mark was added.");
        Assert.DoesNotContain((byte)'\r', watchBytes);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task ReturnType_FollowsTheNullableContext()
    {
        const string annotated = """
            using System.Threading.Tasks;

            namespace App;

            public class Catalog
            {
                public string? Find(int id) => LookupAsync(id).Result;

                private static Task<string?> LookupAsync(int id) => Task.FromResult<string?>(null);
            }
            """;
        const string oblivious = """
            #nullable disable
            using System.Threading.Tasks;

            namespace App;

            public class Legacy
            {
                public object Find(Catalog catalog) => LookupAsync().Result;

                private static Task<object> LookupAsync() => Task.FromResult<object>(null);
            }
            """;
        using var ws = TestWorkspace.Create(("Catalog.cs", Lf(annotated)), ("Legacy.cs", Lf(oblivious)));

        var enabled = await ConvertAsync(ws, ws.PathOf("Catalog.cs"), "Find");
        var disabled = await ConvertAsync(ws, ws.PathOf("Legacy.cs"), "Find");

        Assert.True(enabled.Success, enabled.Error?.Message);
        Assert.True(disabled.Success, disabled.Error?.Message);
        Assert.Contains("public async Task<string?> FindAsync(int id) => await LookupAsync(id);", ws.ReadFile("Catalog.cs"));
        Assert.Contains("public async Task<object> FindAsync(Catalog catalog) => await LookupAsync();", ws.ReadFile("Legacy.cs"));
        Assert.Empty(ws.GetDiskCompilationWarningsAndErrors());
    }

    [Fact]
    public async Task RenameToAsyncFalse_KeepsTheName_AndStillAwaitsAsyncCallers()
    {
        using var ws = CreateStoreSolution();

        var result = await ConvertAsync(ws, ws.PathOf("Lib", "Store.cs"), "Load", renameToAsync: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public async Task<int> Load(int id)", File.ReadAllText(ws.PathOf("Lib", "Store.cs")));
        Assert.Contains("var first = await store.Load(1);", File.ReadAllText(ws.PathOf("App", "Use.cs")));
        Assert.Equal(3, result.ReferencesUpdated);
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task Preview_DescribesWhatTheCommitWrites_AndWritesNothing()
    {
        using var ws = CreateStoreSolution();
        var store = File.ReadAllText(ws.PathOf("Lib", "Store.cs"));
        var use = File.ReadAllText(ws.PathOf("App", "Use.cs"));

        var preview = await ConvertAsync(ws, ws.PathOf("Lib", "Store.cs"), "Load", preview: true);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.True(preview.Preview);
        Assert.Equal(store, File.ReadAllText(ws.PathOf("Lib", "Store.cs")));
        Assert.Equal(use, File.ReadAllText(ws.PathOf("App", "Use.cs")));

        var changes = preview.PendingChanges!;
        // The documentation comment's cref changed on the line before, so both lines form one change.
        Assert.Contains(changes, c =>
            c.Description == "Make 'Load' async as 'LoadAsync', returning 'Task<int>'" &&
            c.BeforeSnippet == "/// <summary>Loads; see <see cref=\"Load(int)\"/>.</summary>\n    public int Load(int id)" &&
            c.AfterSnippet == "/// <summary>Loads; see <see cref=\"LoadAsync(int)\"/>.</summary>\n    public async Task<int> LoadAsync(int id)" &&
            c.StartLine == 7 &&
            c.EndLine == 8);
        Assert.Contains(changes, c => c.Description == "Replace '.Result' with await" && c.AfterSnippet == "return await FetchAsync(id);");
        Assert.Contains(changes, c => c.Description == "Rename 'Load' to 'LoadAsync'" && c.AfterSnippet == "public string Name => nameof(LoadAsync);");
        Assert.Contains(changes, c =>
            c.Description == "Await the call to 'LoadAsync'" &&
            c.BeforeSnippet == "return first + store.Load(2).CompareTo(0);" &&
            c.AfterSnippet == "return first + (await store.LoadAsync(2)).CompareTo(0);");
        Assert.Contains(changes, c => c.Description == "Rename 'Load' to 'LoadAsync'" && c.AfterSnippet == "return store.LoadAsync(3);");

        var commit = await ConvertAsync(ws, ws.PathOf("Lib", "Store.cs"), "Load");

        Assert.True(commit.Success, commit.Error?.Message);
        Assert.Equal(preview.Warnings, commit.Warnings);
        var committed = new Dictionary<string, string>
        {
            [ws.PathOf("Lib", "Store.cs")] = File.ReadAllText(ws.PathOf("Lib", "Store.cs")),
            [ws.PathOf("App", "Use.cs")] = File.ReadAllText(ws.PathOf("App", "Use.cs"))
        };
        Assert.Equal(10, changes.Count);
        Assert.All(changes, c => Assert.Contains(c.AfterSnippet!, committed[c.File]));
        Assert.Equal(changes.Select(c => c.File).Distinct().Order(), commit.Changes!.FilesModified.Order());
    }

    [Fact]
    public async Task MultiTargetedFile_IsEditedOnce_AndACallOnlyAnotherFrameworkCompilesIsReported()
    {
        const string feed = """
            using System.Threading.Tasks;

            namespace Multi;

            public class Feed
            {
                public string Read() => FetchAsync().Result;

                public async Task<int> SizeAsync()
                {
                    await Task.Yield();
            #if NET9
                    return Read().Length + 1;
            #else
                    return Read().Length;
            #endif
                }

                private static Task<string> FetchAsync() => Task.FromResult("x");
            }
            """;
        const string use = """
            namespace Multi;

            public static class Use
            {
                public static async System.Threading.Tasks.Task<string> TwiceAsync(Feed feed) => feed.Read() + feed.Read();
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Multi", [("Feed.cs", Lf(feed)), ("Use.cs", Lf(use))], TargetFrameworks: ["net8", "net9"]));

        var result = await ConvertAsync(ws, ws.PathOf("Multi", "Feed.cs"), "Read");

        Assert.True(result.Success, result.Error?.Message);
        var text = File.ReadAllText(ws.PathOf("Multi", "Feed.cs"));
        Assert.Contains("    public async Task<string> ReadAsync() => await FetchAsync();", text);
        Assert.Contains("=> await feed.ReadAsync() + await feed.ReadAsync();", File.ReadAllText(ws.PathOf("Multi", "Use.cs")));
        Assert.Equal(
            [ws.PathOf("Multi", "Feed.cs")],
            result.Changes!.FilesModified.Where(f => f.EndsWith("Feed.cs", StringComparison.Ordinal)));

        // The branch the edited document compiles is awaited; the other one is renamed and reported.
        var awaited = text.Contains("return (await ReadAsync()).Length + 1;", StringComparison.Ordinal);
        Assert.Contains(awaited ? "        return ReadAsync().Length;" : "        return (await ReadAsync()).Length;", text);
        Assert.Contains(awaited ? "        return (await ReadAsync()).Length + 1;" : "        return ReadAsync().Length + 1;", text);
        var hidden = Assert.Single(result.Warnings!, w => w.Contains("not awaited", StringComparison.Ordinal));
        Assert.StartsWith(
            $"{ws.PathOf("Multi", "Feed.cs")}({(awaited ? 15 : 13)},16): not awaited: it is compiled only in 'Multi({(awaited ? "net8" : "net9")})' (an #if branch)",
            hidden);
    }

    [Theory]
    [InlineData("public async Task<int> Load() => await Helpers.Fetch();", ErrorCodes.AlreadyAsync, "is already async")]
    [InlineData("public IEnumerable<int> Load() { yield return Helpers.Fetch().Result; }", ErrorCodes.CannotConvertIterator, "is an iterator")]
    [InlineData("public int Load(ref int x) => Helpers.Fetch().Result + x;", ErrorCodes.NotConvertibleToAsync, "parameter 'x' is ref")]
    [InlineData("public int Load(out int x) { x = 1; return Helpers.Fetch().Result; }", ErrorCodes.NotConvertibleToAsync, "parameter 'x' is out")]
    [InlineData("public int Load(in int x) => Helpers.Fetch().Result + x;", ErrorCodes.NotConvertibleToAsync, "parameter 'x' is in")]
    [InlineData("public int Load(Span<int> x) => Helpers.Fetch().Result + x.Length;", ErrorCodes.NotConvertibleToAsync, "has type 'System.Span<int>'")]
    [InlineData("private int _value; public ref int Load() { Helpers.Fetch().Wait(); return ref _value; }", ErrorCodes.NotConvertibleToAsync, "returns by reference")]
    [InlineData("public static extern int Load();", ErrorCodes.NotConvertibleToAsync, "it is extern")]
    [InlineData("public Task<int> Load() => Helpers.Fetch();", ErrorCodes.NotConvertibleToAsync, "already returns 'System.Threading.Tasks.Task<int>'")]
    [InlineData("public int Load() => 1;", ErrorCodes.NoAsyncCalls, "has nothing to await")]
    [InlineData("public int Load() { Func<int> f = () => Helpers.Fetch().Result; return f(); }", ErrorCodes.NoAsyncCalls, "has nothing to await")]
    [InlineData("public int Load() => Helpers.Fetch().Result; public Task<int> LoadAsync() => Helpers.Fetch();", ErrorCodes.NameConflictScope, "'App.Widget.LoadAsync()' already exists")]
    [InlineData("public virtual int Load() => Helpers.Fetch().Result;", ErrorCodes.AsyncMemberInFamily, "it is virtual")]
    public async Task UnconvertibleMethods_AreRefused(string members, string code, string message)
    {
        var source = Lf($$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace App;

            public class Widget
            {
                {{members}}
            }

            internal static class Helpers
            {
                public static Task<int> Fetch() => Task.FromResult(1);
            }
            """);
        using var ws = TestWorkspace.Create(("Widget.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, ws.PathOf("Widget.cs"), "Load", line: 9));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
        Assert.Equal(source, ws.ReadFile("Widget.cs"));
    }

    [Theory]
    [InlineData("""
        public abstract class Widget
        {
            public abstract int Load();
        }
        """, "it is abstract")]
    [InlineData("""
        public class Base
        {
            public virtual int Load() => 0;
        }

        public class Widget : Base
        {
            public override int Load() => Helpers.Fetch().Result;
        }
        """, "it overrides 'App.Base.Load()'")]
    [InlineData("""
        public interface ILoader
        {
            int Load();
        }

        public class Widget : ILoader
        {
            public int Load() => Helpers.Fetch().Result;
        }
        """, "it implements 'App.ILoader.Load()'")]
    [InlineData("""
        public interface ILoader
        {
            int Load();
        }

        public class Widget : ILoader
        {
            int ILoader.Load() => Helpers.Fetch().Result;
        }
        """, "it implements 'App.ILoader.Load()'")]
    public async Task MembersOfAFamily_AreRefused_PointingToChangeReturnType(string types, string message)
    {
        var source = Lf($$"""
            using System.Threading.Tasks;

            namespace App;

            {{types}}

            internal static class Helpers
            {
                public static Task<int> Fetch() => Task.FromResult(1);
            }
            """);
        using var ws = TestWorkspace.Create(("Widget.cs", source));
        var line = source.Split('\n').ToList().FindLastIndex(l => l.Contains("Load()", StringComparison.Ordinal)) + 1;

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, ws.PathOf("Widget.cs"), "Load", line: line));

        Assert.Equal(ErrorCodes.AsyncMemberInFamily, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
        Assert.Contains("change_return_type", ex.Message);
        Assert.Equal(source, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task PartialMethod_IsRefused()
    {
        var source = Lf("""
            using System.Threading.Tasks;

            namespace App;

            public partial class Widget
            {
                public partial int Load();
                public partial int Load() => Task.FromResult(1).Result;
            }
            """);
        using var ws = TestWorkspace.Create(("Widget.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, ws.PathOf("Widget.cs"), "Load", line: 8));

        Assert.Equal(ErrorCodes.NotConvertibleToAsync, ex.ErrorCode);
        Assert.Contains("it is partial", ex.Message);
    }

    private static TestWorkspace CreateStoreSolution() => TestWorkspace.CreateSolution(
        new TestWorkspace.ProjectSpec("Lib", [("Store.cs", Lf(Store))]),
        new TestWorkspace.ProjectSpec("App", [("Use.cs", Lf(Callers))], References: ["Lib"]));

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> ConvertAsync(
        TestWorkspace ws,
        string file,
        string method,
        int? line = null,
        bool renameToAsync = true,
        bool preview = false) =>
        new ConvertToAsyncOperation(ws.Context).ExecuteAsync(
            new ConvertToAsyncParams
            {
                SourceFile = file,
                MethodName = method,
                Line = line,
                RenameToAsync = renameToAsync,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
