using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Type names in generated members are as short as the usings in scope allow. They used
/// to be written fully qualified (<c>public event System.EventHandler Changed</c> next to
/// <c>using System;</c>). Names that cannot be shortened stay qualified: no using is added.
/// </summary>
public sealed class GeneratedTypeNameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementInterface_ShortensNamesOnlyAsFarAsUsingsAllow(bool explicitImplementation)
    {
        const string Builder = """
            using System;

            namespace App
            {
                public class Builder : Lib.IBuilder
                {
                }
            }
            """;
        using var ws = TestWorkspace.Create(("IBuilder.cs", """
            namespace Lib
            {
                public interface IBuilder
                {
                    event System.EventHandler Changed;
                    System.Text.StringBuilder Build(System.Collections.Generic.List<int> items);
                }
            }
            """), ("Builder.cs", Builder));

        var result = await new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
            new ImplementInterfaceParams
            {
                SourceFile = ws.PathOf("Builder.cs"),
                TypeName = "Builder",
                InterfaceName = "IBuilder",
                ExplicitImplementation = explicitImplementation
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Builder.cs");
        Assert.Empty(ws.GetDiskCompilationErrors());

        // `using System;` allows EventHandler; nothing allows the others, and no usings are added.
        Assert.StartsWith("using System;" + Environment.NewLine + Environment.NewLine + "namespace App", text.ReplaceLineEndings());
        Assert.Contains(explicitImplementation ? "event EventHandler Lib.IBuilder.Changed" : "public event EventHandler Changed", text);
        Assert.Contains("System.Text.StringBuilder", text);
        Assert.Contains("(System.Collections.Generic.List<int> items)", text);
        Assert.DoesNotContain("System.EventHandler", text);
    }
}
