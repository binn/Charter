namespace Charter.Adapters;

/// <summary>
/// Raised when an <c>events.map</c> predicate cannot be parsed.
/// </summary>
/// <remarks>
/// Parsing happens at load time, never while a session is streaming: a predicate that survives
/// <see cref="EventExpression.Parse"/> evaluates without throwing for every possible input line.
/// </remarks>
public sealed class EventExpressionException : Exception
{
    public EventExpressionException()
        : base("The event mapping expression is not valid.")
    {
    }

    public EventExpressionException(string message)
        : base(message)
    {
    }

    public EventExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
