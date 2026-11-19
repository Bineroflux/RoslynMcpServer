using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Tests;

/// <summary>
/// A source generator standing in for Razor: for each additional file <c>Name.razor</c> it
/// writes a class <c>NameView</c> whose <c>Render</c> method evaluates each line <c>@expr</c> of
/// the file as <c>_ = expr;</c>, under a <c>#line</c> directive that maps the expression back to
/// its place in the <c>.razor</c> file.
/// </summary>
/// <remarks>
/// A test helper loaded from the test assembly itself; the rules for shipped compiler
/// extensions (netstandard2.0, no Workspaces reference) do not apply.
/// </remarks>
#pragma warning disable RS1036, RS1038, RS1041
[Generator]
public sealed class ViewGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var views = context.AdditionalTextsProvider.Where(text => text.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        context.RegisterSourceOutput(views, (output, text) =>
        {
            var name = Path.GetFileNameWithoutExtension(text.Path);
            var source = new StringBuilder($"namespace App;\n\npublic static class {name}View\n{{\n    public static void Render()\n    {{\n");
            var lines = text.GetText(output.CancellationToken)!.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].ToString();
                if (!line.StartsWith('@'))
                    continue;

                var expression = line[1..];
                source.Append($"#line ({i + 1}, 2) - ({i + 1}, {1 + expression.Length}) 12 \"{text.Path}\"\n");
                source.Append($"        _ = {expression};\n");
                source.Append("#line default\n");
            }

            source.Append("    }\n}\n");
            output.AddSource($"{name}.razor.g.cs", source.ToString());
        });
    }

    /// <summary>The generator as the workspace takes it.</summary>
    public static ISourceGenerator Instance { get; } = new ViewGenerator().AsSourceGenerator();
}
#pragma warning restore RS1036, RS1038, RS1041
