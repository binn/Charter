using System.Text.Json;
using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Covers the <c>events.map</c> expression subset and the line classifier that uses it (section 12b).
/// </summary>
/// <remarks>
/// The contract these tests pin down: parsing throws, evaluation never does. Every line an agent can
/// emit — matching, unmatched, blank, or truncated mid-write — has to come back as a classification,
/// because this runs on every line of a live session inside the shim.
/// </remarks>
public class AdapterEventMappingTests
{
    private static bool Evaluate(string expression, string json)
    {
        using var document = JsonDocument.Parse(json);
        return EventExpression.Parse(expression).Evaluate(document.RootElement);
    }

    // ---------------------------------------------------------------------------------------------
    // The supported subset
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("$.type == 'tool_call'", """{"type":"tool_call"}""", true)]
    [InlineData("$.type == 'tool_call'", """{"type":"assistant"}""", false)]
    [InlineData("$.type != 'tool_call'", """{"type":"assistant"}""", true)]
    [InlineData("$.type == \"tool_call\"", """{"type":"tool_call"}""", true)]
    [InlineData("$.tool == 'edit' || $.tool == 'write'", """{"tool":"write"}""", true)]
    [InlineData("$.tool == 'edit' || $.tool == 'write'", """{"tool":"read"}""", false)]
    [InlineData("$.type == 'assistant' && $.subtype == 'text'", """{"type":"assistant","subtype":"text"}""", true)]
    [InlineData("$.type == 'assistant' && $.subtype == 'text'", """{"type":"assistant"}""", false)]
    [InlineData("!($.type == 'result')", """{"type":"assistant"}""", true)]
    [InlineData("$.a == 'x' || ($.b == 'y' && $.c == 'z')", """{"b":"y","c":"z"}""", true)]
    [InlineData("$.message.content[0].type == 'tool_use'", """{"message":{"content":[{"type":"tool_use"}]}}""", true)]
    [InlineData("$.message.content[1].type == 'tool_use'", """{"message":{"content":[{"type":"tool_use"}]}}""", false)]
    [InlineData("$['message']['content'][0]['name'] == 'Edit'", """{"message":{"content":[{"name":"Edit"}]}}""", true)]
    [InlineData("$.is_error == true", """{"is_error":true}""", true)]
    [InlineData("$.is_error == false", """{"is_error":true}""", false)]
    [InlineData("$.exit_code == 0", """{"exit_code":0}""", true)]
    [InlineData("$.exit_code == 1", """{"exit_code":0}""", false)]
    [InlineData("$.cost == 0.25", """{"cost":0.25}""", true)]
    [InlineData("$.parent == null", """{"parent":null}""", true)]
    [InlineData("$.parent == null", "{}", true)]
    [InlineData("$.parent != null", """{"parent":"abc"}""", true)]
    [InlineData("$.tool", """{"tool":"edit"}""", true)]
    [InlineData("$.tool", """{"tool":""}""", false)]
    [InlineData("$.tool", "{}", false)]
    [InlineData("!$.tool", "{}", true)]
    [InlineData("$.type == 'a' && !$.hidden", """{"type":"a"}""", true)]
    public void EvaluatesTheSupportedSubset(string expression, string json, bool expected)
        => Assert.Equal(expected, Evaluate(expression, json));

    [Fact]
    public void ComparisonIsStrictAboutType()
    {
        // A quoted "1" is not the number 1. Adapter authors need to know this, because a JSON stream
        // that quotes its numbers would otherwise match nothing and give no clue why.
        Assert.False(Evaluate("$.n == 1", """{"n":"1"}"""));
        Assert.True(Evaluate("$.n == '1'", """{"n":"1"}"""));
    }

    [Fact]
    public void ObjectsAndArraysAreNeverEqualToALiteral()
    {
        Assert.False(Evaluate("$.content == 'x'", """{"content":[1,2]}"""));
        Assert.False(Evaluate("$.content == null", """{"content":[1,2]}"""));
        Assert.True(Evaluate("$.content", """{"content":[1,2]}"""));
    }

    [Fact]
    public void AMissingBranchDoesNotMatchRatherThanThrowing()
    {
        Assert.False(Evaluate("$.message.content[0].name == 'Edit'", """{"type":"result"}"""));
        Assert.False(Evaluate("$.message.content[0].name == 'Edit'", """{"message":{"content":"not an array"}}"""));
    }

    [Fact]
    public void EvaluatesAgainstANonObjectLine()
    {
        Assert.False(Evaluate("$.type == 'a'", "[1,2,3]"));
        Assert.False(Evaluate("$.type == 'a'", "\"a bare string\""));
    }

    // ---------------------------------------------------------------------------------------------
    // What is deliberately not supported
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("$.a =~ 'x'", "=~")]
    [InlineData("$.a > 1", ">")]
    [InlineData("$.a >= 1", ">=")]
    [InlineData("$.a = 1", "=")]
    public void RejectsOperatorsOutsideTheSubset(string expression, string operatorText)
    {
        var error = Assert.Throws<EventExpressionException>(() => EventExpression.Parse(expression));

        Assert.Contains($"'{operatorText}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'==', '!=', '&&', '||' and '!'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$..name == 'x'", "recursive descent")]
    [InlineData("$.content[*].name == 'x'", "Wildcards and filters")]
    [InlineData("$.content[?(@.name)]", "Wildcards and filters")]
    public void RejectsJsonPathFeaturesOutsideTheSubset(string expression, string expectedHint)
    {
        var error = Assert.Throws<EventExpressionException>(() => EventExpression.Parse(expression));

        Assert.Contains(expectedHint, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type == 'tool_call'")]
    [InlineData("$.type ==")]
    [InlineData("($.type == 'a'")]
    [InlineData("$.type == 'unterminated")]
    [InlineData("")]
    [InlineData("$.")]
    public void RejectsExpressionsItCannotParse(string expression)
        => Assert.Throws<EventExpressionException>(() => EventExpression.Parse(expression));

    [Fact]
    public void ParseErrorsSayWhereTheyAre()
    {
        var error = Assert.Throws<EventExpressionException>(() => EventExpression.Parse("type == 'x'"));

        Assert.Contains("at position", error.Message, StringComparison.Ordinal);
        Assert.Contains("must start at '$'", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Classification
    // ---------------------------------------------------------------------------------------------

    private static AdapterEventClassifier Classifier()
        => new(AdapterTestFiles.Load(AdapterTestFiles.ValidYaml));

    [Fact]
    public void ClassifiesALineIntoEveryEventTypeItMatchesMostSpecificFirst()
    {
        var result = Classifier().Classify("""{"type":"tool_call","tool":"edit"}""");

        Assert.Equal(AdapterLineKind.Matched, result.Kind);
        Assert.Equal([AdapterEventType.FileWrite, AdapterEventType.ToolUse], result.Matches);
        Assert.Equal(AdapterEventType.FileWrite, result.Primary);
    }

    [Fact]
    public void ANonMatchingLineIsUnmatchedRatherThanAnError()
    {
        var result = Classifier().Classify("""{"type":"system","subtype":"init"}""");

        Assert.Equal(AdapterLineKind.Unmatched, result.Kind);
        Assert.Empty(result.Matches);
        Assert.Null(result.Primary);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"type\":\"tool_call\"")]
    [InlineData("{\"type\": }")]
    public void AMalformedLineIsReportedRatherThanThrowing(string line)
    {
        var result = Classifier().Classify(line);

        Assert.Equal(AdapterLineKind.Malformed, result.Kind);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void ABlankLineIsNeitherAnEventNorAProblem()
    {
        Assert.Equal(AdapterLineKind.Blank, Classifier().Classify("   ").Kind);
        Assert.Equal(AdapterLineKind.Blank, Classifier().Classify(string.Empty).Kind);
    }

    [Fact]
    public void ATextAdapterTreatsEveryLineAsRawLog()
    {
        var yaml = """
            id: example
            display_name: "Example"
            version: 1
            install:
              check: "example --version"
              hint: "npm install -g example"
            invoke:
              command: ["example"]
              prompt: stdin
            auth:
              anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
            events:
              format: text
            capabilities: []
            """;

        var classifier = new AdapterEventClassifier(AdapterTestFiles.Load(yaml));

        var result = classifier.Classify("""{"type":"tool_call","tool":"edit"}""");

        Assert.Equal(AdapterLineKind.Raw, result.Kind);
        Assert.Empty(result.Matches);
    }
}
