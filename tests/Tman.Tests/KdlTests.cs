using Tman;
using Xunit;

namespace Tman.Tests;

public class KdlTests
{
    [Fact]
    public void ParsesNestedNodesWithArgs()
    {
        var nodes = Kdl.Parse("""
            alias "test" {
                command "npm"
                args "run" "test"
            }
            """);
        var alias = Assert.Single(nodes);
        Assert.Equal("alias", alias.Name);
        Assert.Equal("test", alias.Arg(0));
        Assert.Equal("npm", alias.Child("command")?.Arg(0));
        Assert.Equal(new[] { "run", "test" }, alias.Child("args")?.Args.Select(a => a.AsString()));
    }

    [Fact]
    public void ParsesScalarTypes()
    {
        var nodes = Kdl.Parse("n 42 1.5 true false null bare \"quoted\"");
        var n = Assert.Single(nodes);
        Assert.Equal(42L, n.Args[0].AsLong());
        Assert.Equal("1.5", n.Args[1].AsString());
        Assert.Equal(true, n.Args[2].AsBool());
        Assert.Equal(false, n.Args[3].AsBool());
        Assert.Null(n.Args[4].AsString());
        Assert.Equal("bare", n.Args[5].AsString());
        Assert.Equal("quoted", n.Args[6].AsString());
    }

    [Fact]
    public void SkipsLineAndBlockComments()
    {
        var nodes = Kdl.Parse("""
            // line comment
            a 1 // trailing
            /* block
               multi */
            b 2
            """);
        Assert.Equal(new[] { "a", "b" }, nodes.Select(n => n.Name));
    }

    [Fact]
    public void HandlesStringEscapes()
    {
        var nodes = Kdl.Parse("n \"a\\\"b\\\\c\\nd\"");
        Assert.Equal("a\"b\\c\nd", nodes[0].Arg(0));
    }

    [Fact]
    public void HandlesLineContinuation()
    {
        var nodes = Kdl.Parse("n 1 \\\n    2");
        Assert.Equal(2, nodes[0].Args.Count);
    }

    [Fact]
    public void SemicolonTerminatesNodes()
    {
        var nodes = Kdl.Parse("a 1; b 2");
        Assert.Equal(new[] { "a", "b" }, nodes.Select(n => n.Name));
    }

    [Fact]
    public void UnexpectedCloseBrace_Throws()
    {
        Assert.Throws<FormatException>(() => Kdl.Parse("}"));
    }

    [Fact]
    public void UnterminatedString_Throws()
    {
        Assert.Throws<FormatException>(() => Kdl.Parse("n \"oops"));
    }
}
