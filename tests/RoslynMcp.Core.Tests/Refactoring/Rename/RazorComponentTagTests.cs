using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Component tags written with their namespace (<c>&lt;Company.Old.Panel /&gt;</c>): the Razor
/// compiler binds them by that name, so when the namespace is renamed or the component moved, a
/// tag left as it was silently becomes an HTML element. rename_namespace and
/// move_type_to_namespace rewrite them (<see cref="RazorComponentTags"/>).
/// </summary>
public sealed class RazorComponentTagTests
{
    private const string Page = """
        <h1>Panels</h1>
        <Company.Old.Panel Title="a" />
        <Company.Old.Panel>
            <Company.Old.Parts.Header />
        </Company.Old.Panel>
        <Company.OldStuff.Panel />
        <Company.Old.PanelGroup />
        <p title="<Company.Old.Panel />">text</p>
        @code {
            private string markup = "<Company.Old.Panel />";
            private bool Less(int a, int b) => a < b;
        }
        """;

    [Fact]
    public void Rewrite_MatchesWholeSegmentsAfterTheTagStart_OutsideCodeBlocksAndStrings()
    {
        var text = SourceText.From(Page.ReplaceLineEndings("\n"));

        var exact = text.WithChanges(RazorComponentTags.Rewrite(text, "Company.Old.Panel", "Company.New.Panel", prefix: false)).ToString();
        var namespaced = text.WithChanges(RazorComponentTags.Rewrite(text, "Company.Old", "Company.New", prefix: true)).ToString();

        Assert.Equal(Page.ReplaceLineEndings("\n")
            .Replace("<Company.Old.Panel Title", "<Company.New.Panel Title", StringComparison.Ordinal)
            .Replace("<Company.Old.Panel>", "<Company.New.Panel>", StringComparison.Ordinal)
            .Replace("</Company.Old.Panel>", "</Company.New.Panel>", StringComparison.Ordinal), exact);
        Assert.Equal(Page.ReplaceLineEndings("\n")
            .Replace("<Company.Old.Panel Title", "<Company.New.Panel Title", StringComparison.Ordinal)
            .Replace("<Company.Old.Panel>", "<Company.New.Panel>", StringComparison.Ordinal)
            .Replace("</Company.Old.Panel>", "</Company.New.Panel>", StringComparison.Ordinal)
            .Replace("<Company.Old.Parts.Header", "<Company.New.Parts.Header", StringComparison.Ordinal)
            .Replace("<Company.Old.PanelGroup", "<Company.New.PanelGroup", StringComparison.Ordinal), namespaced);
        Assert.True(RazorComponentTags.HasTag(SourceText.From("<div>\n<Panel />\n</div>"), "Panel"));
        Assert.False(RazorComponentTags.HasTag(SourceText.From("<Company.Old.Panel />\n<PanelGroup />"), "Panel"));
    }

    [Fact]
    public async Task RenameNamespace_RewritesComponentTagsWrittenWithIt()
    {
        using var ws = TestWorkspace.Create(
            [("Panel.cs", "namespace Company.Old\n{\n    public class Panel { }\n}\n\nnamespace Company.Old.Parts\n{\n    public class Header { }\n}\n")],
            [],
            additionalFiles: [("Pages/Index.razor", "<Company.Old.Panel>\n    <Company.Old.Parts.Header />\n</Company.Old.Panel>\n<Company.OldStuff.Panel />\n")]);

        var result = await new RenameNamespaceOperation(ws.Context).ExecuteAsync(
            new RenameNamespaceParams { OldNamespace = "Company.Old", NewNamespace = "Company.New" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(
            "<Company.New.Panel>\n    <Company.New.Parts.Header />\n</Company.New.Panel>\n<Company.OldStuff.Panel />\n",
            ws.ReadFile("Pages/Index.razor"));
        Assert.DoesNotContain(result.Warnings ?? [], w => w.Contains("Index.razor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveTypeToNamespace_RewritesTagsWrittenWithTheComponentsOldName_OnlyForAComponent()
    {
        const string view = "<Probe.Panel>\n    <Probe.Note />\n</Probe.Panel>\n<Probe.PanelGroup />\n";
        using var ws = TestWorkspace.Create(
            [
                ("Components.cs", "namespace Microsoft.AspNetCore.Components;\n\npublic interface IComponent { }\n"),
                ("Panel.cs", "namespace Probe;\n\npublic class Panel : Microsoft.AspNetCore.Components.IComponent { }\n"),
                ("Note.cs", "namespace Probe;\n\npublic class Note { }\n")
            ],
            [],
            additionalFiles: [("Widget.razor", view)]);

        var panel = await MoveAsync(ws, "Panel", "Probe.Parts");
        var note = await MoveAsync(ws, "Note", "Probe.Notes");

        Assert.True(panel.Success, panel.Error?.Message);
        Assert.True(note.Success, note.Error?.Message);
        // Note is no component: a tag of that name is markup, which stays.
        Assert.Equal("<Probe.Parts.Panel>\n    <Probe.Note />\n</Probe.Parts.Panel>\n<Probe.PanelGroup />\n", ws.ReadFile("Widget.razor"));
        Assert.Contains(ws.PathOf("Widget.razor"), panel.Changes!.FilesModified);
    }

    private static Task<RefactoringResult> MoveAsync(TestWorkspace ws, string type, string target) =>
        new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf(type + ".cs"), SymbolName = type, TargetNamespace = target },
            TestContext.Current.CancellationToken);
}
