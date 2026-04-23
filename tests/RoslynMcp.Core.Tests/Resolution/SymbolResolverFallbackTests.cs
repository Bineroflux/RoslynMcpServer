using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

/// <summary>
/// Integration tests for <see cref="RoslynMcp.Core.Resolution.SymbolResolver"/>'s
/// line-scan column-recovery behavior. Exercised end-to-end through
/// <see cref="FindReferencesOperation"/> so the result's <c>LocationOverride</c>
/// propagation is covered at the same time.
/// </summary>
public sealed class SymbolResolverFallbackTests
{
    [Fact]
    public async Task FindReferences_WhenColumnPointsAtSymbol_DoesNotSetLocationOverride()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = SymbolResolverTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var programPath = Path.Combine(working.ProjectDir, "Program.cs");
        // Line 8: `var user = userService.CreateUser("John Doe", "john@example.com");`
        // `CreateUser` starts at column 24.
        var (line, column) = LocateTokenOnLine(programPath, 8, "CreateUser");
        Assert.Equal(24, column);

        var op = new FindReferencesOperation(ctx);
        var result = await op.ExecuteAsync(
            new FindReferencesParams
            {
                SourceFile = programPath,
                SymbolName = "CreateUser",
                Line = line,
                Column = column
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("CreateUser", result.Data.SymbolName);
        Assert.Null(result.Data.LocationOverride);
    }

    [Fact]
    public async Task FindReferences_WhenColumnIsWrongButSymbolIsUniqueOnLine_RecoversAndReportsOverride()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = SymbolResolverTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var programPath = Path.Combine(working.ProjectDir, "Program.cs");
        // Line 8: `var user = userService.CreateUser(...)` — `user` (local) at col 5,
        // `CreateUser` (the method we want) at col 24. Passing column 5 points at the
        // local variable, whose parent chain (VariableDeclarator → VariableDeclaration →
        // LocalDeclarationStatement → ...) never yields a symbol named "CreateUser",
        // so direct resolution fails and the line-scan fallback must kick in.
        const int requestedColumn = 5;
        var (_, expectedColumn) = LocateTokenOnLine(programPath, 8, "CreateUser");
        Assert.Equal(24, expectedColumn);

        var op = new FindReferencesOperation(ctx);
        var result = await op.ExecuteAsync(
            new FindReferencesParams
            {
                SourceFile = programPath,
                SymbolName = "CreateUser",
                Line = 8,
                Column = requestedColumn
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("CreateUser", result.Data.SymbolName);

        var @override = result.Data.LocationOverride;
        Assert.NotNull(@override);
        Assert.Equal(8, @override.RequestedLine);
        Assert.Equal(requestedColumn, @override.RequestedColumn);
        Assert.Equal(8, @override.ResolvedLine);
        Assert.Equal(expectedColumn, @override.ResolvedColumn);
        Assert.Contains("CreateUser", @override.Reason);

        // Sanity: the recovery must have found the actual method, not a decoy.
        Assert.Contains("TestProject.Services.UserService.CreateUser",
            result.Data.FullyQualifiedName);
    }

    [Fact]
    public async Task FindReferences_WhenColumnIsWrongAndSymbolAppearsMultipleTimesOnLine_DoesNotRecover()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = SymbolResolverTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var programPath = Path.Combine(working.ProjectDir, "Program.cs");
        // Line 22: `Console.WriteLine($"Address: {user.Address?.Street}, {user.Address?.City}");`
        // Contains two `Address` identifier tokens, so the recovery must refuse
        // to pick one and the operation must surface SymbolNotFound.
        var op = new FindReferencesOperation(ctx);
        var ex = await Assert.ThrowsAsync<RefactoringException>(() => op.ExecuteAsync(
            new FindReferencesParams
            {
                SourceFile = programPath,
                SymbolName = "Address",
                Line = 22,
                Column = 1
            },
            ct));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Contains("Address", ex.Message);
    }

    private static (int Line, int Column) LocateTokenOnLine(
        string filePath, int line, string token)
    {
        var lines = File.ReadAllLines(filePath);
        Assert.True(line >= 1 && line <= lines.Length,
            $"Line {line} is out of range for {filePath}.");
        var lineText = lines[line - 1];
        var idx = lineText.IndexOf(token, StringComparison.Ordinal);
        Assert.True(idx >= 0,
            $"Token '{token}' not found on line {line} of {filePath}: '{lineText}'.");
        // Require uniqueness so the probe is stable against content drift.
        Assert.Equal(idx, lineText.LastIndexOf(token, StringComparison.Ordinal));
        return (line, idx + 1);
    }
}

/// <summary>
/// Minimal copy of the checked-in test solution to a unique temp directory.
/// Kept local to this test fixture to avoid coupling with sibling fixtures.
/// </summary>
internal sealed class SymbolResolverTempSolution : IDisposable
{
    public string RootDir { get; }
    public string SolutionPath { get; }
    public string ProjectDir { get; }

    private SymbolResolverTempSolution(string rootDir, string solutionPath, string projectDir)
    {
        RootDir = rootDir;
        SolutionPath = solutionPath;
        ProjectDir = projectDir;
    }

    public static SymbolResolverTempSolution Create()
    {
        var source = LocateTestSolution();
        var dest = Path.Combine(Path.GetTempPath(),
            $"RoslynMcpServer.SymbolResolverFallbackTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        CopyDirectory(source, dest);

        var slnPath = Path.Combine(dest, "TestSolution.sln");
        var projDir = Path.Combine(dest, "TestProject");
        if (!File.Exists(slnPath))
            throw new FileNotFoundException($"Copied TestSolution.sln missing at {slnPath}.");
        if (!Directory.Exists(projDir))
            throw new DirectoryNotFoundException($"Copied TestProject dir missing at {projDir}.");

        return new SymbolResolverTempSolution(dest, slnPath, projDir);
    }

    private static string LocateTestSolution()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(probe, "testdata", "TestSolution");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(probe);
            if (parent == null) break;
            probe = parent.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate testdata/TestSolution.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
                Directory.Delete(RootDir, recursive: true);
        }
        catch
        {
            // File watchers may still be unhooking; ignore cleanup failures.
        }
    }
}
