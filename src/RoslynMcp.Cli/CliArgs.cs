using RoslynMcp.Core;

namespace RoslynMcp.Cli;

/// <summary>
/// Parsed command-line arguments.
/// </summary>
public sealed class ParsedArgs
{
    /// <summary>Path to .sln, .slnx, or .csproj file (null for global help).</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Tool name in kebab-case (null for global help).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool-specific options (kebab-case keys, string values).</summary>
    public Dictionary<string, string> Options { get; init; } = new();

    /// <summary>Whether global help was requested.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Whether tool-specific help was requested.</summary>
    public bool ShowToolHelp { get; init; }

    /// <summary>Whether the version was requested (<c>--version</c> or <c>-v</c> first).</summary>
    public bool ShowVersion { get; init; }

    /// <summary>Output format: "json" (default) or "text".</summary>
    public string Format { get; init; } = "json";

    /// <summary>Whether verbose output is enabled.</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Parses raw argv into structured CLI arguments.
/// </summary>
public static class CliArgs
{
    /// <summary>
    /// Parse command-line arguments into a structured result.
    /// </summary>
    /// <remarks>
    /// Syntax: roslyn-cli [--help]
    ///         roslyn-cli --version
    ///         roslyn-cli &lt;tool-name&gt; --help
    ///         roslyn-cli &lt;solution-path&gt; &lt;tool-name&gt; [--option value ...]
    ///         roslyn-cli &lt;tool-name&gt; [--option value ...]  (tools that need no solution)
    /// </remarks>
    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && IsHelpFlag(args[0])))
            return new ParsedArgs { ShowHelp = true };

        // Only as the first argument: later ones may be option values.
        if (ProductVersion.IsVersionFlag(args[0]))
            return new ParsedArgs { ShowVersion = true };

        // Detect: roslyn-cli <tool-name> --help
        if (args.Length == 2 && IsHelpFlag(args[1]) && !args[0].StartsWith("--"))
        {
            var name = args[0];
            // If it looks like a file path (has extension), it's not a tool name
            if (LooksLikeFilePath(name))
                return new ParsedArgs { ShowHelp = true };

            return new ParsedArgs { ToolName = name, ShowToolHelp = true };
        }

        // Detect: roslyn-cli <solution-path> <tool-name> [--opts]
        if (args.Length >= 2 && !args[0].StartsWith("--") && !args[1].StartsWith("--"))
        {
            var solutionPath = args[0];
            var toolName = args[1];
            var (options, format, verbose) = ParseOptions(args, startIndex: 2);

            // Check for --help among the options
            if (options.ContainsKey("help"))
            {
                options.Remove("help");
                return new ParsedArgs { ToolName = toolName, ShowToolHelp = true };
            }

            return new ParsedArgs
            {
                SolutionPath = solutionPath,
                ToolName = toolName,
                Options = options,
                Format = format,
                Verbose = verbose
            };
        }

        // Detect: roslyn-cli <tool-name> [--opts], for tools that need no solution (diagnose).
        if (!args[0].StartsWith("--") && !LooksLikeFilePath(args[0]))
        {
            var (options, format, verbose) = ParseOptions(args, startIndex: 1);
            if (options.Remove("help"))
                return new ParsedArgs { ToolName = args[0], ShowToolHelp = true };

            return new ParsedArgs { ToolName = args[0], Options = options, Format = format, Verbose = verbose };
        }

        // Fallback: show help
        return new ParsedArgs { ShowHelp = true };
    }

    /// <summary>
    /// The solution path resolved against the current directory, the way a command line
    /// names files (<c>roslyn-cli MySolution.sln ...</c>). The workspace only accepts
    /// absolute paths. Null stays null.
    /// </summary>
    public static string? ResolveSolutionPath(string? solutionPath) =>
        solutionPath == null ? null : ArgsToJsonConverter.ResolvePath(solutionPath);

    private static (Dictionary<string, string> options, string format, bool verbose) ParseOptions(
        string[] args, int startIndex)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var format = "json";
        var verbose = false;

        for (int i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--"))
            {
                // Positional arg after flags — skip (shouldn't happen in normal usage)
                continue;
            }

            var key = arg[2..]; // strip "--"

            // CLI-level flags
            if (key.Equals("verbose", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            // --format json|text picks the output format; any other value is a tool option
            // (generate-tostring's --format interpolated|stringbuilder).
            if (key.Equals("format", StringComparison.OrdinalIgnoreCase)
                && (i + 1 >= args.Length || args[i + 1].StartsWith("--") || IsOutputFormat(args[i + 1])))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    format = args[++i];
                }
                continue;
            }

            // Tool-specific options
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                // --key value
                options[key] = args[++i];
            }
            else
            {
                // --flag (boolean true)
                options[key] = "true";
            }
        }

        return (options, format, verbose);
    }

    private static bool IsHelpFlag(string arg) =>
        arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        arg == "-?" ||
        arg.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputFormat(string value) =>
        value.Equals("json", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("text", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeFilePath(string value) =>
        value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);
}
