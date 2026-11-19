using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// move_type_to_file and move_type_to_namespace report a cancellation that arrives while they
/// run (not only while they wait for the writer section) as every refactoring does: Timeout,
/// with nothing written. They used to wrap it as RoslynError "Unexpected error".
/// </summary>
public sealed class MoveTypeCancellationTests
{
    private const string Models =
        "namespace App;\n\npublic class Customer\n{\n}\n\npublic class Order\n{\n    public Customer? Buyer { get; set; }\n}\n";

    [Fact]
    public async Task MoveTypeToFile_CancelledWhileRunning_ReportsTimeout_AndWritesNothing()
    {
        using var ws = TestWorkspace.Create(("Models.cs", Models));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await CancelOnFirstReadAsync(ws, "Models.cs", cts);

        var thrown = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
                new MoveTypeToFileParams
                {
                    SourceFile = ws.PathOf("Models.cs"),
                    SymbolName = "Customer",
                    TargetFile = ws.PathOf("Customer.cs"),
                },
                cts.Token));

        Assert.True(cts.IsCancellationRequested, "The operation must have started reading the solution.");
        Assert.Equal(ErrorCodes.Timeout, thrown.ErrorCode);
        Assert.Equal(Models, ws.ReadFile("Models.cs"));
        Assert.False(File.Exists(ws.PathOf("Customer.cs")));
    }

    [Fact]
    public async Task MoveTypeToNamespace_CancelledWhileRunning_ReportsTimeout_AndWritesNothing()
    {
        using var ws = TestWorkspace.Create(("Models.cs", Models));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await CancelOnFirstReadAsync(ws, "Models.cs", cts);

        var thrown = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
                new MoveTypeToNamespaceParams
                {
                    SourceFile = ws.PathOf("Models.cs"),
                    SymbolName = "Customer",
                    TargetNamespace = "App.Customers",
                },
                cts.Token));

        Assert.True(cts.IsCancellationRequested, "The operation must have started reading the solution.");
        Assert.Equal(ErrorCodes.Timeout, thrown.ErrorCode);
        Assert.Equal(Models, ws.ReadFile("Models.cs"));
    }

    /// <summary>
    /// Makes the first read of <paramref name="file"/>'s text cancel <paramref name="cts"/>: the
    /// refactoring is cancelled once it runs inside its writer section, at the point it starts
    /// reading the solution.
    /// </summary>
    private static Task CancelOnFirstReadAsync(TestWorkspace ws, string file, CancellationTokenSource cts)
    {
        var id = ws.Context.GetDocumentByPath(ws.PathOf(file))!.Id;
        var text = SourceText.From(ws.ReadFile(file));
        return ws.Context.UpdateSolutionInWriterAsync(
            s => s.WithDocumentTextLoader(id, new CancellingLoader(text, cts), PreservationMode.PreserveValue),
            TestContext.Current.CancellationToken);
    }

    private sealed class CancellingLoader(SourceText text, CancellationTokenSource cts) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(
            LoadTextOptions options, CancellationToken cancellationToken)
        {
            cts.Cancel();
            return Task.FromResult(TextAndVersion.Create(text, VersionStamp.Create()));
        }
    }
}
