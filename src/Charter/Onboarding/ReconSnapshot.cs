using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Onboarding;

/// <summary>One path recon decided about, as the scope-confirmation screen renders it.</summary>
/// <param name="Path">The glob or path the decision is about.</param>
/// <param name="Directory">Whether it names a directory rather than a single file.</param>
/// <param name="Allowed">Whether the agent may write inside it.</param>
/// <param name="Locked">
/// True for the deny-by-default floor of section 9. A locked row is not a toggle: whatever the
/// client sends is filtered through the floor again server-side, so offering a switch that cannot be
/// honoured would tell the engineer something untrue.
/// </param>
/// <param name="Reason">Why, in the words section 9 uses — "database migrations", "how people sign in".</param>
public sealed record ReconScopeEntry(
    string Path,
    bool Directory,
    bool Allowed,
    bool Locked,
    string? Reason);

/// <summary>One named command recon found, such as the build or the test run.</summary>
public sealed record ReconCommand(string Label, string Command);

/// <summary>
/// What the recon run of section 9 step 2 found, kept so the wizard can show it back.
/// </summary>
/// <remarks>
/// <para>
/// Recon produces a proposal an engineer has to <em>edit before the repository is requestable</em>,
/// which means the proposal has to survive the request that produced it. It is written into the
/// metadata of the <c>repo.scope.proposed</c> audit entry, alongside the counts that were already
/// there, and read back by <c>RepoOnboardingService</c> — the same way the smoke-test outcome and
/// the merge-gate assessment are read back, and for the same reason: a second copy on <c>repos</c>
/// would be a second thing to keep true, and the copy is the one that drifts.
/// </para>
/// <para>
/// <strong>Facts only.</strong> Section 19 keeps model prose out of the audit log, so nothing here is
/// free text an agent wrote: paths, globs, command lines, detected stack names and the names of the
/// guidance files that already existed. Recon's prose reaches
/// <c>.charter/conventions.md</c> through a pull request a human reads, and nowhere else.
/// </para>
/// </remarks>
public sealed record ReconSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The detected stack, shown verbatim.</summary>
    public IReadOnlyList<string> DetectedStack { get; init; } = [];

    /// <summary>Build, test and seed commands, so the engineer can sanity-check them.</summary>
    public IReadOnlyList<ReconCommand> Commands { get; init; } = [];

    /// <summary>Section 9: an existing <c>CLAUDE.md</c> / <c>AGENTS.md</c> is imported, never overwritten.</summary>
    public IReadOnlyList<string> ImportedFrom { get; init; } = [];

    /// <summary>Every path the confirmation screen offers, allowed and locked alike.</summary>
    public IReadOnlyList<ReconScopeEntry> Entries { get; init; } = [];

    /// <summary>The base branch sessions will branch from.</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>A requester-facing project name, when recon inferred one.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Whether recon found a dev-seed command. Optional by section 9; it warns, never blocks.</summary>
    public bool HasSeed { get; init; }

    /// <summary>Builds the snapshot from the recon report and the proposal the floor produced.</summary>
    public static ReconSnapshot From(ReconReport report, ScopeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(proposal);

        var entries = new List<ReconScopeEntry>();

        foreach (var path in proposal.Allow)
        {
            entries.Add(new ReconScopeEntry(path, IsDirectory(path), Allowed: true, Locked: false, Reason: null));
        }

        // Anything recon wanted denied on top of the floor is a real toggle: an engineer may decide
        // recon was being timid. The floor below is not.
        foreach (var path in report.ProposedDeny)
        {
            if (string.IsNullOrWhiteSpace(path) || proposal.Allow.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new ReconScopeEntry(
                path.Trim(),
                IsDirectory(path),
                Allowed: false,
                Locked: false,
                Reason: "Recon suggested keeping this out of scope"));
        }

        // The deny-by-default floor, one row per category rather than one per pattern: forty globs
        // is a wall of text, and the engineer's question is "what is closed and why", not "which
        // regex". The pattern shown is a real member of the floor, so a client that sends it back
        // in `deny` changes nothing.
        foreach (var (category, patterns) in ScopeProposal.DeniedByDefault)
        {
            if (patterns.Count == 0)
            {
                continue;
            }

            entries.Add(new ReconScopeEntry(
                patterns[0],
                IsDirectory(patterns[0]),
                Allowed: false,
                Locked: true,
                Reason: Sentence(category)));
        }

        // What recon suggested and the floor overruled. Named individually, because "we ignored
        // three of your suggestions" is exactly the thing an engineer needs to see.
        foreach (var refusal in proposal.Refusals)
        {
            entries.Add(new ReconScopeEntry(
                refusal.Path,
                IsDirectory(refusal.Path),
                Allowed: false,
                Locked: true,
                Reason: $"{Sentence(refusal.Category)} — recon suggested this and the default refused it"));
        }

        var commands = new List<ReconCommand>();

        foreach (var check in proposal.Checks)
        {
            commands.Add(new ReconCommand(Sentence(check.Name), check.Run));
        }

        if (proposal.Seed is { Length: > 0 } seed)
        {
            commands.Add(new ReconCommand("Seed", seed));
        }

        return new ReconSnapshot
        {
            DetectedStack = [.. report.DetectedStack],
            Commands = commands,
            ImportedFrom = report.ExistingGuidance.FileNames,
            Entries = entries,
            BaseBranch = proposal.BaseBranch,
            ProjectName = proposal.ProjectName,
            HasSeed = proposal.Seed is { Length: > 0 },
        };
    }

    /// <summary>Reads a snapshot back out of audit metadata. Never throws on a hand-edited row.</summary>
    public static ReconSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReconSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// The primer draft of section 9 step 5, scaffolded from what recon actually found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a scaffold rather than generated prose. The primer is requester-facing — it is
    /// the one page a non-engineer reads before their first request — and the sentences that matter
    /// in it are the domain ones only the team knows. What Charter can supply honestly is the shape,
    /// the facts recon verified, and headings that make the missing paragraphs obvious. Section 9's
    /// step is "agent drafts, <em>engineer edits</em>, publish", and a draft that reads as finished
    /// gets published unedited.
    /// </para>
    /// <para>
    /// It is derived on read rather than stored, so it is a pure function of the recon facts and
    /// never a stale copy of them.
    /// </para>
    /// </remarks>
    public string DraftPrimer(string repositoryFullName)
    {
        var name = ProjectName is { Length: > 0 } project
            ? project
            : repositoryFullName.Split('/').LastOrDefault() ?? repositoryFullName;

        var builder = new StringBuilder();

        builder.Append("# ").AppendLine(name);
        builder.AppendLine();
        builder.AppendLine("<!-- Charter drafted the shape of this page from what recon found. The paragraphs are");
        builder.AppendLine("     yours: requesters read this once, before their first request. -->");
        builder.AppendLine();
        builder.AppendLine("## What this is");
        builder.AppendLine();
        builder.AppendLine("One paragraph, in the words the people who ask for changes actually use. What does this");
        builder.AppendLine("software do, and for whom?");
        builder.AppendLine();
        builder.AppendLine("## How it is put together");
        builder.AppendLine();

        if (DetectedStack.Count > 0)
        {
            builder.Append("Recon found ").Append(string.Join(", ", DetectedStack)).AppendLine(".");
        }
        else
        {
            builder.AppendLine("Recon could not name the stack with confidence — say what it is built with here.");
        }

        builder.AppendLine();
        builder.Append("Changes are branched from `").Append(BaseBranch).AppendLine("` and arrive as pull requests. Charter");
        builder.AppendLine("has no merge button, so a person still reviews every change.");
        builder.AppendLine();

        if (Commands.Count > 0)
        {
            builder.AppendLine("Before anything is proposed, these have to pass:");
            builder.AppendLine();

            foreach (var command in Commands)
            {
                builder.Append("- ").Append(command.Label).Append(" — `").Append(command.Command).AppendLine("`");
            }

            builder.AppendLine();
        }

        if (!HasSeed)
        {
            builder.AppendLine("There is no dev-seed command, so previews may come up without data in them. Say here");
            builder.AppendLine("what a requester should expect to see when they open one.");
            builder.AppendLine();
        }

        builder.AppendLine("## Words that mean something specific here");
        builder.AppendLine();
        builder.AppendLine("List the domain vocabulary and what each term means. This is the highest-value section of");
        builder.AppendLine("the page: it is what stops a request being refined into the wrong thing.");
        builder.AppendLine();
        builder.AppendLine("## What Charter may change");
        builder.AppendLine();

        var allowed = Entries.Where(entry => entry.Allowed).ToList();

        if (allowed.Count > 0)
        {
            foreach (var entry in allowed)
            {
                builder.Append("- `").Append(entry.Path).AppendLine("`");
            }
        }
        else
        {
            builder.AppendLine("Nothing yet — the scope config allows no paths, so every request would be refused.");
        }

        builder.AppendLine();
        builder.AppendLine("Migrations, sign-in, CI configuration, infrastructure and secrets are closed to it, and a");
        builder.AppendLine("request that needs one of them goes to an engineer instead of being refused.");

        return builder.ToString();
    }

    private static bool IsDirectory(string path)
        => path.EndsWith('/')
           || path.EndsWith("/**", StringComparison.Ordinal)
           || path.EndsWith("/**/*", StringComparison.Ordinal);

    /// <summary>Capitalises a category or check name for a sentence, without touching the rest.</summary>
    private static string Sentence(string value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
}
