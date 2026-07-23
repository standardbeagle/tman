namespace Tman;

public sealed class KdlNode
{
    public required string Name { get; init; }
    public List<KdlValue> Args { get; } = [];
    public List<KdlNode> Children { get; } = [];

    public string? Arg(int i) => i < Args.Count ? Args[i].AsString() : null;
    public KdlNode? Child(string name) => Children.Find(c => c.Name == name);

    public IEnumerable<KdlNode> All(string name)
    {
        foreach (var c in Children)
            if (c.Name == name) yield return c;
    }
}

public readonly struct KdlValue
{
    public object? Raw { get; }
    public KdlValue(object? raw) => Raw = raw;

    public string? AsString() => Raw switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        _ => Convert.ToString(Raw, System.Globalization.CultureInfo.InvariantCulture),
    };

    public long? AsLong() => Raw switch
    {
        long l => l,
        double d => (long)d,
        string s when long.TryParse(s, out var l) => l,
        _ => null,
    };

    public bool? AsBool() => Raw switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };
}

public static class Kdl
{
    public static List<KdlNode> Parse(string text)
    {
        var p = new Parser(text);
        return p.ParseNodes(topLevel: true);
    }

    sealed class Parser
    {
        readonly string _s;
        int _i;

        public Parser(string s) => _s = s;

        char Cur => _i < _s.Length ? _s[_i] : '\0';
        char Peek => _i + 1 < _s.Length ? _s[_i + 1] : '\0';

        public List<KdlNode> ParseNodes(bool topLevel)
        {
            var nodes = new List<KdlNode>();
            while (true)
            {
                SkipTrivia();
                if (_i >= _s.Length) break;
                if (Cur == '}')
                {
                    if (topLevel) throw Error("unexpected '}'");
                    _i++;
                    return nodes;
                }
                nodes.Add(ParseNode());
            }
            return nodes;
        }

        KdlNode ParseNode()
        {
            var name = ParseIdent();
            var node = new KdlNode { Name = name };
            while (true)
            {
                SkipInlineWsAndComments();
                if (_i >= _s.Length || Cur == '\n' || Cur == '\r' || Cur == ';' || Cur == '}')
                {
                    ConsumeTerminator();
                    return node;
                }
                if (Cur == '{')
                {
                    _i++;
                    node.Children.AddRange(ParseNodes(topLevel: false));
                    ConsumeTerminator();
                    return node;
                }
                if (Cur == '\\' && (Peek == '\n' || Peek == '\r'))
                {
                    _i++;
                    if (Cur == '\r') _i++;
                    if (Cur == '\n') _i++;
                    continue;
                }
                node.Args.Add(ParseValue());
            }
        }

        void ConsumeTerminator()
        {
            if (Cur == '\r') _i++;
            if (Cur == '\n' || Cur == ';') _i++;
        }

        KdlValue ParseValue()
        {
            SkipInlineWsAndComments();
            if (Cur == '"') return new KdlValue(ParseString());
            var tok = ParseBare();
            if (tok.Length == 0) throw Error("expected value");
            if (tok == "true") return new KdlValue(true);
            if (tok == "false") return new KdlValue(false);
            if (tok == "null") return new KdlValue(null);
            var clean = tok.Replace("_", "");
            if (long.TryParse(clean, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var l))
                return new KdlValue(l);
            if (double.TryParse(clean, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                return new KdlValue(d);
            return new KdlValue(tok);
        }

        string ParseString()
        {
            _i++;
            var sb = new System.Text.StringBuilder();
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && _i < _s.Length)
                {
                    var e = _s[_i++];
                    sb.Append(e switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r',
                        '"' => '"', '\\' => '\\',
                        _ => e,
                    });
                }
                else sb.Append(c);
            }
            throw Error("unterminated string");
        }

        string ParseIdent()
        {
            SkipInlineWsAndComments();
            if (Cur == '"') return ParseString();
            var tok = ParseBare();
            if (tok.Length == 0) throw Error("expected node name");
            return tok;
        }

        string ParseBare()
        {
            var start = _i;
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == ';' || c == '"')
                    break;
                if (c == '/' && Peek == '/') break;
                _i++;
            }
            return _s[start.._i];
        }

        void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                if (char.IsWhiteSpace(Cur)) { _i++; continue; }
                if (Cur == '/' && Peek == '/') { SkipLineComment(); continue; }
                if (Cur == '/' && Peek == '*') { SkipBlockComment(); continue; }
                break;
            }
        }

        void SkipInlineWsAndComments()
        {
            while (_i < _s.Length)
            {
                if (Cur == ' ' || Cur == '\t') { _i++; continue; }
                if (Cur == '/' && Peek == '/') { SkipLineComment(); continue; }
                if (Cur == '/' && Peek == '*') { SkipBlockComment(); continue; }
                break;
            }
        }

        void SkipLineComment()
        {
            while (_i < _s.Length && Cur != '\n') _i++;
        }

        void SkipBlockComment()
        {
            _i += 2;
            var depth = 1;
            while (_i < _s.Length && depth > 0)
            {
                if (Cur == '/' && Peek == '*') { depth++; _i += 2; }
                else if (Cur == '*' && Peek == '/') { depth--; _i += 2; }
                else _i++;
            }
        }

        Exception Error(string msg) =>
            new FormatException($"KDL parse error at offset {_i}: {msg}");
    }
}
