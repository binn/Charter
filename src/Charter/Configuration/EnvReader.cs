using System.Globalization;

namespace Charter.Configuration;

/// <summary>
/// Reads environment variables and accumulates problems instead of throwing on the first one.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism behind the section 4.1 rule that startup validation reports <em>every</em>
/// problem at once. Each accessor returns a usable fallback after recording a problem so parsing can
/// continue and find the rest, rather than aborting at the first fault and forcing the operator to
/// fix a misconfigured instance one restart at a time.
/// </para>
/// <para>
/// Every message follows the same shape - the variable name, what is wrong, what was expected - so
/// the printed list reads consistently regardless of which parser produced a line.
/// </para>
/// </remarks>
internal sealed class EnvReader
{
    private readonly Func<string, string?> read;
    private readonly List<ConfigProblem> problems = [];

    internal EnvReader(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        this.read = read;
    }

    /// <summary>Every problem found so far, in the order the variables were read.</summary>
    internal IReadOnlyList<ConfigProblem> Problems => problems;

    /// <summary>True when at least one problem blocks startup.</summary>
    internal bool HasErrors => problems.Any(problem => problem.Severity == ConfigSeverity.Error);

    /// <summary>Records a problem that blocks startup.</summary>
    internal void Error(string variable, string message)
        => problems.Add(new ConfigProblem(variable, message));

    /// <summary>Records a problem that does not block startup but is almost certainly a mistake.</summary>
    internal void Warn(string variable, string message)
        => problems.Add(new ConfigProblem(variable, message, ConfigSeverity.Warning));

    /// <summary>Records "<c>NAME</c> must be {expectation}, got '{raw}'".</summary>
    internal void Invalid(string variable, string expectation, string raw)
        => Error(variable, $"{variable} must be {expectation}, got '{raw}'");

    /// <summary>Records "<c>NAME</c> is required: {expectation}".</summary>
    internal void Missing(string variable, string expectation)
        => Error(variable, $"{variable} is required: {expectation}");

    /// <summary>The trimmed value, or <c>null</c> when unset or blank.</summary>
    internal string? Optional(string name)
    {
        var value = read(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The trimmed value, recording a problem and returning <c>null</c> when it is absent.</summary>
    internal string? Required(string name, string expectation)
    {
        var value = Optional(name);
        if (value is null)
        {
            Missing(name, expectation);
        }

        return value;
    }

    /// <summary>The value wrapped as a <see cref="Secret"/>, or <c>null</c> when unset.</summary>
    internal Secret? OptionalSecret(string name) => Secret.From(Optional(name));

    /// <summary>The value wrapped as a <see cref="Secret"/>, recording a problem when absent.</summary>
    internal Secret? RequiredSecret(string name, string expectation)
        => Secret.From(Required(name, expectation));

    /// <summary>Parses <c>true</c>/<c>false</c>, falling back after recording a problem.</summary>
    internal bool Bool(string name, bool fallback)
    {
        var raw = Optional(name);
        if (raw is null)
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        Invalid(name, "true or false", raw);
        return fallback;
    }

    /// <summary>Parses an integer within an inclusive range, falling back after recording a problem.</summary>
    internal int Int(string name, int fallback, int minimum, int maximum, string expectation)
    {
        var raw = Optional(name);
        if (raw is null)
        {
            return fallback;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minimum && parsed <= maximum)
        {
            return parsed;
        }

        Invalid(name, expectation, raw);
        return fallback;
    }

    /// <summary>Parses a required positive 64-bit integer, such as a GitHub App ID.</summary>
    internal long RequiredId(string name, string expectation)
    {
        var raw = Required(name, expectation);
        if (raw is null)
        {
            return 0;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        Invalid(name, expectation, raw);
        return 0;
    }

    /// <summary>Parses a non-negative decimal amount, falling back after recording a problem.</summary>
    internal decimal Money(string name, decimal fallback)
    {
        var raw = Optional(name);
        if (raw is null)
        {
            return fallback;
        }

        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0m)
        {
            return parsed;
        }

        Invalid(name, "a non-negative amount in US dollars, for example 5.00", raw);
        return fallback;
    }

    /// <summary>Parses an absolute <c>http</c> or <c>https</c> URL.</summary>
    internal Uri? Url(string name, string expectation, bool required)
    {
        var raw = required ? Required(name, expectation) : Optional(name);
        if (raw is null)
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            Invalid(name, expectation, raw);
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            Invalid(name, expectation, raw);
            return null;
        }

        return uri;
    }

    /// <summary>Matches a value case-insensitively against a set of accepted tokens.</summary>
    internal T Choice<T>(string name, T fallback, IReadOnlyList<(string Token, T Value)> accepted)
    {
        var raw = Optional(name);
        if (raw is null)
        {
            return fallback;
        }

        foreach (var (token, value) in accepted)
        {
            if (string.Equals(token, raw, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        Invalid(name, $"one of {string.Join(", ", accepted.Select(entry => entry.Token))}", raw);
        return fallback;
    }
}
