using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KasaMonitor.Core.Mqtt;

/// <summary>
/// The subset of Home Assistant's Jinja <c>value_template</c> KasaMonitor accepts (0.11, audit §6): one expression
/// <c>{{ … }}</c> over <c>value</c> and <c>value_json</c> (<c>.a.b</c>, <c>['a']</c>, <c>[0]</c>), number and string
/// literals, the filters <c>float</c>, <c>int</c>, <c>round(n)</c>, <c>default(x)</c>, <c>lower</c>, <c>upper</c> and
/// arithmetic <c>* / + -</c> with parentheses. Anything else (<c>{% … %}</c>, other filters, tests, functions, text around
/// the expression) is refused at parse time: the entity is marked „șablon neacceptat” and the raw payload is never used
/// in its place. Rendering follows Jinja's text: a JSON <c>true</c> renders <c>True</c>, an undefined attribute renders empty.
/// </summary>
public sealed class ValueTemplate
{
    private readonly Node _root;

    private ValueTemplate(string source, Node root)
    {
        Source = source;
        _root = root;
    }

    public string Source { get; }

    /// <summary>R1 (round 2): the longest template accepted (a discovery payload is untrusted input).</summary>
    public const int MaxLength = 512;
    /// <summary>R1: the deepest nesting of parentheses / unary minus (the parser and the evaluation are recursive).</summary>
    public const int MaxDepth = 32;
    /// <summary>R1: the most nodes of one expression (left-deep chains are evaluated recursively too).</summary>
    public const int MaxNodes = 128;

    /// <summary>
    /// Parses a template; false with the reason when it is outside the accepted subset. Never throws and never recurses
    /// without a bound: a hostile retained discovery payload can only mark its entity „șablon neacceptat”.
    /// </summary>
    public static bool TryParse(string? template, out ValueTemplate? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(template)) { error = "șablon gol"; return false; }
        if (template.Length > MaxLength) { error = $"șablon prea lung (peste {MaxLength} caractere)"; return false; }
        var t = template.Trim();
        if (t.Contains("{%", StringComparison.Ordinal) || t.Contains("{#", StringComparison.Ordinal)) { error = "instrucțiuni {% … %}"; return false; }
        if (!t.StartsWith("{{", StringComparison.Ordinal) || !t.EndsWith("}}", StringComparison.Ordinal)) { error = "text în afara {{ … }}"; return false; }
        var body = t[2..^2];
        if (body.Contains("{{", StringComparison.Ordinal) || body.Contains("}}", StringComparison.Ordinal)) { error = "mai multe expresii"; return false; }
        try
        {
            var p = new Parser(body);
            var node = p.ParseExpression();
            p.ExpectEnd();
            result = new ValueTemplate(template, node);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Renders the template for one payload; null when the evaluation fails (e.g. <c>float</c> of a text without a default).</summary>
    public string? Render(string payload) => TryRender(payload, out var text, out _) ? text : null;

    /// <summary>Renders the template; false with the reason when the evaluation fails (counted per entity, never stored).</summary>
    public bool TryRender(string payload, out string? text, out string? error)
    {
        text = null;
        error = null;
        JsonNode? json = null;
        var jsonParsed = false;
        try
        {
            var v = _root.Eval(new Context(payload, () =>
            {
                if (!jsonParsed)
                {
                    jsonParsed = true;
                    try { json = JsonNode.Parse(payload); } catch (JsonException) { json = null; }
                }
                return json;
            }));
            text = ToText(v);
            return true;
        }
        catch (EvalException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or FormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---- values: null (undefined), Json (a node, null = JSON null), double, long, string, bool ----

    private sealed record Undefined
    {
        public static readonly Undefined Value = new();
    }

    private sealed class EvalException(string message) : Exception(message);

    private sealed record Context(string Payload, Func<JsonNode?> Json);

    private static string ToText(object? v) => v switch
    {
        null or Undefined => "",
        bool b => b ? "True" : "False",
        double d => FormatDouble(d),
        long l => l.ToString(CultureInfo.InvariantCulture),
        string s => s,
        JsonNode n => JsonText(n),
        _ => v.ToString() ?? "",
    };

    private static string FormatDouble(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        // Python's float repr: integral values keep one decimal („21.0”).
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
    }

    private static string JsonText(JsonNode n) => n is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.String => v.GetValue<string>(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Number => v.ToJsonString(),
        _ => "None",
    } : n.ToJsonString();

    /// <summary>A value as a scalar: JSON nodes unwrapped to string / number / bool; JSON null → null.</summary>
    private static object? Scalar(object? v)
    {
        if (v is not JsonNode n) return v;
        if (n is not JsonValue jv) return n;
        return jv.GetValueKind() switch
        {
            JsonValueKind.String => jv.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => jv.TryGetValue<long>(out var l) ? (object)l : jv.GetValue<double>(),
            _ => null,
        };
    }

    private static double ToDouble(object? v, string op)
    {
        return Scalar(v) switch
        {
            double d => d,
            long l => l,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new EvalException($"{op}: nu e număr"),
        };
    }

    private static bool IsNumber(object? v) => Scalar(v) is double or long;

    // ---- AST ----

    private abstract class Node
    {
        public abstract object? Eval(Context c);
    }

    private sealed class ValueNode : Node
    {
        public override object? Eval(Context c) => c.Payload;
    }

    private sealed class ValueJsonNode : Node
    {
        public override object? Eval(Context c) => c.Json() is { } n ? n : Undefined.Value;
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Eval(Context c) => value;
    }

    private sealed class Member(Node target, object key) : Node
    {
        public override object? Eval(Context c)
        {
            var t = target.Eval(c);
            return t switch
            {
                JsonObject o when key is string s => o.TryGetPropertyValue(s, out var n) ? n ?? (object?)null : Undefined.Value,
                JsonArray a when key is long i => i >= 0 && i < a.Count ? a[(int)i] ?? (object?)null : Undefined.Value,
                JsonArray a when key is long i2 && i2 < 0 && -i2 <= a.Count => a[a.Count + (int)i2] ?? (object?)null,
                _ => Undefined.Value,
            };
        }
    }

    private sealed class Unary(Node operand) : Node
    {
        public override object? Eval(Context c)
        {
            var v = operand.Eval(c);
            return Scalar(v) is long l ? (object)(-l) : -ToDouble(v, "-");
        }
    }

    private sealed class Binary(char op, Node left, Node right) : Node
    {
        public override object? Eval(Context c)
        {
            var a = left.Eval(c);
            var b = right.Eval(c);
            if (op == '+' && Scalar(a) is string sa && Scalar(b) is string sb) return sa + sb;
            if (op != '/' && Scalar(a) is long la && Scalar(b) is long lb)
                return op switch { '+' => la + lb, '-' => la - lb, _ => la * lb };
            if (!IsNumber(a) || !IsNumber(b)) throw new EvalException($"operația {op} pe o valoare care nu e număr");
            double x = ToDouble(a, op.ToString()), y = ToDouble(b, op.ToString());
            return op switch
            {
                '+' => x + y,
                '-' => x - y,
                '*' => x * y,
                _ => y == 0 ? throw new EvalException("împărțire la zero") : x / y,
            };
        }
    }

    private sealed class Filter(Node target, string name, IReadOnlyList<object?> args) : Node
    {
        public override object? Eval(Context c)
        {
            var v = target.Eval(c);
            var s = Scalar(v);
            switch (name)
            {
                case "float":
                    if (s is double or long) return ToDouble(s, name);
                    if (s is bool b) return b ? 1.0 : 0.0;
                    if (s is string str && double.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    return args.Count > 0 ? args[0] : throw new EvalException("float: valoarea nu e număr");
                case "int":
                    if (s is long l) return l;
                    if (s is double dd) return (long)Math.Truncate(dd);
                    if (s is bool bb) return bb ? 1L : 0L;
                    if (s is string si && double.TryParse(si.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var di)) return (long)Math.Truncate(di);
                    return args.Count > 0 ? args[0] : throw new EvalException("int: valoarea nu e număr");
                case "round":
                    var digits = args.Count > 0 && args[0] is long n ? (int)n : 0;
                    // Jinja's „common” method is Python's round(): half to even.
                    return Math.Round(ToDouble(s, name), digits, MidpointRounding.ToEven);
                case "default":
                    return v is Undefined ? (args.Count > 0 ? args[0] : "") : v;
                case "lower":
                    return ToText(v).ToLowerInvariant();
                case "upper":
                    return ToText(v).ToUpperInvariant();
                default:
                    throw new EvalException("filtru necunoscut " + name);
            }
        }
    }

    // ---- parser (recursive descent) ----

    private sealed class Parser
    {
        private static readonly HashSet<string> Filters = new(StringComparer.Ordinal) { "float", "int", "round", "default", "lower", "upper" };
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        private void Ws()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private bool Peek(char c)
        {
            Ws();
            return _i < _s.Length && _s[_i] == c;
        }

        private bool Accept(char c)
        {
            if (!Peek(c)) return false;
            _i++;
            return true;
        }

        private void Expect(char c)
        {
            if (!Accept(c)) throw new FormatException($"se aștepta „{c}” la poziția {_i}");
        }

        public void ExpectEnd()
        {
            Ws();
            if (_i < _s.Length) throw new FormatException($"expresie neacceptată de la „{_s[_i..].Trim()}”");
        }

        public Node ParseExpression() => Additive();

        private Node Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                if (Accept('+')) left = Count(new Binary('+', left, Multiplicative()));
                else if (Accept('-')) left = Count(new Binary('-', left, Multiplicative()));
                else return left;
            }
        }

        private Node Multiplicative()
        {
            var left = Unary();
            while (true)
            {
                Ws();
                // „**” and „//” are outside the subset.
                if (_i + 1 < _s.Length && (_s[_i..].StartsWith("**", StringComparison.Ordinal) || _s[_i..].StartsWith("//", StringComparison.Ordinal)))
                    throw new FormatException("operatorii ** și // nu sunt acceptați");
                if (Accept('*')) left = Count(new Binary('*', left, Unary()));
                else if (Accept('/')) left = Count(new Binary('/', left, Unary()));
                else return left;
            }
        }

        private int _depth;
        private int _nodes;

        /// <summary>Counts a node; past <see cref="MaxNodes"/> the expression is refused.</summary>
        private T Count<T>(T node) where T : Node
        {
            if (++_nodes > MaxNodes) throw new FormatException("expresie prea mare");
            return node;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new FormatException("expresie prea adâncă");
        }

        private Node Unary()
        {
            if (Accept('-'))
            {
                Enter();
                var operand = Unary();
                _depth--;
                return Count(new Unary(operand));
            }
            return Postfix();
        }

        private Node Postfix()
        {
            var node = Primary();
            while (true)
            {
                if (Accept('.'))
                {
                    node = Count(new Member(node, Identifier()));
                }
                else if (Accept('['))
                {
                    Ws();
                    object key = Peek('\'') || Peek('"') ? StringLiteral() : Integer();
                    Expect(']');
                    node = Count(new Member(node, key));
                }
                else if (Accept('|'))
                {
                    var name = Identifier();
                    if (!Filters.Contains(name)) throw new FormatException($"filtrul „{name}” nu e acceptat");
                    var args = new List<object?>();
                    if (Accept('('))
                    {
                        if (!Accept(')'))
                        {
                            do args.Add(LiteralValue()); while (Accept(','));
                            Expect(')');
                        }
                    }
                    if (name == "round" && (args.Count > 1 || args.Count == 1 && args[0] is not long))
                        throw new FormatException("round acceptă doar numărul de zecimale");
                    if (name is "lower" or "upper" && args.Count > 0) throw new FormatException($"{name} fără argumente");
                    if (args.Count > 1) throw new FormatException($"{name}: prea multe argumente");
                    node = Count(new Filter(node, name, args));
                }
                else return node;
            }
        }

        private Node Primary()
        {
            Ws();
            if (Accept('('))
            {
                Enter();
                var e = Additive();
                Expect(')');
                _depth--;
                return e;
            }
            if (Peek('\'') || Peek('"')) return new Literal(StringLiteral());
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) return new Literal(Number());
            var id = Identifier();
            return id switch
            {
                "value" => new ValueNode(),
                "value_json" => new ValueJsonNode(),
                "true" or "True" => new Literal(true),
                "false" or "False" => new Literal(false),
                "none" or "None" => new Literal(null),
                _ => throw new FormatException($"„{id}” nu e acceptat (doar value și value_json)"),
            };
        }

        private object? LiteralValue()
        {
            Ws();
            if (Peek('\'') || Peek('"')) return StringLiteral();
            var neg = Accept('-');
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
            {
                var n = Number();
                return neg ? n is long l ? (object)(-l) : -(double)n : n;
            }
            if (neg) throw new FormatException("argument invalid");
            var id = Identifier();
            return id switch
            {
                "true" or "True" => true,
                "false" or "False" => false,
                "none" or "None" => null,
                _ => throw new FormatException($"argumentul „{id}” nu e o constantă"),
            };
        }

        private string Identifier()
        {
            Ws();
            var start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            if (start == _i || char.IsDigit(_s[start])) throw new FormatException($"se aștepta un nume la poziția {start}");
            return _s[start.._i];
        }

        private long Integer()
        {
            Ws();
            var neg = Accept('-');
            var start = _i;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (start == _i) throw new FormatException($"se aștepta un indice la poziția {start}");
            if (!long.TryParse(_s[start.._i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) throw new FormatException("indice prea mare");
            return neg ? -v : v;
        }

        private object Number()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            var text = _s[start.._i];
            if (!text.Contains('.'))
                return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : throw new FormatException($"număr prea mare „{text}”");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new FormatException($"număr invalid „{text}”");
            return d;
        }

        private string StringLiteral()
        {
            Ws();
            var q = _s[_i++];
            var sb = new StringBuilder();
            while (_i < _s.Length && _s[_i] != q)
            {
                if (_s[_i] == '\\' && _i + 1 < _s.Length) _i++;
                sb.Append(_s[_i++]);
            }
            if (_i >= _s.Length) throw new FormatException("șir neterminat");
            _i++;
            return sb.ToString();
        }
    }
}
