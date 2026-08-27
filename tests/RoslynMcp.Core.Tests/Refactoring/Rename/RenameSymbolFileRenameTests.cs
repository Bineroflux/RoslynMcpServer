using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Integration tests for <see cref="RenameSymbolOperation"/>'s <c>renameFile</c> handling,
/// exercised end-to-end against a real MSBuild workspace.
///
/// Regression coverage for the self-collision: the operation repoints the Roslyn document
/// at its new path before committing, so the commit writes the renamed source under the
/// new file name. A post-commit "does the target already exist?" check therefore always
/// found the operation's own output, skipped the move, and left the original file behind
/// with the old type name — two files where there should have been one.
/// </summary>
public sealed class RenameSymbolFileRenameTests
{
    private const int UserServiceDeclarationLine = 9;

    private const string OccupiedContent =
        "namespace TestProject.Services;\n\npublic sealed class PreExisting;\n";

    [Fact]
    public async Task RenameType_WithRenameFile_RenamesFileAndRemovesOriginal()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var oldPath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        var newPath = Path.Combine(working.ProjectDir, "Services", "AccountService.cs");

        var result = await RenameUserServiceAsync(ctx, oldPath, "AccountService", preview: false, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);

        Assert.True(File.Exists(newPath), "Renamed file was not created.");
        Assert.False(File.Exists(oldPath), "Original file was left behind after the rename.");

        var text = await File.ReadAllTextAsync(newPath, ct);
        Assert.Contains("class AccountService", text);
        Assert.DoesNotContain("class UserService", text);

        // References elsewhere in the solution follow the type, not the file.
        var programText = await File.ReadAllTextAsync(
            Path.Combine(working.ProjectDir, "Program.cs"), ct);
        Assert.Contains("new AccountService()", programText);
    }

    [Fact]
    public async Task RenameType_WithRenameFile_ReportsCreatedAndDeletedPaths()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var oldPath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        var newPath = Path.Combine(working.ProjectDir, "Services", "AccountService.cs");

        var result = await RenameUserServiceAsync(ctx, oldPath, "AccountService", preview: false, ct);

        Assert.True(result.Success, result.Error?.Message);
        var changes = Assert.IsType<FileChanges>(result.Changes);

        Assert.Contains(changes.FilesCreated, f => PathResolver.PathsEqual(f, newPath));
        Assert.Contains(changes.FilesDeleted, f => PathResolver.PathsEqual(f, oldPath));

        // The renamed file is reported as created, not as a modification of a path that
        // did not exist before the operation.
        Assert.DoesNotContain(changes.FilesModified, f => PathResolver.PathsEqual(f, newPath));
        Assert.Contains(
            changes.FilesModified,
            f => PathResolver.PathsEqual(f, Path.Combine(working.ProjectDir, "Program.cs")));
    }

    [Fact]
    public async Task RenameType_WhenTargetFileAlreadyExists_KeepsTargetAndRenamesInPlace()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        var oldPath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        var occupiedPath = Path.Combine(working.ProjectDir, "Services", "AccountService.cs");
        await File.WriteAllTextAsync(occupiedPath, OccupiedContent, ct);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var result = await RenameUserServiceAsync(ctx, oldPath, "AccountService", preview: false, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Error);
        Assert.Equal("PARTIAL_SUCCESS", result.Error!.Code);
        Assert.Contains("already exists", result.Error.Message);

        // The unrelated file occupying the target path must survive untouched.
        Assert.Equal(OccupiedContent, await File.ReadAllTextAsync(occupiedPath, ct));

        // ...and the rename still lands, in place, in the original file.
        Assert.True(File.Exists(oldPath), "Source file was removed even though the rename was skipped.");
        Assert.Contains("class AccountService", await File.ReadAllTextAsync(oldPath, ct));
    }

    [Fact]
    public async Task RenameType_PreviewWithOccupiedTarget_ReportsCollisionAndTouchesNothing()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        var oldPath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        var occupiedPath = Path.Combine(working.ProjectDir, "Services", "AccountService.cs");
        await File.WriteAllTextAsync(occupiedPath, OccupiedContent, ct);
        var originalSource = await File.ReadAllTextAsync(oldPath, ct);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var result = await RenameUserServiceAsync(ctx, oldPath, "AccountService", preview: true, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Preview);
        Assert.NotNull(result.Error);
        Assert.Contains("already exists", result.Error!.Message);

        // Preview must not promise a file rename it will not perform.
        Assert.NotNull(result.PendingChanges);
        Assert.DoesNotContain(
            result.PendingChanges!,
            c => PathResolver.PathsEqual(c.File, occupiedPath));

        Assert.Equal(OccupiedContent, await File.ReadAllTextAsync(occupiedPath, ct));
        Assert.Equal(originalSource, await File.ReadAllTextAsync(oldPath, ct));
    }

    [Fact]
    public async Task RenameType_CaseOnlyRename_LeavesExactlyOneFile()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var servicesDir = Path.Combine(working.ProjectDir, "Services");
        var oldPath = Path.Combine(servicesDir, "UserService.cs");

        // On a case-insensitive file system the target path resolves to the very same
        // file, which must not be mistaken for a collision.
        var result = await RenameUserServiceAsync(ctx, oldPath, "userService", preview: false, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);

        var serviceFiles = Directory.GetFiles(servicesDir, "*.cs")
            .Select(Path.GetFileName)
            .Where(n => string.Equals(n, "UserService.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(new[] { "userService.cs" }, serviceFiles);

        var text = await File.ReadAllTextAsync(Path.Combine(servicesDir, "userService.cs"), ct);
        Assert.Contains("class userService", text);
    }

    [Fact]
    public async Task RenameType_WithRenameFileDisabled_KeepsOriginalFileName()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var oldPath = Path.Combine(working.ProjectDir, "Services", "UserService.cs");
        var newPath = Path.Combine(working.ProjectDir, "Services", "AccountService.cs");

        var op = new RenameSymbolOperation(ctx);
        var result = await op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = oldPath,
                SymbolName = "UserService",
                NewName = "AccountService",
                Line = UserServiceDeclarationLine,
                RenameFile = false
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.True(File.Exists(oldPath));
        Assert.False(File.Exists(newPath));
        Assert.Contains("class AccountService", await File.ReadAllTextAsync(oldPath, ct));
    }

    private static Task<RefactoringResult> RenameUserServiceAsync(
        WorkspaceContext ctx,
        string sourceFile,
        string newName,
        bool preview,
        CancellationToken ct)
    {
        var op = new RenameSymbolOperation(ctx);
        return op.ExecuteAsync(
            new RenameSymbolParams
            {
                SourceFile = sourceFile,
                SymbolName = "UserService",
                NewName = newName,
                Line = UserServiceDeclarationLine,
                RenameFile = true,
                Preview = preview
            },
            ct);
    }
}
