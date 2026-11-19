using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Generate;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Layout of the code that generating operations write. Generated members, statements
/// and types used to land at column 0 (members with doubled blank lines), because they
/// were built with <c>NormalizeWhitespace</c> and inserted with hard-coded line breaks.
/// They are now formatted in place, and only they are: the deliberately odd
/// <see cref="OddLine"/> must survive every operation that does not move it byte for byte.
/// </summary>
public sealed class GeneratedCodeLayoutTests
{
    private const string FileName = "Code.cs";

    private const string OddLine = "        public int  Odd( )  =>  1;";

    private const string PersonSource = """
        using System;
        using System.Collections.Generic;

        namespace App
        {
            public interface INamed
            {
                string Name { get; }
            }

            public class Person
            {
                private string _name = "";
                public int  Odd( )  =>  1;

                public virtual string Describe(string prefix)
                {
                    Console.WriteLine(prefix);
                    var text = prefix + _name.Length * 42;
                    return text;
                }
            }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImplementInterface_IndentsMembers()
    {
        var text = await RunAsync(
            ws => new ImplementInterfaceOperation(ws.Context).ExecuteAsync(
                new ImplementInterfaceParams
                {
                    SourceFile = ws.PathOf(FileName),
                    TypeName = "Person",
                    InterfaceName = "INamed",
                    ThrowNotImplemented = false
                },
                Ct),
            PersonSource.Replace("class Person", "class Person : INamed"));

        AssertLayout("""
                        return text;
                    }

                    public string Name
                    {
                        get
                        {
                            return null;
                        }
                    }
                }
            }
            """, text);
    }

    [Fact]
    public async Task GenerateOverrides_IndentsMembers()
    {
        var text = await RunAsync(
            ws => new GenerateOverridesOperation(ws.Context).ExecuteAsync(
                new GenerateOverridesParams
                {
                    SourceFile = ws.PathOf("Employee.cs"),
                    TypeName = "Employee",
                    Members = ["Describe"]
                },
                Ct),
            PersonSource,
            ("Employee.cs", """
                namespace App
                {
                    public class Employee : Person
                    {
                    }
                }
                """),
            readFile: "Employee.cs");

        AssertLayout("""
                public class Employee : Person
                {
                    public override string Describe(string prefix)
                    {
                        return base.Describe(prefix);
                    }
                }
            """, text, checkOddLine: false);
    }

    [Fact]
    public async Task GenerateConstructor_IndentsConstructorBetweenMembers()
    {
        var text = await RunAsync(ws => new GenerateConstructorOperation(ws.Context).ExecuteAsync(
            new GenerateConstructorParams { SourceFile = ws.PathOf(FileName), TypeName = "Person" },
            Ct));

        AssertLayout("""
                    private string _name = "";

                    public Person(string name)
                    {
                        _name = name;
                    }

                    public int  Odd( )  =>  1;
            """, text);
    }

    [Fact]
    public async Task GenerateEqualsHashCode_IndentsMembers()
    {
        var text = await RunAsync(ws => new GenerateEqualsHashCodeOperation(ws.Context).ExecuteAsync(
            new GenerateEqualsHashCodeParams { SourceFile = ws.PathOf(FileName), TypeName = "Person" },
            Ct));

        AssertLayout("""
                        return text;
                    }

                    public override bool Equals(object? obj)
                    {
                        return obj is Person other && EqualityComparer<string>.Default.Equals(_name, other._name);
                    }

                    public override int GetHashCode()
                    {
                        return HashCode.Combine(_name);
                    }
                }
            """, text);
    }

    [Fact]
    public async Task GenerateToString_IndentsMember()
    {
        var text = await RunAsync(ws => new GenerateToStringOperation(ws.Context).ExecuteAsync(
            new GenerateToStringParams { SourceFile = ws.PathOf(FileName), TypeName = "Person" },
            Ct));

        AssertLayout("""
                        return text;
                    }

                    public override string ToString()
                    {
                        return $"Person {{ _name = {_name} }}";
                    }
                }
            """, text);
    }

    [Fact]
    public async Task AddNullChecks_IndentsStatements()
    {
        var text = await RunAsync(ws => new AddNullChecksOperation(ws.Context).ExecuteAsync(
            new AddNullChecksParams { SourceFile = ws.PathOf(FileName), MethodName = "Describe" },
            Ct));

        AssertLayout("""
                    public virtual string Describe(string prefix)
                    {
                        ArgumentNullException.ThrowIfNull(prefix);
                        Console.WriteLine(prefix);
            """, text);
    }

    [Fact]
    public async Task EncapsulateField_IndentsPropertyAndKeepsFieldLayout()
    {
        var text = await RunAsync(
            ws => new EncapsulateFieldOperation(ws.Context).ExecuteAsync(
                new EncapsulateFieldParams { SourceFile = ws.PathOf(FileName), FieldName = "_count" },
                Ct),
            """
            namespace App
            {
                public class Counter
                {
                    protected internal int _count;
                    public int  Odd( )  =>  1;
                }
            }
            """);

        // The new `private` replaces the old accessibility keywords in place instead of
        // being glued, without indentation, onto the type (`privateint _count;`).
        AssertLayout("""
                public class Counter
                {
                    private int _count;

                    public int Count
                    {
                        get
                        {
                            return _count;
                        }

                        set
                        {
                            _count = value;
                        }
                    }

                    public int  Odd( )  =>  1;
                }
            """, text);
    }

    [Fact]
    public async Task ExtractInterface_IndentsInterfaceAndBaseList()
    {
        var text = await RunAsync(ws => new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams { SourceFile = ws.PathOf(FileName), TypeName = "Person", InterfaceName = "IPerson" },
            Ct));

        AssertLayout("""
                public interface IPerson
                {
                    int Odd();
                    string Describe(string prefix);
                }

                public class Person : IPerson
                {
                    private string _name = "";
            """, text);
    }

    [Fact]
    public async Task ExtractBaseClass_IndentsClassAndBaseList()
    {
        var text = await RunAsync(ws => new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams
            {
                SourceFile = ws.PathOf(FileName),
                TypeName = "Person",
                BaseClassName = "PersonBase",
                Members = ["Odd"]
            },
            Ct));

        AssertLayout("""
                public class PersonBase
                {
                    public int Odd() => 1;
                }

                public class Person : PersonBase
                {
                    private string _name = "";

                    public virtual string Describe(string prefix)
            """, text, checkOddLine: false);
    }

    [Fact]
    public async Task ExtractConstant_IndentsField()
    {
        var (line, start, end) = Locate("42");
        var text = await RunAsync(ws => new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                ConstantName = "Factor"
            },
            Ct));

        AssertLayout("""
                public class Person
                {
                    private const int Factor = 42;
                    private string _name = "";
            """, text);
    }

    [Fact]
    public async Task ExtractVariable_IndentsDeclaration()
    {
        var (line, start, end) = Locate("_name.Length * 42");
        var text = await RunAsync(ws => new ExtractVariableOperation(ws.Context).ExecuteAsync(
            new ExtractVariableParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                VariableName = "size"
            },
            Ct));

        AssertLayout("""
                        Console.WriteLine(prefix);
                        var size = _name.Length * 42;
                        var text = prefix + size;
            """, text);
    }

    [Fact]
    public async Task ExtractMethod_IndentsMethod()
    {
        var (line, start, end) = Locate("Console.WriteLine(prefix);");
        var text = await RunAsync(ws => new ExtractMethodOperation(ws.Context).ExecuteAsync(
            new ExtractMethodParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = start,
                EndLine = line,
                EndColumn = end,
                MethodName = "Log"
            },
            Ct));

        AssertLayout("""
                        return text;
                    }

                    private static void Log(string prefix)
                    {
                        Console.WriteLine(prefix);
                    }
                }
            """, text);
    }

    private static async Task<string> RunAsync(
        Func<TestWorkspace, Task<RefactoringResult>> operation,
        string source = PersonSource,
        (string Name, string Text)? otherFile = null,
        string readFile = FileName)
    {
        var files = new List<(string, string)> { (FileName, source) };
        if (otherFile is { } other)
        {
            files.Add(other);
        }

        using var ws = TestWorkspace.Create(files, []);
        var result = await operation(ws);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(ws.GetDiskCompilationErrors());
        return ws.ReadFile(readFile).ReplaceLineEndings("\n");
    }

    private static void AssertLayout(string expected, string text, bool checkOddLine = true)
    {
        Assert.Contains(expected.ReplaceLineEndings("\n"), text);
        if (checkOddLine)
        {
            Assert.Contains(OddLine + "\n", text);
        }
    }

    private static (int Line, int Start, int End) Locate(string token)
    {
        var lines = PersonSource.ReplaceLineEndings("\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(token, StringComparison.Ordinal));
        var column = lines[index].IndexOf(token, StringComparison.Ordinal) + 1;
        return (index + 1, column, column + token.Length);
    }
}
