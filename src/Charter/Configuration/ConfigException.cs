namespace Charter.Configuration;

/// <summary>
/// Raised when Charter's environment configuration is invalid.
/// </summary>
/// <remarks>
/// Section 4.1 requires configuration to be validated once at startup and to report every problem
/// at once rather than failing lazily on first use, so this exception carries a list rather than a
/// single message.
/// </remarks>
public sealed class ConfigException : Exception
{
    private static readonly IReadOnlyList<string> NoProblems = [];

    public ConfigException()
        : base("Charter configuration is invalid.")
    {
        Problems = NoProblems;
    }

    public ConfigException(string message)
        : base(message)
    {
        Problems = [message];
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    public ConfigException(IReadOnlyList<string> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Every configuration problem found, so an operator can fix them in one pass.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Count == 0
            ? "Charter configuration is invalid."
            : string.Join("; ", problems);
    }
}
