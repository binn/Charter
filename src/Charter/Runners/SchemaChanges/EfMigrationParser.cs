using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Charter.Runners.SchemaChanges;

/// <summary>One <c>migrationBuilder.X(...)</c> call in a migration's <c>Up</c> method.</summary>
/// <param name="Name">The operation — <c>AddColumn</c>, <c>DropTable</c>, <c>Sql</c>.</param>
/// <param name="TypeArgument">The generic argument, if any: <c>string</c> in <c>AddColumn&lt;string&gt;</c>.</param>
/// <param name="Arguments">Named arguments, by name, with their source text.</param>
/// <param name="Positional">Positional arguments, in order, as source text.</param>
public sealed record MigrationOperation(
    string Name,
    string? TypeArgument,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyList<string> Positional)
{
    /// <summary>The <c>table:</c> argument, unquoted, when the call names one.</summary>
    public string? Table => Text("table") ?? Text("name");

    /// <summary>The <c>name:</c> argument, unquoted — a column, index or constraint name.</summary>
    public string? Name_ => Text("name");

    /// <summary><c>nullable: true</c>. Null when the call does not say.</summary>
    public bool? Nullable => Flag("nullable");

    /// <summary>True when the call supplies a default, in either form EF generates.</summary>
    public bool HasDefault
        => Arguments.ContainsKey("defaultValue") || Arguments.ContainsKey("defaultValueSql");

    /// <summary>True when the call describes a column that was previously nullable.</summary>
    public bool? OldNullable => Flag("oldNullable");

    /// <summary>The unquoted text of a named string argument.</summary>
    public string? Text(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return Arguments.TryGetValue(argument, out var value) ? Unquote(value) : null;
    }

    /// <summary>A named boolean argument, when it is a literal.</summary>
    public bool? Flag(string argument)
    {
        if (!Arguments.TryGetValue(argument, out var value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.StartsWith("@\"", StringComparison.Ordinal) && trimmed.EndsWith('"'))
        {
            return trimmed[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}

/// <summary>
/// Parses the <c>Up</c> method of a generated EF Core migration into its operation list.
/// </summary>
/// <remarks>
/// <para>
/// Section 15 requires classification to be <strong>structural, not heuristic</strong>: inspect the
/// EF Core <c>Up</c> operations rather than grepping the file for the word <c>DROP</c>. That is what
/// this does. It finds the <c>Up</c> method, walks its body respecting string literals, comments and
/// nesting, and yields each <c>migrationBuilder</c> call with its arguments broken out by name.
/// </para>
/// <para>
/// Why not Roslyn: bringing the C# compiler into the control plane to read one method is a large
/// dependency for a small, extremely regular input. EF generates these files, and every call in them
/// has the same shape — a member access on the builder parameter, named arguments, one statement per
/// operation. What matters for section 15 is that <c>nullable: false</c> without a
/// <c>defaultValue:</c> is recognised as a distinct thing from <c>nullable: false</c> with one, and
/// that is an argument-level fact, not a textual one.
/// </para>
/// <para>
/// A file this cannot parse is reported as unparseable rather than as empty. An empty operation list
/// classifies as additive, and silently treating an unreadable migration as safe is precisely the
/// failure this section exists to prevent.
/// </para>
/// </remarks>
public static class EfMigrationParser
{
    /// <summary>Parses the <c>Up</c> method. False when the file has no recognisable <c>Up</c>.</summary>
    public static bool TryParseUp(
        string source,
        [NotNullWhen(true)] out IReadOnlyList<MigrationOperation>? operations,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(source);

        operations = null;
        problem = null;

        if (!TryFindMethodBody(source, "Up", out var body, out var builderName))
        {
            problem = "The migration has no `Up(MigrationBuilder ...)` method, so its operations cannot "
                + "be inspected. Charter classifies migrations structurally (section 15) and will not "
                + "guess at a file it cannot read.";
            return false;
        }

        operations = ParseOperations(body, builderName);
        return true;
    }

    /// <summary>Parses the operation list out of an already-extracted method body.</summary>
    internal static IReadOnlyList<MigrationOperation> ParseOperations(string body, string builderName)
    {
        var operations = new List<MigrationOperation>();
        var index = 0;

        while (index < body.Length)
        {
            var found = IndexOfIdentifier(body, builderName, index);
            if (found < 0)
            {
                break;
            }

            index = found + builderName.Length;

            // A builder call chains through '.' - possibly across a line break.
            var cursor = SkipTrivia(body, index);
            if (cursor >= body.Length || body[cursor] != '.')
            {
                continue;
            }

            cursor = SkipTrivia(body, cursor + 1);
            var nameStart = cursor;
            while (cursor < body.Length && (char.IsLetterOrDigit(body[cursor]) || body[cursor] == '_'))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                continue;
            }

            var name = body[nameStart..cursor];
            cursor = SkipTrivia(body, cursor);

            string? typeArgument = null;
            if (cursor < body.Length && body[cursor] == '<')
            {
                var close = MatchBracket(body, cursor, '<', '>');
                if (close < 0)
                {
                    continue;
                }

                typeArgument = body[(cursor + 1)..close].Trim();
                cursor = SkipTrivia(body, close + 1);
            }

            if (cursor >= body.Length || body[cursor] != '(')
            {
                continue;
            }

            var closeParen = MatchBracket(body, cursor, '(', ')');
            if (closeParen < 0)
            {
                continue;
            }

            var (named, positional) = SplitArguments(body[(cursor + 1)..closeParen]);
            operations.Add(new MigrationOperation(name, typeArgument, named, positional));
            index = closeParen + 1;
        }

        return operations;
    }

    /// <summary>Finds a method body and the name of its <c>MigrationBuilder</c> parameter.</summary>
    internal static bool TryFindMethodBody(
        string source,
        string methodName,
        out string body,
        out string builderName)
    {
        body = string.Empty;
        builderName = "migrationBuilder";

        var search = 0;
        while (true)
        {
            var found = IndexOfIdentifier(source, methodName, search);
            if (found < 0)
            {
                return false;
            }

            search = found + methodName.Length;

            var cursor = SkipTrivia(source, search);
            if (cursor >= source.Length || source[cursor] != '(')
            {
                continue;
            }

            var closeParen = MatchBracket(source, cursor, '(', ')');
            if (closeParen < 0)
            {
                return false;
            }

            var parameters = source[(cursor + 1)..closeParen];
            if (!parameters.Contains("MigrationBuilder", StringComparison.Ordinal))
            {
                continue;
            }

            var words = parameters.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var builderIndex = Array.FindIndex(words, word => word == "MigrationBuilder");
            if (builderIndex >= 0 && builderIndex + 1 < words.Length)
            {
                builderName = words[builderIndex + 1].TrimEnd(',', ')');
            }

            var openBrace = SkipTrivia(source, closeParen + 1);
            if (openBrace >= source.Length || source[openBrace] != '{')
            {
                continue;
            }

            var closeBrace = MatchBracket(source, openBrace, '{', '}');
            if (closeBrace < 0)
            {
                return false;
            }

            body = source[(openBrace + 1)..closeBrace];
            return true;
        }
    }

    /// <summary>Splits a top-level argument list into named and positional arguments.</summary>
    internal static (IReadOnlyDictionary<string, string> Named, IReadOnlyList<string> Positional) SplitArguments(
        string arguments)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();

        foreach (var argument in SplitTopLevel(arguments))
        {
            var text = argument.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var colon = FindArgumentNameColon(text);
            if (colon > 0)
            {
                named[text[..colon].Trim()] = text[(colon + 1)..].Trim();
            }
            else
            {
                positional.Add(text);
            }
        }

        return (named, positional);
    }

    /// <summary>
    /// The colon that makes <c>nullable: false</c> a named argument, or -1.
    /// </summary>
    /// <remarks>
    /// Only a leading identifier followed by a colon counts. That excludes <c>?:</c>, <c>::</c>, and a
    /// colon inside a lambda body or a nested call, all of which appear in generated migrations.
    /// </remarks>
    private static int FindArgumentNameColon(string argument)
    {
        var index = 0;
        while (index < argument.Length && (char.IsLetterOrDigit(argument[index]) || argument[index] == '_'))
        {
            index++;
        }

        if (index == 0)
        {
            return -1;
        }

        var cursor = index;
        while (cursor < argument.Length && char.IsWhiteSpace(argument[cursor]))
        {
            cursor++;
        }

        if (cursor >= argument.Length || argument[cursor] != ':')
        {
            return -1;
        }

        // `::` is a namespace alias, not a named argument.
        return cursor + 1 < argument.Length && argument[cursor + 1] == ':' ? -1 : cursor;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (TrySkipLiteralOrComment(text, ref index))
            {
                continue;
            }

            switch (character)
            {
                case '(' or '[' or '{' or '<' when character != '<' || IsGenericOpen(text, index):
                    depth++;
                    break;
                case ')' or ']' or '}' or '>' when character != '>' || depth > 0:
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..index];
                    start = index + 1;
                    break;
            }

            index++;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>
    /// Distinguishes a generic argument list from a comparison. Generated migrations only ever use
    /// <c>&lt;</c> for generics, so the test is deliberately conservative: an identifier character
    /// must follow.
    /// </summary>
    private static bool IsGenericOpen(string text, int index)
        => index + 1 < text.Length && (char.IsLetter(text[index + 1]) || text[index + 1] == '_');

    private static int IndexOfIdentifier(string text, string identifier, int start)
    {
        var index = start;

        while (index < text.Length)
        {
            if (TrySkipLiteralOrComment(text, ref index))
            {
                continue;
            }

            if (text[index] == identifier[0]
                && index + identifier.Length <= text.Length
                && string.CompareOrdinal(text, index, identifier, 0, identifier.Length) == 0
                && (index == 0 || !IsIdentifierPart(text[index - 1]))
                && (index + identifier.Length == text.Length || !IsIdentifierPart(text[index + identifier.Length])))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool IsIdentifierPart(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static int SkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var before = index;
            if (TrySkipComment(text, ref index) && index != before)
            {
                continue;
            }

            break;
        }

        return index;
    }

    private static int MatchBracket(string text, int open, char opening, char closing)
    {
        var depth = 0;
        var index = open;

        while (index < text.Length)
        {
            if (TrySkipLiteralOrComment(text, ref index))
            {
                continue;
            }

            if (text[index] == opening)
            {
                depth++;
            }
            else if (text[index] == closing)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }

            index++;
        }

        return -1;
    }

    /// <summary>Advances past a string, char literal or comment. True when it moved.</summary>
    private static bool TrySkipLiteralOrComment(string text, ref int index)
    {
        if (TrySkipComment(text, ref index))
        {
            return true;
        }

        if (index + 2 < text.Length && text[index] == '@' && text[index + 1] == '"')
        {
            index += 2;
            while (index < text.Length)
            {
                if (text[index] == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    return true;
                }

                index++;
            }

            return true;
        }

        if (text[index] == '"')
        {
            // Raw string literal: run to the matching run of quotes.
            if (index + 2 < text.Length && text[index + 1] == '"' && text[index + 2] == '"')
            {
                var fence = 0;
                while (index + fence < text.Length && text[index + fence] == '"')
                {
                    fence++;
                }

                index += fence;
                var closing = text.IndexOf(new string('"', fence), index, StringComparison.Ordinal);
                index = closing < 0 ? text.Length : closing + fence;
                return true;
            }

            index++;
            while (index < text.Length)
            {
                if (text[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (text[index] == '"')
                {
                    index++;
                    return true;
                }

                index++;
            }

            return true;
        }

        if (text[index] == '\'')
        {
            index++;
            while (index < text.Length)
            {
                if (text[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (text[index] == '\'')
                {
                    index++;
                    return true;
                }

                index++;
            }

            return true;
        }

        return false;
    }

    private static bool TrySkipComment(string text, ref int index)
    {
        if (index + 1 >= text.Length || text[index] != '/')
        {
            return false;
        }

        if (text[index + 1] == '/')
        {
            var newline = text.IndexOf('\n', index);
            index = newline < 0 ? text.Length : newline + 1;
            return true;
        }

        if (text[index + 1] == '*')
        {
            var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            index = close < 0 ? text.Length : close + 2;
            return true;
        }

        return false;
    }

    /// <summary>Renders an operation for a transcript line.</summary>
    public static string Describe(MigrationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var builder = new StringBuilder(operation.Name);
        var table = operation.Text("table");
        var name = operation.Text("name");

        if (name is not null && table is not null)
        {
            builder.Append(' ').Append(name).Append(" on ").Append(table);
        }
        else if (name is not null)
        {
            builder.Append(' ').Append(name);
        }

        return builder.ToString();
    }
}
