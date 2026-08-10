using System.Text.RegularExpressions;

namespace Charter.Refinement;

/// <summary>What kind of instruction-shaped content was spotted in a submitted request (section 16).</summary>
public enum InjectionSignalKind
{
    /// <summary>Text trying to replace the model's instructions — <em>ignore all previous instructions</em>.</summary>
    RoleOverride,

    /// <summary>An imperative addressed to an agent rather than a description of a wanted outcome.</summary>
    AgentDirectedImperative,

    /// <summary>A long encoded-looking blob, which no requester writes by hand.</summary>
    EncodedBlob,

    /// <summary>A link. Fetching one is how instructions arrive from somewhere Charter cannot see.</summary>
    ExternalUrl,

    /// <summary>Zero-width or bidirectional control characters, which hide text from a human reviewer.</summary>
    HiddenCharacters,
}

/// <summary>
/// One flag raised against a submitted request. Flags do not block refinement — they route the
/// result to an engineer before anything is dispatched (section 16).
/// </summary>
/// <param name="Kind">What was spotted.</param>
/// <param name="Excerpt">A short, truncated excerpt so an engineer can judge it.</param>
/// <param name="Explanation">Plain English, for the engineer review queue.</param>
public sealed record InjectionSignal(InjectionSignalKind Kind, string Excerpt, string Explanation);

/// <summary>
/// Scans submitted requests for instruction-shaped language (section 16).
/// </summary>
/// <remarks>
/// <para>
/// This is a <strong>flagging</strong> pass, not a filter and not a sanitiser. Section 16 is explicit
/// that "ignore injected instructions" in a system prompt is a layer rather than the defence; the
/// same is true here. The defence is that the agent never receives this text at all — refinement
/// replaces it with a model-authored spec, and a human approves that spec. What this adds is that a
/// request which <em>looks</em> like an attempt reaches an engineer before it reaches a runner.
/// </para>
/// <para>
/// It is deliberately noisy in the safe direction. A false positive costs one engineer glance; a
/// false negative costs a session.
/// </para>
/// </remarks>
public static partial class InstructionShapedTextDetector
{
    private const int ExcerptLength = 120;

    private static readonly string[] RoleOverridePhrases =
    [
        "ignore all previous",
        "ignore previous instruction",
        "ignore the above",
        "ignore any prior",
        "disregard all previous",
        "disregard the above",
        "disregard your instruction",
        "forget your instruction",
        "forget everything above",
        "your new instructions",
        "new instructions:",
        "system prompt",
        "you are now",
        "act as if you",
        "pretend to be",
        "from now on you",
        "override your",
        "developer mode",
        "<|im_start|>",
        "<|system|>",
        "[[system]]",
    ];

    private static readonly string[] AgentDirectedPhrases =
    [
        "rm -rf",
        "sudo ",
        "chmod ",
        "curl ",
        "wget ",
        "git push",
        "git commit",
        "npm publish",
        "drop table",
        "delete from ",
        "os.system",
        "subprocess.",
        "eval(",
        "base64 -d",
        "cat ~/",
        ".ssh/id_",
        "env |",
        "printenv",
        "process.env",
        "exfiltrat",
        "send the contents",
        "post the contents",
        "upload the file",
        "print your instructions",
        "reveal your",
        "as an ai",
        "assistant:",
        "you must run",
        "run the following",
        "execute the following",
    ];

    /// <summary>Scans submitted text and returns every flag raised, in the order found.</summary>
    public static IReadOnlyList<InjectionSignal> Scan(RequesterText text)
    {
        var raw = text.RevealForScanning();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var signals = new List<InjectionSignal>();
        var lowered = raw.ToLowerInvariant();

        foreach (var phrase in RoleOverridePhrases)
        {
            var index = lowered.IndexOf(phrase, StringComparison.Ordinal);
            if (index >= 0)
            {
                signals.Add(new InjectionSignal(
                    InjectionSignalKind.RoleOverride,
                    Excerpt(raw, index),
                    "This request contains text that tries to replace the instructions the model was "
                    + "given, rather than describing a change. An engineer should read it before "
                    + "anything is built."));
                break;
            }
        }

        foreach (var phrase in AgentDirectedPhrases)
        {
            var index = lowered.IndexOf(phrase, StringComparison.Ordinal);
            if (index >= 0)
            {
                signals.Add(new InjectionSignal(
                    InjectionSignalKind.AgentDirectedImperative,
                    Excerpt(raw, index),
                    "This request gives a command aimed at the agent or the machine it runs on, "
                    + "instead of describing what should change for the people using the app."));
                break;
            }
        }

        foreach (Match match in EncodedBlobPattern().Matches(raw))
        {
            if (!LooksEncoded(match.Value))
            {
                continue;
            }

            signals.Add(new InjectionSignal(
                InjectionSignalKind.EncodedBlob,
                Truncate(match.Value),
                "This request contains a long encoded-looking block. Requests are written in plain "
                + "language, so an engineer should check what it decodes to."));
            break;
        }

        var url = UrlPattern().Match(raw);
        if (url.Success)
        {
            signals.Add(new InjectionSignal(
                InjectionSignalKind.ExternalUrl,
                Truncate(url.Value),
                "This request contains a link. Charter never fetches links from a request, but an "
                + "engineer should confirm the link is context rather than an instruction to follow."));
        }

        var hidden = HiddenCharacterPattern().Match(raw);
        if (hidden.Success)
        {
            signals.Add(new InjectionSignal(
                InjectionSignalKind.HiddenCharacters,
                $"U+{char.ConvertToUtf32(hidden.Value, 0):X4}",
                "This request contains characters that do not display, which can hide text from the "
                + "person reviewing it."));
        }

        return signals;
    }

    /// <summary>Scans and reports only whether anything was raised.</summary>
    public static bool IsFlagged(RequesterText text) => Scan(text).Count > 0;

    // Long unbroken runs of base64-ish characters. Length 32 is well past any ordinary word or
    // identifier a requester would type.
    [GeneratedRegex(@"[A-Za-z0-9+/]{32,}={0,2}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EncodedBlobPattern();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlPattern();

    // Zero-width and bidirectional control characters.
    [GeneratedRegex(
        "[\\u200B-\\u200F\\u202A-\\u202E\\u2060-\\u2064\\uFEFF]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HiddenCharacterPattern();

    // A run of base64 characters is only interesting when it actually looks encoded. `Application`
    // and `ThisIsAVeryLongCamelCaseIdentifier` are not.
    private static bool LooksEncoded(string candidate)
    {
        var digits = 0;
        var upper = 0;
        var lower = 0;
        var symbols = 0;

        foreach (var character in candidate)
        {
            if (char.IsAsciiDigit(character))
            {
                digits++;
            }
            else if (char.IsAsciiLetterUpper(character))
            {
                upper++;
            }
            else if (char.IsAsciiLetterLower(character))
            {
                lower++;
            }
            else
            {
                symbols++;
            }
        }

        // Mixed case plus at least a couple of digits or padding characters. A long CamelCase
        // identifier has the mixed case but not the digits, and is not flagged.
        return upper > 0 && lower > 0 && digits + symbols >= 2;
    }

    private static string Excerpt(string raw, int index)
    {
        var start = Math.Max(0, index - 20);
        var length = Math.Min(ExcerptLength, raw.Length - start);
        return Truncate(raw.Substring(start, length));
    }

    private static string Truncate(string value) =>
        value.Length <= ExcerptLength ? value : value[..ExcerptLength] + "…";
}
