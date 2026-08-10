using System.Globalization;
using System.Text;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Domain;
using Charter.Recaps;

namespace Charter.Api.Requests;

/// <summary>
/// Projects a stored <see cref="Recap"/> row into the engineer card of section 14.
/// </summary>
/// <remarks>
/// <para>
/// The row holds two things: the markdown body <c>RecapComposer</c> assembled, and the risk-ranked
/// file list as <c>risk_items</c> jsonb. This file reads both and never re-derives either.
/// <strong>The order of <c>risk_items</c> is the order <c>RecapFileRiskRanker</c> produced</strong> —
/// auth, migrations, money maths and denylist-adjacent paths at the top, tests and formatting at the
/// bottom — and it is passed through untouched, because the client does not re-sort and re-sorting
/// here would throw away the only ranking anybody computed.
/// </para>
/// <para>
/// The body is split back into section 14's five sections by the headings <c>RecapComposer</c> writes.
/// That is a coupling worth naming: the two files agree on those headings, and
/// <c>ApiRecapTests</c> composes a real recap and reads it back through here so that the agreement is
/// checked rather than assumed. The alternative — a second copy of the structured payload on the row
/// — is a migration and an entity this work does not own; see the report.
/// </para>
/// <para>
/// Nothing here adds a verdict. Section 14: <em>it must never say "looks good"</em>, so there is no
/// score, no badge and no pass/fail field for a client to render as one.
/// </para>
/// </remarks>
public static class RecapProjection
{
    private const string SummaryHeading = "1. What changed, and why";
    private const string DeviationsHeading = "2. Where this deviated from the specification";
    private const string CouldNotVerifyHeading = "4. What could not be verified";
    private const string SpecHeading = "The specification, in full";

    private const string SpecSaidMarker = " The specification asked for: ";
    private const string ReasonMarker = " Reason given: ";

    private static readonly JsonSerializerOptions RiskItemJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The recap card, or <c>null</c> when this session has none.
    /// </summary>
    /// <param name="recap">The stored row.</param>
    /// <param name="session">The session it belongs to. Supplies section 7.5's auto-dispatch lead.</param>
    /// <param name="changeRequestUrl">Where the recap was posted, when the provider has a surface.</param>
    /// <param name="changeRequestTerm">What that provider calls a change request. Never assumed.</param>
    public static EngineerRecapResponse? Card(
        Recap? recap,
        Session? session,
        string? changeRequestUrl,
        string? changeRequestTerm)
    {
        if (recap is null)
        {
            return null;
        }

        var body = recap.BodyMd ?? string.Empty;
        var sections = Sections(body);
        var ranked = RiskItems(recap.RiskItems);

        // Section 7.5: an auto-dispatched session had nobody vet its spec, so the card leads with
        // that and carries the specification in full rather than a summary of it.
        var autoDispatched = session?.AutoDispatched ?? false;
        var specMd = autoDispatched ? sections.GetValueOrDefault(SpecHeading) : null;

        return new EngineerRecapResponse
        {
            AutoDispatched = autoDispatched,
            SummaryMd = sections.GetValueOrDefault(SummaryHeading)?.Trim() is { Length: > 0 } summary
                ? summary
                : body.Trim(),
            SpecMd = string.IsNullOrWhiteSpace(specMd) ? null : specMd.Trim(),
            Deviations = Deviations(sections.GetValueOrDefault(DeviationsHeading)),
            Files = [.. ranked.Select(File)],
            CouldNotVerify = Notes(sections.GetValueOrDefault(CouldNotVerifyHeading)),

            // Section 14 part 5: the ranker already decided this and stamped a 1-based position on
            // the items it chose. Reading it back is the only way the card and the change request
            // comment can be the same advice.
            ReviewOrder =
            [
                .. ranked
                    .Where(item => item.ReviewOrder is > 0)
                    .OrderBy(item => item.ReviewOrder!.Value)
                    .Select(item => item.Path),
            ],

            // Absent means there was nowhere to post it and this view is the only copy (section 14).
            PostedToUrl = string.IsNullOrWhiteSpace(changeRequestUrl) ? null : changeRequestUrl,
            PostedToTerm = string.IsNullOrWhiteSpace(changeRequestUrl) || string.IsNullOrWhiteSpace(changeRequestTerm)
                ? null
                : changeRequestTerm,
            GeneratedAt = recap.GeneratedAt,
        };
    }

    /// <summary>
    /// One risk item as pane 3 and the recap card both render it.
    /// </summary>
    /// <remarks>
    /// <c>additions</c> and <c>deletions</c> are zero because <c>risk_items</c> does not carry line
    /// counts. Zero is honest here in the way <see cref="RequestProjection"/> already treats it: a
    /// made-up number in a reviewer's file list is worse than a visibly absent one.
    /// </remarks>
    public static ChangedFileResponse File(RecapRiskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new ChangedFileResponse
        {
            Path = item.Path,
            Additions = 0,
            Deletions = 0,
            Risk = RiskOf(item.Band),
            RiskReasons = item.Reasons.Count == 0 ? null : item.Reasons,
        };
    }

    /// <summary>
    /// The five bands of <c>RecapFileRiskRanker</c> collapsed onto the three the wire has.
    /// </summary>
    /// <remarks>
    /// The client's vocabulary is high / medium / low, and the ranker's is finer. Collapsing loses
    /// the distinction between <em>critical</em> and <em>high</em>, which is why the <em>order</em>
    /// is the ranking that matters and is preserved exactly; the word is a label on top of it.
    /// </remarks>
    public static ApiChangeRisk RiskOf(string? band) => band?.ToLowerInvariant() switch
    {
        "critical" or "high" => ApiChangeRisk.High,
        "moderate" => ApiChangeRisk.Medium,
        _ => ApiChangeRisk.Low,
    };

    /// <summary>The stored risk items, in the order the ranker wrote them.</summary>
    public static IReadOnlyList<RecapRiskItem> RiskItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecapRiskItem>>(json, RiskItemJson) ?? [];
        }
        catch (JsonException)
        {
            // A row written by a build that spelled the payload differently is not a reason to fail
            // the whole request detail. The card renders without a file list and says nothing false.
            return [];
        }
    }

    /// <summary>The markdown body split on the headings <c>RecapComposer</c> writes.</summary>
    private static Dictionary<string, string> Sections(string body)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? heading = null;
        var builder = new StringBuilder();

        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush(sections, heading, builder);
                heading = line[4..].Trim();
                continue;
            }

            if (heading is not null)
            {
                builder.Append(line).Append('\n');
            }
        }

        Flush(sections, heading, builder);
        return sections;
    }

    private static void Flush(Dictionary<string, string> sections, string? heading, StringBuilder builder)
    {
        if (heading is not null)
        {
            sections[heading] = builder.ToString().Trim();
        }

        builder.Clear();
    }

    /// <summary>
    /// Section 14's highest-value section, read back out of the composed bullets.
    /// </summary>
    /// <remarks>
    /// The distinction the type draws — <c>specSaid</c> present or absent — is the one that matters:
    /// <em>"the spec said X and it did Y"</em> and <em>"the spec was silent and it chose Y"</em> need
    /// different amounts of scrutiny. So a bullet with no <em>"The specification asked for:"</em>
    /// clause yields no <c>specSaid</c> key at all rather than an empty string.
    /// </remarks>
    private static IReadOnlyList<RecapDeviationResponse> Deviations(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var deviations = new List<RecapDeviationResponse>();

        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var text = trimmed[2..].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // Right to left, because the composer appends these in order and each one ends the
            // clause before it. Taking the reason off first is what keeps it out of `specSaid`.
            var why = Cut(ref text, ReasonMarker);
            var specSaid = Cut(ref text, SpecSaidMarker);
            var path = TrailingParenthetical(ref text);

            var did = text.Trim().Trim('*').Trim();
            if (did.Length == 0)
            {
                continue;
            }

            deviations.Add(new RecapDeviationResponse
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"deviation-{deviations.Count + 1}"),
                SpecSaid = specSaid,
                AgentDid = why is null ? did : $"{did} Reason given: {why}",
                Path = path,
            });
        }

        return deviations;
    }

    private static IReadOnlyList<RecapNoteResponse> Notes(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var notes = new List<RecapNoteResponse>();

        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var text = trimmed[2..].Trim();
            if (text.Length > 0)
            {
                notes.Add(new RecapNoteResponse
                {
                    Id = string.Create(CultureInfo.InvariantCulture, $"could-not-verify-{notes.Count + 1}"),
                    Text = text,
                });
            }
        }

        return notes;
    }

    /// <summary>Cuts everything from <paramref name="marker"/> onwards off and returns it.</summary>
    private static string? Cut(ref string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var tail = text[(index + marker.Length)..].Trim();
        text = text[..index];

        return tail.Length == 0 ? null : tail.TrimEnd('.');
    }

    /// <summary>The <c>(where)</c> the composer appends after the bold clause, when it is a path.</summary>
    private static string? TrailingParenthetical(ref string text)
    {
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith(')'))
        {
            return null;
        }

        var open = trimmed.LastIndexOf('(');
        if (open <= 0)
        {
            return null;
        }

        var inner = trimmed[(open + 1)..^1].Trim();
        if (inner.Length == 0)
        {
            return null;
        }

        text = trimmed[..open];
        return inner;
    }
}
