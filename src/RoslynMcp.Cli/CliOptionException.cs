namespace RoslynMcp.Cli;

/// <summary>
/// A tool option the CLI cannot pass on: one the tool does not take, or a value of the wrong
/// type. The message says what is wrong and what the tool takes; the CLI exits with code 2.
/// </summary>
public sealed class CliOptionException(string message) : Exception(message);
