using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Integration tests for <see cref="RenameSymbolOperation"/>'s "omit the column when
/// it is unambiguous" behavior, exercised end-to-end against a real MSBuild workspace.
///
/// Regression coverage for the field-declaration case: a field symbol lives on the
/// <c>VariableDeclaratorSyntax</c>, which is a <em>descendant</em> of the
/// <c>FieldDeclarationSyntax</c> — not an ancestor of the leading <c>private</c> token.
/// When the column is omitted it defaults to 1, <c>FindToken</c> lands on <c>private</c>,
/// and walking up the ancestors never reaches the declarator. The line-scan recovery in
/// <see cref="RoslynMcp.Core.Resolution.SymbolResolver"/> must therefore kick in to
/// resolve the unique identifier on the line.
/// </summary>
public sealed class RenameSymbolColumnOmissionTests
{
    [Fact]
    public async Task RenameField_WhenColumnOmitted_ResolvesUniqueIdentifierOnLine()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var userServicePath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        // Line 11: `private readonly List<User> _users = new();`
        // `_users` is the only occurrence on the line; no column is supplied.
        var op = new RenameSymbolOperation(ctx);
        var result = await op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = userServicePath,
                SymbolName = "_users",
                NewName = "_userList",
                Line = 11,
                Preview = true
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(
            result.PendingChanges,
            c => c.Description != null && c.Description.Contains("_users"));
    }

    [Fact]
    public async Task RenameField_WhenColumnPointsAtSymbol_StillWorks()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var userServicePath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        // `_users` starts at column 33 on line 11; supplying the exact column must
        // continue to resolve directly (guards against the fix regressing the happy path).
        var (line, column) = LocateTokenOnLine(userServicePath, 11, "_users");

        var op = new RenameSymbolOperation(ctx);
        var result = await op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = userServicePath,
                SymbolName = "_users",
                NewName = "_userList",
                Line = line,
                Column = column,
                Preview = true
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
    }

    [Fact]
    public async Task RenameMethod_WhenColumnOmitted_StillWorks()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var userServicePath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        // Line 13: `public User CreateUser(string name, string email)` — the method
        // declaration node IS an ancestor of the leading token, so this path always
        // worked. Kept as a guard that the shared resolver preserves it.
        var op = new RenameSymbolOperation(ctx);
        var result = await op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = userServicePath,
                SymbolName = "CreateUser",
                NewName = "MakeUser",
                Line = 13,
                Preview = true
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
    }

    [Fact]
    public async Task RenameField_WhenColumnOmittedAndApplied_RewritesAllReferences()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var userServicePath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");

        var op = new RenameSymbolOperation(ctx);
        var result = await op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = userServicePath,
                SymbolName = "_users",
                NewName = "_userList",
                Line = 11,
                Preview = false
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);

        var text = await File.ReadAllTextAsync(userServicePath, ct);
        Assert.Contains("_userList", text);
        Assert.DoesNotContain("_users", text);
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
        Assert.Equal(idx, lineText.LastIndexOf(token, StringComparison.Ordinal));
        return (line, idx + 1);
    }
}

/// <summary>
/// Minimal copy of the checked-in test solution to a unique temp directory so rename
/// operations (which write files) cannot mutate the source tree. Kept local to this
/// fixture to avoid coupling with sibling fixtures.
/// </summary>
internal sealed class RenameTempSolution : IDisposable
{
    public string RootDir { get; }
    public string SolutionPath { get; }
    public string ProjectDir { get; }

    private RenameTempSolution(string rootDir, string solutionPath, string projectDir)
    {
        RootDir = rootDir;
        SolutionPath = solutionPath;
        ProjectDir = projectDir;
    }

    public static RenameTempSolution Create()
    {
        var source = LocateTestSolution();
        var dest = Path.Combine(Path.GetTempPath(),
            $"RoslynMcpServer.RenameSymbolColumnOmissionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        CopyDirectory(source, dest);

        var slnPath = Path.Combine(dest, "TestSolution.sln");
        var projDir = Path.Combine(dest, "TestProject");
        if (!File.Exists(slnPath))
            throw new FileNotFoundException($"Copied TestSolution.sln missing at {slnPath}.");
        if (!Directory.Exists(projDir))
            throw new DirectoryNotFoundException($"Copied TestProject dir missing at {projDir}.");

        return new RenameTempSolution(dest, slnPath, projDir);
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
