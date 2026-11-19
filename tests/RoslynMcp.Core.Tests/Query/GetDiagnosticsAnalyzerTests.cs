using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// get_diagnostics' <c>includeAnalyzers</c>: the project's analyzer references run like in the
/// build, with the severities of <c>.editorconfig</c>, the suppressions and the suppressors, and
/// the other options narrow which analyzers run.
/// </summary>
public sealed class GetDiagnosticsAnalyzerTests
{
    // A.cs: two methods (TEST0001 each), one of them under #pragma, plus an unused local (CS0168).
    private const string ASource = """
        namespace App;

        public class A
        {
            public void First()
            {
                int unused;
            }

        #pragma warning disable TEST0001
            public void Hidden() { }
        #pragma warning restore TEST0001
        }
        """;

    // B.cs: one method (TEST0001), one suppressed by attribute.
    private const string BSource = """
        namespace App;

        public class B
        {
            public void Second() { }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Test", "TEST0001")]
            public void Attributed() { }
        }
        """;

    private const string RaiseToWarning = """
        root = true

        [*.cs]
        dotnet_diagnostic.TEST0001.severity = warning
        """;

    [Fact]
    public async Task WithoutIncludeAnalyzers_TheAnalyzersDoNotRun()
    {
        var methods = new MethodAnalyzer();
        using var ws = await CreateAsync(RaiseToWarning, methods);

        var result = await GetAsync(ws, new GetDiagnosticsParams());

        Assert.Equal(["CS0168"], result.Diagnostics.Select(d => d.Id));
        Assert.Empty(methods.AnalyzedFiles);
    }

    [Fact]
    public async Task IncludeAnalyzers_ReportsTheirDiagnostics_WithTheSeverityOfTheEditorConfig()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer());

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true });

        Assert.Equal(["CS0168", "TEST0001 First", "TEST0001 Second"], result.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
        var first = result.Diagnostics.Single(d => d.Message.Contains("First"));
        Assert.Equal("Warning", first.Severity);
        Assert.Equal("Test", first.Category);
        Assert.Equal(ws.PathOf("A.cs"), first.File);
        Assert.Equal(5, first.Line);
    }

    [Fact]
    public async Task IncludeAnalyzers_WithoutEditorConfig_KeepsTheDefaultSeverity()
    {
        using var ws = await CreateAsync(editorConfig: null, new MethodAnalyzer());

        var warnings = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true });
        var info = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "Info" });

        Assert.Equal(["CS0168"], warnings.Diagnostics.Select(d => d.Id));
        Assert.Equal(["CS0168", "TEST0001 First", "TEST0001 Second"], info.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
        Assert.All(info.Diagnostics.Where(d => d.Id == "TEST0001"), d => Assert.Equal("Info", d.Severity));
    }

    [Fact]
    public async Task EditorConfig_CanRaiseToErrorAndTurnOff()
    {
        const string editorConfig = """
            root = true

            [*.cs]
            dotnet_diagnostic.TEST0001.severity = error
            dotnet_diagnostic.TEST0002.severity = none
            """;
        using var ws = await CreateAsync(editorConfig, new MethodAnalyzer(), new TypeAnalyzer());

        var errors = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "Error" });
        var all = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "All" });

        Assert.Equal(["TEST0001 First", "TEST0001 Second"], errors.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(all.Diagnostics, d => d.Id == "TEST0002");
    }

    [Fact]
    public async Task Suppressor_HidesTheCompilersAndTheAnalyzersDiagnostics()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer(), new Suppressor("CS0168"), new Suppressor("TEST0001"));

        var without = await GetAsync(ws, new GetDiagnosticsParams());
        var whole = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "All" });
        var file = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "All", SourceFile = ws.PathOf("A.cs") });

        Assert.Equal(["CS0168"], without.Diagnostics.Select(d => d.Id));
        Assert.DoesNotContain(whole.Diagnostics, d => d.Id is "CS0168" or "TEST0001");
        Assert.DoesNotContain(file.Diagnostics, d => d.Id is "CS0168" or "TEST0001");
    }

    [Fact]
    public async Task ThrowingAnalyzer_IsReportedAsAD0001_AndTheOthersStillReport()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer(), new ThrowingAnalyzer());

        var whole = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true });
        var file = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SourceFile = ws.PathOf("A.cs") });

        // One per failed callback: the whole compilation has two types, the file one.
        Assert.Equal(2, whole.Diagnostics.Count(d => d.Id == "AD0001"));
        Assert.Equal(1, file.Diagnostics.Count(d => d.Id == "AD0001"));
        foreach (var result in new[] { whole, file })
        {
            Assert.All(result.Diagnostics.Where(d => d.Id == "AD0001"), crash =>
            {
                Assert.Equal("Warning", crash.Severity);
                Assert.Contains(nameof(ThrowingAnalyzer), crash.Message);
                Assert.Contains("analyzer bug", crash.Message);
            });
            Assert.Contains(result.Diagnostics, d => Describe(d) == "TEST0001 First");
        }
    }

    [Fact]
    public async Task DiagnosticIds_RunOnlyTheAnalyzersThatReportThem()
    {
        var methods = new MethodAnalyzer();
        var types = new TypeAnalyzer();
        using var ws = await CreateAsync(RaiseToWarning, methods, types);

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "All", DiagnosticIds = ["test0002"] });

        Assert.Equal(["TEST0002 A", "TEST0002 B"], result.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
        Assert.Empty(methods.AnalyzedFiles);
        Assert.NotEmpty(types.AnalyzedFiles);
    }

    [Fact]
    public async Task ExcludeDiagnosticIds_SkipsTheAnalyzersThatOnlyReportThem()
    {
        var methods = new MethodAnalyzer();
        var types = new TypeAnalyzer();
        using var ws = await CreateAsync(RaiseToWarning, methods, types);

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SeverityFilter = "All", ExcludeDiagnosticIds = ["TEST0001"] });

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "TEST0001");
        Assert.Contains(result.Diagnostics, d => d.Id == "TEST0002");
        Assert.Empty(methods.AnalyzedFiles);
    }

    [Fact]
    public async Task MessageFilterAndMaxResults_ApplyToAnalyzerDiagnostics()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer());

        var byMessage = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, MessageFilter = "'second'" });
        var capped = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, MaxResults = 1 });

        Assert.Equal(["TEST0001 Second"], byMessage.Diagnostics.Select(Describe));
        Assert.Single(capped.Diagnostics);
        Assert.Equal(3, capped.TotalCount);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public async Task SourceFile_RunsTheAnalyzersOnThatFile_ButCompilationEndOnesOnTheWholeCompilation()
    {
        var methods = new MethodAnalyzer();
        using var ws = await CreateAsync(RaiseToWarning, methods, new CompilationEndAnalyzer());

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SourceFile = ws.PathOf("A.cs") });

        Assert.Equal(["CS0168", "TEST0001 First", "TEST0003 A"], result.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
        Assert.Equal([ws.PathOf("A.cs")], methods.AnalyzedFiles.Distinct());
    }

    [Fact]
    public async Task SourceFile_OfAnotherProject_SkipsTheAnalyzers()
    {
        var appMethods = new MethodAnalyzer();
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("App", [("A.cs", ASource)]),
            new TestWorkspace.ProjectSpec("Lib", [("B.cs", BSource.Replace("namespace App;", "namespace Lib;"))]));
        var app = ws.Context.Solution.Projects.Single(p => p.Name == "App");
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.AddAnalyzerReference(app.Id, new AnalyzerImageReference([appMethods])), TestContext.Current.CancellationToken);

        var result = await GetAsync(ws, new GetDiagnosticsParams
        {
            IncludeAnalyzers = true,
            SeverityFilter = "All",
            SourceFile = ws.PathOf("Lib", "B.cs")
        });

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "TEST0001");
        Assert.Empty(appMethods.AnalyzedFiles);
    }

    [Fact]
    public async Task ProjectPath_RunsOnlyThatProjectsAnalyzers()
    {
        var appMethods = new MethodAnalyzer();
        var libMethods = new MethodAnalyzer();
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("App", [("A.cs", ASource)]),
            new TestWorkspace.ProjectSpec("Lib", [("B.cs", BSource.Replace("namespace App;", "namespace Lib;"))]));
        var app = ws.Context.Solution.Projects.Single(p => p.Name == "App");
        var lib = ws.Context.Solution.Projects.Single(p => p.Name == "Lib");
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.AddAnalyzerReference(app.Id, new AnalyzerImageReference([appMethods]))
                .AddAnalyzerReference(lib.Id, new AnalyzerImageReference([libMethods])),
            TestContext.Current.CancellationToken);

        var result = await GetAsync(ws, new GetDiagnosticsParams
        {
            IncludeAnalyzers = true,
            SeverityFilter = "All",
            ProjectPath = ws.ProjectPathOf("Lib")
        });

        Assert.Equal(["TEST0001 Second"], result.Diagnostics.Select(Describe));
        Assert.Empty(appMethods.AnalyzedFiles);
    }

    [Fact]
    public async Task AnalyzersThatCannotBeListed_AreReportedAsRMCP0003_WithTheCompilersDiagnostics()
    {
        using var ws = await CreateAsync(RaiseToWarning);
        var project = ws.Context.Solution.Projects.Single();
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.AddAnalyzerReference(project.Id, new UnlistableAnalyzerReference()), TestContext.Current.CancellationToken);

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true });
        var filtered = await GetAsync(ws, new GetDiagnosticsParams
        {
            IncludeAnalyzers = true,
            SeverityFilter = "Error",
            DiagnosticIds = ["TEST0001"],
            ExcludeDiagnosticIds = ["RMCP0003"],
            MessageFilter = "no such text"
        });

        var failure = Assert.Single(result.Diagnostics, d => d.Id == "RMCP0003");
        Assert.Contains("'TestProject'", failure.Message);
        Assert.Contains("unlistable", failure.Message);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS0168");
        Assert.Equal(["RMCP0003"], filtered.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public async Task AD0001_OfAnAnalyzerThatRan_PassesEveryFilter()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer(), new ThrowingAnalyzer());

        var wanted = await GetAsync(ws, new GetDiagnosticsParams
        {
            IncludeAnalyzers = true,
            DiagnosticIds = ["TEST0004"],
            ExcludeDiagnosticIds = ["AD0001"],
            MessageFilter = "no such text",
            SeverityFilter = "Error",
            SourceFile = ws.PathOf("B.cs")
        });
        var notWanted = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, DiagnosticIds = ["TEST0001"] });

        // The throwing analyzer reports TEST0004, so it ran for the first call (on B.cs only: one
        // type, one AD0001) and not for the second.
        var crash = Assert.Single(wanted.Diagnostics);
        Assert.Equal("AD0001", crash.Id);
        Assert.Contains(nameof(ThrowingAnalyzer), crash.Message);
        Assert.Equal(["TEST0001 First", "TEST0001 Second"], notWanted.Diagnostics.Select(Describe).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnalyzerFailures_ComeFirst_AndCountTowardMaxResults()
    {
        using var ws = await CreateAsync(RaiseToWarning, new MethodAnalyzer(), new ThrowingAnalyzer());

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, MaxResults = 2 });

        // Two AD0001 (one per type), then CS0168 and the two TEST0001.
        Assert.Equal(["AD0001", "AD0001"], result.Diagnostics.Select(d => d.Id));
        Assert.Equal(5, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task AnalyzerLoadFailure_OfAProjectWhoseAnalyzersRan_PassesEveryFilter()
    {
        string[] issues =
        [
            MSBuildWorkspaceProvider.BuildAnalyzerLoadFailureMessage(
                "App", "AppAnalyzers", AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToLoadAnalyzer, "boom", null),
            MSBuildWorkspaceProvider.BuildAnalyzerLoadFailureMessage(
                "Lib", "LibAnalyzers", AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToLoadAnalyzer, "boom", null),
            "RazorSourceGenerator failed to load for project 'App' (unresolved analyzer reference: razor.dll). " +
            ".razor / .cshtml diagnostics will not appear.",
        ];
        using var ws = TestWorkspace.CreateSolution(
            [
                new TestWorkspace.ProjectSpec("App", [("A.cs", ASource)]),
                new TestWorkspace.ProjectSpec("Lib", [("B.cs", BSource.Replace("namespace App;", "namespace Lib;"))]),
            ],
            issues);
        var filters = new GetDiagnosticsParams
        {
            DiagnosticIds = ["CA1822"],
            MessageFilter = "no such text",
            SeverityFilter = "Error"
        };

        var whole = await GetAsync(ws, With(filters, includeAnalyzers: true));
        var withoutAnalyzers = await GetAsync(ws, filters);
        var lib = await GetAsync(ws, With(filters, includeAnalyzers: true, projectPath: ws.ProjectPathOf("Lib")));
        var appFile = await GetAsync(ws, With(filters, includeAnalyzers: true, sourceFile: ws.PathOf("App", "A.cs")));

        Assert.Equal(["AppAnalyzers", "LibAnalyzers"], whole.Diagnostics.Select(LoadFailureName));
        Assert.All(whole.Diagnostics, d => Assert.Equal("RMCP0001", d.Id));
        Assert.Empty(withoutAnalyzers.Diagnostics);
        Assert.Equal(["LibAnalyzers"], lib.Diagnostics.Select(LoadFailureName));
        Assert.Equal(["AppAnalyzers"], appFile.Diagnostics.Select(LoadFailureName));

        static GetDiagnosticsParams With(
            GetDiagnosticsParams p, bool includeAnalyzers, string? projectPath = null, string? sourceFile = null) =>
            new()
            {
                DiagnosticIds = p.DiagnosticIds,
                MessageFilter = p.MessageFilter,
                SeverityFilter = p.SeverityFilter,
                IncludeAnalyzers = includeAnalyzers,
                ProjectPath = projectPath,
                SourceFile = sourceFile
            };

        static string LoadFailureName(DiagnosticInfo d) => d.Message.Split('\'')[1];
    }

    [Fact]
    public async Task UnresolvedAnalyzerReference_OfAProjectWhoseAnalyzersRun_IsRMCP0003_WhateverTheFilters()
    {
        var projects = new[]
        {
            new TestWorkspace.ProjectSpec("App", [("A.cs", ASource)]),
            new TestWorkspace.ProjectSpec("Lib", [("B.cs", BSource.Replace("namespace App;", "namespace Lib;"))]),
        };
        var unresolved = new Dictionary<string, string[]>
        {
            ["App"] = [@"C:\missing\App.Analyzers.dll"],
            ["Lib"] = [@"C:\missing\Lib.Analyzers.dll"],
        };
        using var ws = TestWorkspace.CreateSolution(projects, generatorLoadIssues: null, unresolved);
        using var plain = TestWorkspace.CreateSolution(projects, generatorLoadIssues: null);
        var filters = new GetDiagnosticsParams
        {
            IncludeAnalyzers = true,
            DiagnosticIds = ["CA1822"],
            ExcludeDiagnosticIds = ["RMCP0003"],
            MessageFilter = "no such text",
            SeverityFilter = "Error"
        };

        var whole = await GetAsync(ws, filters);
        var lib = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, ProjectPath = ws.ProjectPathOf("Lib") });
        var appFile = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, SourceFile = ws.PathOf("App", "A.cs") });

        Assert.Equal(
            [
                @"Analyzer reference 'C:\missing\App.Analyzers.dll' of project 'App' could not be resolved (the file does not exist), " +
                "so its analyzers did not run; the result may be incomplete.",
                @"Analyzer reference 'C:\missing\Lib.Analyzers.dll' of project 'Lib' could not be resolved (the file does not exist), " +
                "so its analyzers did not run; the result may be incomplete.",
            ],
            whole.Diagnostics.Select(d => d.Message));
        Assert.All(whole.Diagnostics, d => Assert.Equal("RMCP0003", d.Id));
        Assert.Equal(["RMCP0003"], lib.Diagnostics.Select(d => d.Id));
        Assert.Contains("Lib.Analyzers.dll", lib.Diagnostics[0].Message);
        Assert.Equal("RMCP0003", appFile.Diagnostics[0].Id);
        Assert.Contains("App.Analyzers.dll", appFile.Diagnostics[0].Message);
        Assert.DoesNotContain(appFile.Diagnostics, d => d.Message.Contains("Lib.Analyzers.dll"));

        // Without includeAnalyzers nothing changes.
        foreach (var severity in new[] { "Warning", "All" })
        {
            var with = await GetAsync(ws, new GetDiagnosticsParams { SeverityFilter = severity });
            var without = await GetAsync(plain, new GetDiagnosticsParams { SeverityFilter = severity });
            Assert.Equal(without.Diagnostics.Select(d => (d.Id, d.Message, d.Line)), with.Diagnostics.Select(d => (d.Id, d.Message, d.Line)));
            Assert.DoesNotContain(with.Diagnostics, d => d.Id.StartsWith("RMCP", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ProjectThatCannotBeCompiled_ReportsRMCP0003_WhateverTheFilters()
    {
        using var ws = await CreateAsync(RaiseToWarning);
        var project = ws.Context.Solution.Projects.Single();
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.AddAnalyzerReference(project.Id, new GeneratorThrowingAnalyzerReference()), TestContext.Current.CancellationToken);

        var result = await GetAsync(ws, new GetDiagnosticsParams { IncludeAnalyzers = true, DiagnosticIds = ["TEST0001"], SeverityFilter = "Error" });

        var failure = Assert.Single(result.Diagnostics);
        Assert.Equal("RMCP0003", failure.Id);
        Assert.Contains("could not be compiled", failure.Message);
    }

    [Fact]
    public async Task Cancellation_StopsTheAnalyzers()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var ws = await CreateAsync(RaiseToWarning, new CancellingAnalyzer(cts));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new GetDiagnosticsOperation(ws.Context).ExecuteAsync(new GetDiagnosticsParams { IncludeAnalyzers = true }, cts.Token));

        Assert.Equal(ErrorCodes.Timeout, ex.ErrorCode);
    }

    private static async Task<TestWorkspace> CreateAsync(string? editorConfig, params DiagnosticAnalyzer[] analyzers)
    {
        var ws = TestWorkspace.Create([("A.cs", ASource), ("B.cs", BSource)], [], editorConfig: editorConfig);
        if (analyzers.Length > 0)
        {
            var project = ws.Context.Solution.Projects.Single();
            await ws.Context.UpdateSolutionInWriterAsync(
                s => s.AddAnalyzerReference(project.Id, new AnalyzerImageReference([.. analyzers])), TestContext.Current.CancellationToken);
        }

        return ws;
    }

    private static async Task<GetDiagnosticsResult> GetAsync(TestWorkspace ws, GetDiagnosticsParams @params)
    {
        var result = await new GetDiagnosticsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    /// <summary>The ID, and for the test analyzers the name they report.</summary>
    private static string Describe(DiagnosticInfo diagnostic) =>
        diagnostic.Id.StartsWith("TEST", StringComparison.Ordinal)
            ? diagnostic.Id + " " + diagnostic.Message.Split('\'')[1]
            : diagnostic.Id;

#pragma warning disable RS1036, RS1038, RS1041, RS2008 // test analyzers, loaded from memory, never shipped

    /// <summary>TEST0001 (Info) on every method; records the files it analyzed.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class MethodAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0001", "Method", "Method '{0}'", "Test", DiagnosticSeverity.Info, isEnabledByDefault: true);

        public ConcurrentQueue<string> AnalyzedFiles { get; } = new();

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(
                c =>
                {
                    if (c.Symbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                        return;

                    AnalyzedFiles.Enqueue(method.Locations[0].SourceTree!.FilePath);
                    c.ReportDiagnostic(Diagnostic.Create(Rule, method.Locations[0], method.Name));
                },
                SymbolKind.Method);
        }
    }

    /// <summary>TEST0002 (Info) on every type; records the files it analyzed.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class TypeAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0002", "Type", "Type '{0}'", "Test", DiagnosticSeverity.Info, isEnabledByDefault: true);

        public ConcurrentQueue<string> AnalyzedFiles { get; } = new();

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(
                c =>
                {
                    AnalyzedFiles.Enqueue(c.Symbol.Locations[0].SourceTree!.FilePath);
                    c.ReportDiagnostic(Diagnostic.Create(Rule, c.Symbol.Locations[0], c.Symbol.Name));
                },
                SymbolKind.NamedType);
        }
    }

    /// <summary>TEST0003 (Warning) on every type, reported at the end of the compilation.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class CompilationEndAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0003", "Type at the end", "Type '{0}'", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            customTags: [WellKnownDiagnosticTags.CompilationEnd]);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var types = new ConcurrentQueue<ISymbol>();
                start.RegisterSymbolAction(c => types.Enqueue(c.Symbol), SymbolKind.NamedType);
                start.RegisterCompilationEndAction(end =>
                {
                    foreach (var type in types)
                        end.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations[0], type.Name));
                });
            });
        }
    }

    /// <summary>Throws while analyzing a type.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ThrowingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0004", "Never", "Never", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(_ => throw new InvalidOperationException("analyzer bug"), SymbolKind.NamedType);
        }
    }

    /// <summary>Cancels the call while analyzing a type.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class CancellingAnalyzer(CancellationTokenSource cts) : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST0005", "Never", "Never", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(
                c =>
                {
                    cts.Cancel();
                    c.CancellationToken.ThrowIfCancellationRequested();
                },
                SymbolKind.NamedType);
        }
    }

    /// <summary>Suppresses every diagnostic with one ID.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class Suppressor(string suppressedId) : DiagnosticSuppressor
    {
        private readonly SuppressionDescriptor _rule = new("SPR" + suppressedId, suppressedId, "test");

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [_rule];

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
                context.ReportSuppression(Suppression.Create(_rule, diagnostic));
        }
    }

#pragma warning restore RS1036, RS1038, RS1041, RS2008

    /// <summary>An analyzer reference whose generators can't be listed, which fails the compilation.</summary>
    private sealed class GeneratorThrowingAnalyzerReference : AnalyzerReference
    {
        public override string FullPath => "generators.dll";
        public override string Display => "generators";
        public override object Id => "generators-id";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
            => throw new InvalidOperationException("generator boom");
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => throw new InvalidOperationException("generator boom");
    }

    /// <summary>An analyzer reference whose analyzers can't be listed.</summary>
    private sealed class UnlistableAnalyzerReference : AnalyzerReference
    {
        public override string FullPath => "unlistable.dll";
        public override string Display => "unlistable";
        public override object Id => "unlistable-id";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages()
            => throw new InvalidOperationException("boom");
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language)
            => throw new InvalidOperationException("boom");
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => [];
    }
}
