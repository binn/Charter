namespace Charter.Configuration;

/// <summary>How badly a configuration problem hurts.</summary>
public enum ConfigSeverity
{
    /// <summary>Charter must not start. Section 4.1: print every problem at once and exit non-zero.</summary>
    Error,

    /// <summary>
    /// Charter starts, but the operator almost certainly did not mean this - a variable set with no
    /// effect, or a requirement that only a runtime check (section 30.1 preflight) can settle.
    /// </summary>
    Warning,
}

/// <summary>
/// One thing wrong with the environment: which variable, what is wrong, and what was expected.
/// </summary>
/// <remarks>
/// Section 4.1 requires every problem to be reported together rather than one restart at a time, so
/// validation accumulates these instead of throwing at the first fault.
/// </remarks>
/// <param name="Variable">The environment variable at fault, for example <c>CHARTER_BASE_URL</c>.</param>
/// <param name="Text">A complete sentence naming the variable, the fault and the expectation.</param>
/// <param name="Severity">Whether this blocks startup.</param>
public sealed record ConfigProblem(string Variable, string Text, ConfigSeverity Severity = ConfigSeverity.Error)
{
    /// <summary>The message, so a problem interpolates into log output as its own text.</summary>
    public override string ToString() => Text;
}
