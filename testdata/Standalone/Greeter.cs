// A standalone .NET 10 "file-based program" — runnable with `dotnet run Greeter.cs`,
// with no .csproj or .sln. Used to exercise the Roslyn MCP server's standalone-file
// support (find_references, go_to_definition, get_diagnostics, search_symbols, ...).

var greeter = new Greeter("world");
Console.WriteLine(greeter.Greet());
Console.WriteLine(greeter.Greet("again"));

var names = new[] { "Ada", "Linus", "Grace" };
foreach (var name in names)
{
    Console.WriteLine(new Greeter(name).Greet());
}

/// <summary>Builds friendly greetings for a configured subject.</summary>
internal sealed class Greeter(string subject)
{
    private readonly string _subject = subject;

    /// <summary>Greets the configured subject.</summary>
    public string Greet() => Greet("hello");

    /// <summary>Greets the configured subject with a custom salutation.</summary>
    public string Greet(string salutation) => $"{salutation}, {_subject}!";
}
