using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// The SDK fallback used when MSBuildLocator finds no instance: it only looked in
/// <c>%ProgramFiles%\dotnet\sdk</c> and ignored prerelease labels when ordering versions.
/// </summary>
public sealed class DotNetSdkLocatorTests
{
    [Theory]
    [InlineData("11.0.100", "9.0.321")]
    [InlineData("11.0.100", "11.0.100-rc.2.25502.107")]
    [InlineData("11.0.100-rc.2.25502.107", "11.0.100-rc.1.25451.107")]
    [InlineData("11.0.100-rc.10", "11.0.100-rc.9")]
    [InlineData("11.0.100-rc.1", "11.0.100-preview.7")]
    [InlineData("11.0.100-rc.1.1", "11.0.100-rc.1")]
    [InlineData("11.0.101", "11.0.100")]
    public void CompareVersions_OrdersLikeSemVer(string newer, string older)
    {
        Assert.True(DotNetSdkLocator.CompareVersions(newer, older) > 0);
        Assert.True(DotNetSdkLocator.CompareVersions(older, newer) < 0);
        Assert.Equal(0, DotNetSdkLocator.CompareVersions(newer, newer));
    }

    [Fact]
    public void SelectNewest_PrefersARelease_AndSkipsSdksNewerThanTheRuntime()
    {
        var sdks = new[]
        {
            new DotNetSdk("9.0.321", "a"),
            new DotNetSdk("11.0.100-rc.2.25502.107", "b"),
            new DotNetSdk("11.0.100", "c"),
            new DotNetSdk("12.0.100-preview.1", "d"),
        };

        Assert.Equal("c", DotNetSdkLocator.SelectNewest(sdks, runtimeMajor: 11)?.Path);
        Assert.Equal("a", DotNetSdkLocator.SelectNewest(sdks, runtimeMajor: 10)?.Path);
        Assert.Null(DotNetSdkLocator.SelectNewest(sdks, runtimeMajor: 8));
    }

    [Fact]
    public void FindAll_ListsVersionFoldersThatContainMsBuild()
    {
        using var root = new TempDirectory();
        root.CreateSdk("11.0.100");
        root.CreateSdk("10.0.100-rc.1");
        Directory.CreateDirectory(Path.Combine(root.Path, "sdk", "9.0.100")); // no MSBuild
        Directory.CreateDirectory(Path.Combine(root.Path, "sdk", "NuGetFallbackFolder"));

        var sdks = DotNetSdkLocator.FindAll([root.Path, Path.Combine(root.Path, "missing")]);

        Assert.Equal(new[] { "10.0.100-rc.1", "11.0.100" }, sdks.Select(s => s.Version).Order());
    }

    [Fact]
    public void GetSearchRoots_ProbesDotnetRootAndTheHostOnPath()
    {
        using var dotnetRoot = new TempDirectory();
        using var hostRoot = new TempDirectory();
        File.WriteAllText(Path.Combine(hostRoot.Path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
        var path = string.Join(Path.PathSeparator, Path.Combine(hostRoot.Path, "missing"), hostRoot.Path);

        var roots = DotNetSdkLocator.GetSearchRoots(name => name switch
        {
            "DOTNET_ROOT" => dotnetRoot.Path,
            "PATH" => path,
            _ => null
        });

        Assert.Equal(dotnetRoot.Path, roots[0]);
        Assert.Contains(hostRoot.Path, roots);
        Assert.All(roots, root => Assert.True(Directory.Exists(root)));
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The runtime running this test lives under one of the roots.
        Assert.Contains(roots, root => typeof(object).Assembly.Location.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RoslynMcpServer.SdkLocator-{Guid.NewGuid():N}")));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void CreateSdk(string version)
        {
            var dir = System.IO.Path.Combine(Path, "sdk", version);
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, "Microsoft.Build.dll"), "");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
