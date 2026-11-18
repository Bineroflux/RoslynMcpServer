using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_base_class selects members by name. It moved only the first overload of a
/// selected method into the base class but removed every overload from the derived class,
/// so the others were lost.
/// </summary>
public sealed class ExtractBaseClassOverloadTests
{
    [Fact]
    public async Task SelectingAnOverloadedMethod_MovesEveryOverload()
    {
        using var ws = TestWorkspace.Create(
            ("Printer.cs", """
                namespace App;

                public class Printer
                {
                    public string Print(int value) => "int";
                    public string Print(string value) => "string";
                    public void Other() { }
                }
                """),
            ("Use.cs", """
                namespace App;

                public static class Use
                {
                    public static string Both(Printer p) => p.Print(1) + p.Print("x");
                }
                """));

        var result = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams { SourceFile = ws.PathOf("Printer.cs"), TypeName = "Printer", BaseClassName = "PrinterBase", Members = ["Print"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Printer.cs");
        Assert.Contains("public string Print(int value)", text);
        Assert.Contains("public string Print(string value)", text);
        Assert.Contains("public void Other()", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
