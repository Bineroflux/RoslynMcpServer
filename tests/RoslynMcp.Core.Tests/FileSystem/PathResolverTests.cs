using RoslynMcp.Core.FileSystem;
using Xunit;

namespace RoslynMcp.Core.Tests.FileSystem;

public class PathResolverTests
{
    private static string WinOrUnix(string winPath, string unixPath) =>
        OperatingSystem.IsWindows() ? winPath : unixPath;

    [Theory]
    [MemberData(nameof(IsAbsolutePathData))]
    public void IsAbsolutePath_ReturnsExpected(string? path, bool expected)
    {
        var result = PathResolver.IsAbsolutePath(path!);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string?, bool> IsAbsolutePathData => new()
    {
        { WinOrUnix(@"C:\path\to\file.cs", "/path/to/file.cs"), true },
        { @"/usr/local/file.cs", true },
        { @"relative\path.cs", false },
        { @".\file.cs", false },
        { @"..\file.cs", false },
        { "", false },
        { null, false }
    };

    [Theory]
    [MemberData(nameof(IsValidCSharpFilePathData))]
    public void IsValidCSharpFilePath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidCSharpFilePath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidCSharpFilePathData => new()
    {
        { WinOrUnix(@"C:\project\src\File.cs", "/project/src/File.cs"), true },
        { WinOrUnix(@"C:\project\src\File.CS", "/project/src/File.CS"), true },
        { @"/home/user/File.cs", true },
        { WinOrUnix(@"C:\project\src\File.txt", "/project/src/File.txt"), false },
        { @"relative\File.cs", false },
        { "", false }
    };

    [Theory]
    [MemberData(nameof(IsValidSolutionOrProjectPathData))]
    public void IsValidSolutionOrProjectPath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidSolutionOrProjectPath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidSolutionOrProjectPathData => new()
    {
        { WinOrUnix(@"C:\project\Solution.sln", "/project/Solution.sln"), true },
        { WinOrUnix(@"C:\project\Solution.slnx", "/project/Solution.slnx"), true },
        { WinOrUnix(@"C:\project\Project.csproj", "/project/Project.csproj"), true },
        { WinOrUnix(@"C:\project\File.cs", "/project/File.cs"), false },
        { @"relative\Solution.sln", false }
    };

    [Theory]
    [MemberData(nameof(IsValidRazorFilePathData))]
    public void IsValidRazorFilePath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidRazorFilePath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidRazorFilePathData => new()
    {
        { WinOrUnix(@"C:\project\Pages\Counter.razor", "/project/Pages/Counter.razor"), true },
        { WinOrUnix(@"C:\project\Pages\Counter.RAZOR", "/project/Pages/Counter.RAZOR"), true },
        { WinOrUnix(@"C:\project\Views\Index.cshtml", "/project/Views/Index.cshtml"), true },
        { WinOrUnix(@"C:\project\src\File.cs", "/project/src/File.cs"), false },
        { WinOrUnix(@"C:\project\src\File.txt", "/project/src/File.txt"), false },
        { @"relative\Counter.razor", false },
        { "", false }
    };

    [Theory]
    [MemberData(nameof(IsValidDiagnosticsSourcePathData))]
    public void IsValidDiagnosticsSourcePath_ReturnsExpected(string path, bool expected)
    {
        var result = PathResolver.IsValidDiagnosticsSourcePath(path);
        Assert.Equal(expected, result);
    }

    public static TheoryData<string, bool> IsValidDiagnosticsSourcePathData => new()
    {
        { WinOrUnix(@"C:\project\src\File.cs", "/project/src/File.cs"), true },
        { WinOrUnix(@"C:\project\Pages\Counter.razor", "/project/Pages/Counter.razor"), true },
        { WinOrUnix(@"C:\project\Views\Index.cshtml", "/project/Views/Index.cshtml"), true },
        { WinOrUnix(@"C:\project\src\File.txt", "/project/src/File.txt"), false },
        { @"relative\File.cs", false },
        { "", false }
    };

    [Theory]
    [InlineData(@"C:\project\src\File.txt", "sourceFile", "sourceFile must be a .cs file.")]
    [InlineData(@"C:\project\Pages\Counter.razor", "sourceFile", "sourceFile is a Razor/CSHTML file")]
    [InlineData(@"C:\project\Views\Index.cshtml", "targetFile", "targetFile is a Razor/CSHTML file")]
    public void GetCSharpFileRejectionReason_ReturnsExpectedMessage(string path, string parameterName, string expectedFragment)
    {
        var message = PathResolver.GetCSharpFileRejectionReason(path, parameterName);
        Assert.Contains(expectedFragment, message);
    }
}
