using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Charter.Adapters;

/// <summary>
/// A predicate from an adapter's <c>events.map</c> block, evaluated against one line of an agent's
/// JSONL output stream.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately small language, not JSONPath. Section 12b needs exactly enough to classify
/// a line into one of Charter's event types, and a full JSONPath engine brings filters, slices,
/// recursive descent, and script expressions that no shipped adapter uses and that an adapter author
/// would then reasonably expect to work.
/// </para>
/// <para>
/// The supported grammar, in full:
/// </para>
/// <code>
/// expression  := or
/// or          := and       ( '||' and )*
/// and         := unary     ( '&amp;&amp;' unary )*
/// unary       := '!' unary | comparison
/// comparison  := primary   ( ( '==' | '!=' ) primary )?
/// primary     := '(' expression ')' | path | literal
/// path        := '$' ( '.' name | '[' index ']' | '[' quoted ']' )*
/// literal     := quoted-string | number | 'true' | 'false' | 'null'
/// </code>
/// <para>
/// Rules an adapter author needs to know:
/// </para>
/// <list type="bullet">
/// <item><description>Paths always start at <c>$</c>, the whole parsed line. Bare identifiers are not
/// paths and are rejected at load time.</description></item>
/// <item><description>A missing key and a JSON <c>null</c> are the same thing: both compare equal to
/// <c>null</c> and to each other, and both are falsey.</description></item>
/// <item><description>Comparison is strict about type. <c>$.n == 1</c> is false when the JSON holds
/// the string <c>"1"</c>. Objects and arrays are never equal to anything.</description></item>
/// <item><description>A path used on its own is a truthiness test: true when the value is present and
/// is not <c>null</c>, <c>false</c>, <c>0</c>, or the empty string.</description></item>
/// <item><description><c>!</c> negates everything up to the next <c>&amp;&amp;</c> or <c>||</c>, so
/// <c>!$.a == 'b'</c> means <c>!($.a == 'b')</c>. Parenthesise when in doubt.</description></item>
/// <item><description>Strings may be single- or double-quoted, with <c>\\</c>, <c>\'</c>, <c>\"</c>,
/// <c>\n</c>, <c>\r</c>, and <c>\t</c> escapes.</description></item>
/// </list>
/// <para>
/// Everything else — <c>&gt;</c>, <c>&lt;</c>, <c>=~</c>, <c>..</c>, <c>[*]</c>, <c>[?(...)]</c>,
/// functions — is a parse error naming the file and field, not a silently false predicate.
/// </para>
/// <para>
/// Parsing throws; evaluation never does. Once an expression has been parsed it returns a
/// <see cref="bool"/> for every possible input, which is what lets the shim classify a hostile or
/// truncated line without a try/catch in the streaming path.
/// </para>
/// </remarks>
public abstract class EventExpression
{
    private protected EventExpression()
    {
    }

    /// <summary>Parses a predicate, throwing <see cref="EventExpressionException"/> if it is not valid.</summary>
    public static EventExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parser = new Parser(source);
        var expression = parser.ParseExpression();
        parser.ExpectEndOfInput();
        return expression;
    }

    /// <summary>Evaluates the predicate against one parsed output line. Never throws.</summary>
    public abstract bool Evaluate(JsonElement root);

    private protected abstract EventValue Value(JsonElement root);

    // ---------------------------------------------------------------------------------------------
    // Values
    // ---------------------------------------------------------------------------------------------

    private protected enum EventValueKind
    {
        /// <summary>The path did not resolve. Treated as <see cref="Null"/> for comparison.</summary>
        Absent,
        Null,
        String,
        Number,
        Boolean,

        /// <summary>An object or an array. Never equal to anything, always truthy.</summary>
        Structured,
    }

    private protected readonly record struct EventValue(
        EventValueKind Kind,
        string? Text,
        double Number,
        bool Boolean)
    {
        public static EventValue Absent { get; } = new(EventValueKind.Absent, null, 0, false);

        public static EventValue Null { get; } = new(EventValueKind.Null, null, 0, false);

        public static EventValue Structured { get; } = new(EventValueKind.Structured, null, 0, false);

        public static EventValue FromString(string value) => new(EventValueKind.String, value, 0, false);

        public static EventValue FromNumber(double value) => new(EventValueKind.Number, null, value, false);

        public static EventValue FromBoolean(bool value) => new(EventValueKind.Boolean, null, 0, value);

        public static EventValue FromElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => Structured,
            JsonValueKind.String => FromString(element.GetString() ?? string.Empty),
            JsonValueKind.Number => element.TryGetDouble(out var number) ? FromNumber(number) : Structured,
            JsonValueKind.True => FromBoolean(true),
            JsonValueKind.False => FromBoolean(false),
            _ => Null,
        };

        public bool IsNullish => Kind is EventValueKind.Absent or EventValueKind.Null;

        public bool IsTruthy => Kind switch
        {
            EventValueKind.Absent or EventValueKind.Null => false,
            EventValueKind.Boolean => Boolean,
            EventValueKind.Number => Number != 0,
            EventValueKind.String => !string.IsNullOrEmpty(Text),
            _ => true,
        };

        public static bool AreEqual(EventValue left, EventValue right)
        {
            if (left.IsNullish || right.IsNullish)
            {
                return left.IsNullish && right.IsNullish;
            }

            if (left.Kind != right.Kind)
            {
                return false;
            }

            return left.Kind switch
            {
                EventValueKind.String => string.Equals(left.Text, right.Text, StringComparison.Ordinal),
                EventValueKind.Number => left.Number.Equals(right.Number),
                EventValueKind.Boolean => left.Boolean == right.Boolean,
                _ => false,
            };
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Nodes
    // ---------------------------------------------------------------------------------------------

    private readonly record struct PathSegment(string? Name, int Index)
    {
        public bool IsIndex => Name is null;
    }

    private sealed class PathExpression : EventExpression
    {
        private readonly PathSegment[] _segments;

        public PathExpression(PathSegment[] segments) => _segments = segments;

        public override bool Evaluate(JsonElement root) => Value(root).IsTruthy;

        private protected override EventValue Value(JsonElement root)
        {
            var current = root;
            foreach (var segment in _segments)
            {
                if (segment.IsIndex)
                {
                    if (current.ValueKind != JsonValueKind.Array || segment.Index >= current.GetArrayLength())
                    {
                        return EventValue.Absent;
                    }

                    current = current[segment.Index];
                    continue;
                }

                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(segment.Name!, out var next))
                {
                    return EventValue.Absent;
                }

                current = next;
            }

            return EventValue.FromElement(current);
        }
    }

    private sealed class LiteralExpression : EventExpression
    {
        private readonly EventValue _value;

        public LiteralExpression(EventValue value) => _value = value;

        public override bool Evaluate(JsonElement root) => _value.IsTruthy;

        private protected override EventValue Value(JsonElement root) => _value;
    }

    private sealed class ComparisonExpression : EventExpression
    {
        private readonly EventExpression _left;
        private readonly EventExpression _right;
        private readonly bool _negated;

        public ComparisonExpression(EventExpression left, EventExpression right, bool negated)
        {
            _left = left;
            _right = right;
            _negated = negated;
        }

        public override bool Evaluate(JsonElement root)
            => EventValue.AreEqual(_left.Value(root), _right.Value(root)) != _negated;

        private protected override EventValue Value(JsonElement root) => EventValue.FromBoolean(Evaluate(root));
    }

    private sealed class AndExpression : EventExpression
    {
        private readonly EventExpression _left;
        private readonly EventExpression _right;

        public AndExpression(EventExpression left, EventExpression right)
        {
            _left = left;
            _right = right;
        }

        public override bool Evaluate(JsonElement root) => _left.Evaluate(root) && _right.Evaluate(root);

        private protected override EventValue Value(JsonElement root) => EventValue.FromBoolean(Evaluate(root));
    }

    private sealed class OrExpression : EventExpression
    {
        private readonly EventExpression _left;
        private readonly EventExpression _right;

        public OrExpression(EventExpression left, EventExpression right)
        {
            _left = left;
            _right = right;
        }

        public override bool Evaluate(JsonElement root) => _left.Evaluate(root) || _right.Evaluate(root);

        private protected override EventValue Value(JsonElement root) => EventValue.FromBoolean(Evaluate(root));
    }

    private sealed class NotExpression : EventExpression
    {
        private readonly EventExpression _inner;

        public NotExpression(EventExpression inner) => _inner = inner;

        public override bool Evaluate(JsonElement root) => !_inner.Evaluate(root);

        private protected override EventValue Value(JsonElement root) => EventValue.FromBoolean(Evaluate(root));
    }

    // ---------------------------------------------------------------------------------------------
    // Parser
    // ---------------------------------------------------------------------------------------------

    private sealed class Parser
    {
        private const int MaxDepth = 32;

        private static readonly string[] UnsupportedOperators = ["=~", ">=", "<=", ">", "<", "="];

        private readonly string _source;
        private int _position;
        private int _depth;

        public Parser(string source) => _source = source;

        public EventExpression ParseExpression()
        {
            if (++_depth > MaxDepth)
            {
                throw Error("the expression is nested too deeply");
            }

            var expression = ParseOr();
            _depth--;
            return expression;
        }

        public void ExpectEndOfInput()
        {
            SkipWhitespace();
            if (_position < _source.Length)
            {
                throw Error($"unexpected '{_source[_position]}'");
            }
        }

        private EventExpression ParseOr()
        {
            var left = ParseAnd();
            while (TryConsume("||"))
            {
                left = new OrExpression(left, ParseAnd());
            }

            return left;
        }

        private EventExpression ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsume("&&"))
            {
                left = new AndExpression(left, ParseUnary());
            }

            return left;
        }

        private EventExpression ParseUnary()
        {
            SkipWhitespace();

            // '!' before '=' is the '!=' operator, not a negation.
            if (_position < _source.Length && _source[_position] == '!' && !Looking("!="))
            {
                _position++;
                return new NotExpression(ParseUnary());
            }

            return ParseComparison();
        }

        private EventExpression ParseComparison()
        {
            var left = ParsePrimary();

            SkipWhitespace();
            if (TryConsume("=="))
            {
                return new ComparisonExpression(left, ParsePrimary(), negated: false);
            }

            if (TryConsume("!="))
            {
                return new ComparisonExpression(left, ParsePrimary(), negated: true);
            }

            RejectUnsupportedOperator();
            return left;
        }

        private EventExpression ParsePrimary()
        {
            SkipWhitespace();
            if (_position >= _source.Length)
            {
                throw Error("expected a path, a literal or '('");
            }

            var c = _source[_position];

            if (c == '(')
            {
                _position++;
                var inner = ParseExpression();
                SkipWhitespace();
                if (_position >= _source.Length || _source[_position] != ')')
                {
                    throw Error("expected ')'");
                }

                _position++;
                return inner;
            }

            if (c == '!')
            {
                _position++;
                return new NotExpression(ParseUnary());
            }

            if (c == '$')
            {
                return ParsePath();
            }

            if (c is '\'' or '"')
            {
                return new LiteralExpression(EventValue.FromString(ParseQuoted()));
            }

            if (char.IsAsciiDigit(c) || c == '-')
            {
                return new LiteralExpression(EventValue.FromNumber(ParseNumber()));
            }

            if (TryConsumeWord("true"))
            {
                return new LiteralExpression(EventValue.FromBoolean(true));
            }

            if (TryConsumeWord("false"))
            {
                return new LiteralExpression(EventValue.FromBoolean(false));
            }

            if (TryConsumeWord("null"))
            {
                return new LiteralExpression(EventValue.Null);
            }

            throw Error(
                $"expected a path, a literal or '(' but found '{c}'. Paths must start at '$', "
                + "for example \"$.type == 'tool_call'\"");
        }

        private EventExpression ParsePath()
        {
            _position++; // '$'
            var segments = new List<PathSegment>();

            while (_position < _source.Length)
            {
                var c = _source[_position];
                if (c == '.')
                {
                    if (Looking(".."))
                    {
                        throw Error("recursive descent ('..') is not supported");
                    }

                    _position++;
                    var start = _position;
                    while (_position < _source.Length
                           && (char.IsAsciiLetterOrDigit(_source[_position]) || _source[_position] is '_' or '-'))
                    {
                        _position++;
                    }

                    if (_position == start)
                    {
                        throw Error("expected a property name after '.'");
                    }

                    segments.Add(new PathSegment(_source[start.._position], 0));
                    continue;
                }

                if (c == '[')
                {
                    _position++;
                    SkipWhitespace();
                    if (_position >= _source.Length)
                    {
                        throw Error("expected an index or a quoted property name after '['");
                    }

                    PathSegment segment;
                    if (_source[_position] is '\'' or '"')
                    {
                        segment = new PathSegment(ParseQuoted(), 0);
                    }
                    else
                    {
                        var start = _position;
                        while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
                        {
                            _position++;
                        }

                        if (_position == start)
                        {
                            throw Error(
                                "expected a non-negative integer index or a quoted property name after '['. "
                                + "Wildcards and filters are not supported");
                        }

                        segment = new PathSegment(
                            null,
                            int.Parse(_source[start.._position], CultureInfo.InvariantCulture));
                    }

                    SkipWhitespace();
                    if (_position >= _source.Length || _source[_position] != ']')
                    {
                        throw Error("expected ']'");
                    }

                    _position++;
                    segments.Add(segment);
                    continue;
                }

                break;
            }

            return new PathExpression([.. segments]);
        }

        private string ParseQuoted()
        {
            var quote = _source[_position];
            _position++;

            var builder = new StringBuilder();
            while (_position < _source.Length)
            {
                var c = _source[_position];
                if (c == '\\')
                {
                    _position++;
                    if (_position >= _source.Length)
                    {
                        throw Error("the string ends with an unfinished escape");
                    }

                    builder.Append(_source[_position] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        var other => throw Error($"unsupported escape '\\{other}'"),
                    });
                    _position++;
                    continue;
                }

                if (c == quote)
                {
                    _position++;
                    return builder.ToString();
                }

                builder.Append(c);
                _position++;
            }

            throw Error("the string is never closed");
        }

        private double ParseNumber()
        {
            var start = _position;
            if (_source[_position] == '-')
            {
                _position++;
            }

            while (_position < _source.Length
                   && (char.IsAsciiDigit(_source[_position]) || _source[_position] is '.' or 'e' or 'E' or '+' or '-'))
            {
                // A '-' only continues the number when it is an exponent sign.
                if (_source[_position] is '+' or '-' && _source[_position - 1] is not ('e' or 'E'))
                {
                    break;
                }

                _position++;
            }

            var text = _source[start.._position];
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw Error($"'{text}' is not a number");
        }

        private void RejectUnsupportedOperator()
        {
            SkipWhitespace();
            if (_position >= _source.Length)
            {
                return;
            }

            foreach (var unsupported in UnsupportedOperators)
            {
                if (Looking(unsupported))
                {
                    throw Error(
                        $"the operator '{unsupported}' is not supported. Only '==', '!=', '&&', '||' and '!' are");
                }
            }
        }

        private bool Looking(string token)
            => _position + token.Length <= _source.Length
               && string.CompareOrdinal(_source, _position, token, 0, token.Length) == 0;

        private bool TryConsume(string token)
        {
            SkipWhitespace();
            if (!Looking(token))
            {
                return false;
            }

            _position += token.Length;
            return true;
        }

        private bool TryConsumeWord(string word)
        {
            if (!Looking(word))
            {
                return false;
            }

            var end = _position + word.Length;
            if (end < _source.Length && (char.IsAsciiLetterOrDigit(_source[end]) || _source[end] == '_'))
            {
                return false;
            }

            _position = end;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }
        }

        private EventExpressionException Error(string message)
            => new($"{message} (at position {_position} in \"{_source}\")");
    }
}
