using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Component tags in Razor files written with a namespace (<c>&lt;Company.Old.Panel /&gt;</c>,
/// <c>&lt;/Company.Old.Panel&gt;</c>), which the Razor compiler binds by that name: when the
/// namespace or the component moves, nothing else updates them and a tag that no longer binds
/// silently becomes an HTML element. Matched as text like <see cref="RazorNamespaceDirectives"/>
/// matches names: whole dotted segments right after <c>&lt;</c> or <c>&lt;/</c>, not inside
/// <c>@code</c> / <c>@functions</c> blocks (C#) or string literals.
/// </summary>
internal static partial class RazorComponentTags
{
    [GeneratedRegex(@"@(?:code|functions)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlockStart();

    /// <summary>
    /// The changes that write <paramref name="newName"/> for <paramref name="oldName"/> in the
    /// tags of <paramref name="text"/>: tags named exactly <paramref name="oldName"/>, or, with
    /// <paramref name="prefix"/>, tags whose name starts with it followed by a dot and a name
    /// (a namespace and what is in it).
    /// </summary>
    public static List<TextChange> Rewrite(SourceText text, string oldName, string newName, bool prefix)
    {
        var content = text.ToString();
        var changes = new List<TextChange>();
        if (content.IndexOf(oldName, StringComparison.Ordinal) < 0)
            return changes;

        var tag = new Regex(
            @"(?<=</?)" + Regex.Escape(oldName) + (prefix ? @"(?=\.[A-Za-z_])" : @"(?![\w.])"),
            RegexOptions.CultureInvariant);
        var blocks = CodeBlocks(content);
        foreach (Match match in tag.Matches(content))
        {
            if (blocks.Any(block => block.Contains(match.Index)))
                continue;

            var line = text.Lines.GetLineFromPosition(match.Index);
            if (!RazorNamespaceDirectives.InString(line.ToString(), match.Index - line.Start))
                changes.Add(new TextChange(new TextSpan(match.Index, match.Length), newName));
        }

        return changes;
    }

    /// <summary>Whether <paramref name="text"/> has a tag named exactly <paramref name="name"/> (as <see cref="Rewrite"/> matches it).</summary>
    public static bool HasTag(SourceText text, string name) => Rewrite(text, name, name, prefix: false).Count > 0;

    /// <summary>The spans of the <c>@code</c> and <c>@functions</c> blocks, braces matched outside string and character literals.</summary>
    private static List<TextSpan> CodeBlocks(string content)
    {
        var blocks = new List<TextSpan>();
        foreach (Match start in CodeBlockStart().Matches(content))
        {
            var depth = 0;
            char? quote = null;
            var end = content.Length;
            for (var i = start.Index + start.Length - 1; i < content.Length; i++)
            {
                var c = content[i];
                if (quote != null)
                {
                    if (c == '\\')
                        i++;
                    else if (c == quote)
                        quote = null;
                    continue;
                }

                if (c is '"' or '\'')
                    quote = c;
                else if (c == '{')
                    depth++;
                else if (c == '}' && --depth == 0)
                {
                    end = i + 1;
                    break;
                }
            }

            blocks.Add(TextSpan.FromBounds(start.Index, end));
        }

        return blocks;
    }
}
