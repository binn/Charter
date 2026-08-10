namespace Charter.Adapters;

/// <summary>
/// Raised when an adapter file cannot be loaded.
/// </summary>
/// <remarks>
/// Every problem names the file and the field, and all of a file's problems are reported at once —
/// the same rule section 4.1 applies to environment configuration, for the same reason: fixing an
/// adapter one restart at a time is miserable.
/// </remarks>
public sealed class AdapterLoadException : Exception
{
    private static readonly IReadOnlyList<string> NoProblems = [];

    public AdapterLoadException()
        : base("An adapter file is invalid.")
    {
        Problems = NoProblems;
    }

    public AdapterLoadException(string message)
        : base(message)
    {
        Problems = [message];
    }

    public AdapterLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    public AdapterLoadException(IReadOnlyList<string> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Every problem found, so an adapter author can fix them in one pass.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Count == 0
            ? "An adapter file is invalid."
            : string.Join(Environment.NewLine, problems);
    }
}
