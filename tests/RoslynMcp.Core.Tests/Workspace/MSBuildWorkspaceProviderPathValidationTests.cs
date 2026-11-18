using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// The workspace path is checked before anything loads. A relative path was rejected with
/// "Path must be a .sln, .slnx, .csproj, or standalone .cs file", even when it was one.
/// </summary>
public sealed class MSBuildWorkspaceProviderPathValidationTests
{
    [Theory]
    [InlineData("MySolution.sln")]
    [InlineData("src/App/App.csproj")]
    [InlineData("Program.cs")]
    public async Task RelativePath_IsRejectedAsNotAbsolute(string path)
    {
        using var provider = new MSBuildWorkspaceProvider();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            provider.CreateContextAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal($"Path must be absolute: {path}", ex.Message);
    }

    [Fact]
    public async Task AbsolutePathOfTheWrongKind_StillNamesTheAcceptedKinds()
    {
        using var provider = new MSBuildWorkspaceProvider();
        var path = Path.Combine(Path.GetTempPath(), "notes.txt");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            provider.CreateContextAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Contains(".sln, .slnx, .csproj", ex.Message);
    }
}
