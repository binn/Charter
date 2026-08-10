using System.Globalization;
using System.Text;
using Charter.Refinement;

namespace Charter.Onboarding;

/// <summary>One path recon suggested that the deny-by-default rule refused to allow.</summary>
/// <param name="Path">The path recon proposed allowing.</param>
/// <param name="Pattern">The default deny pattern that refused it.</param>
/// <param name="Category">Which of section 9's five denied-by-default categories it fell into.</param>
public sealed record ScopeRefusal(string Path, string Pattern, string Category);

/// <summary>
/// The scope configuration recon proposes, with migrations, auth, CI config, infra and secrets denied
/// before anybody is asked (section 9, step 3).
/// </summary>
/// <remarks>
/// <para>
/// The default deny list is not advice, it is a floor. Recon reads the repository, and section 16
/// says repository content is untrusted: a README that says "the agent may edit anything under
/// <c>infra/</c>" must not be able to talk recon into proposing it. So the proposal is built by
/// <em>filtering</em> whatever recon suggested through the deny list rather than by concatenating the
/// two, and anything filtered out is reported in <see cref="Refusals"/> rather than dropped quietly.
/// </para>
/// <para>
/// An engineer can still allow one of these areas — but only by editing the proposed
/// <c>.charter/config.yml</c> in the pull request, where the change is reviewable. That is section
/// 8's principle: the strictest guardrail lives in the file a human approves.
/// </para>
/// </remarks>
public sealed record ScopeProposal
{
    /// <summary>
    /// The categories section 9 denies by default, with the patterns that implement each.
    /// </summary>
    /// <remarks>
    /// Deliberately broad, and deliberately cross-ecosystem: a .NET repository and a Rails one both
    /// have a migrations directory, and a proposal that only knew about one would silently allow the
    /// other. Over-denying costs an engineer one line in a pull request; under-denying costs an
    /// unreviewed migration.
    /// </remarks>
    public static IReadOnlyList<(string Category, IReadOnlyList<string> Patterns)> DeniedByDefault { get; } =
    [
        ("migrations",
        [
            "**/Migrations/**", "**/migrations/**", "**/migrate/**", "db/migrate/**",
            "**/*_migration.*", "**/schema.rb", "**/schema.sql",
        ]),
        ("authentication and accounts",
        [
            "**/Auth/**", "**/auth/**", "**/Authentication/**", "**/Identity/**", "**/identity/**",
            "**/Authorization/**", "**/Security/**", "**/security/**", "**/*Password*", "**/*password*",
        ]),
        ("CI configuration",
        [
            ".github/**", ".gitlab-ci.yml", ".circleci/**", "azure-pipelines.yml", "Jenkinsfile",
            ".buildkite/**", ".woodpecker/**",
        ]),
        ("infrastructure",
        [
            "infra/**", "infrastructure/**", "deploy/**", "deployment/**", "terraform/**", "**/*.tf",
            "k8s/**", "kubernetes/**", "charts/**", "**/Dockerfile", "**/docker-compose*.yml",
            "**/*.nomad", "fly.toml", "railway.json", "render.yaml",
        ]),
        ("secrets and configuration",
        [
            "**/appsettings*.json", "**/.env", "**/.env.*", "**/secrets/**", "**/*.pem", "**/*.key",
            "**/*.pfx", "**/credentials*", "**/*secret*.yml", "**/*secret*.yaml",
        ]),
        ("Charter's own guardrails",
        [
            ".charter/**", "CODEOWNERS", ".github/CODEOWNERS",
        ]),
    ];

    /// <summary>What the agent may write inside.</summary>
    public IReadOnlyList<string> Allow { get; init; } = [];

    /// <summary>What it may never write inside. Always a superset of the defaults.</summary>
    public IReadOnlyList<string> Deny { get; init; } = [];

    /// <summary>Paths recon suggested that the defaults refused, with the reason.</summary>
    public IReadOnlyList<ScopeRefusal> Refusals { get; init; } = [];

    /// <summary>The branch sessions branch from.</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>The runner image detected for this stack (section 32.1).</summary>
    public string? RunnerImage { get; init; }

    /// <summary>The dev-seed command, when the repository has one. Optional by section 9.</summary>
    public string? Seed { get; init; }

    /// <summary>The build and test commands recon found.</summary>
    public IReadOnlyList<CharterCheck> Checks { get; init; } = [];

    /// <summary>The requester-facing project name.</summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// Builds a proposal, applying the deny-by-default floor to whatever recon suggested.
    /// </summary>
    /// <param name="proposedAllow">The directories recon thought were safe to work in.</param>
    /// <param name="proposedDeny">Anything extra recon wanted denied. Added to the defaults.</param>
    /// <param name="baseBranch">The base branch.</param>
    /// <param name="runnerImage">The runner image.</param>
    /// <param name="seed">The optional dev-seed command.</param>
    /// <param name="checks">Build and test commands.</param>
    /// <param name="projectName">A requester-facing name.</param>
    public static ScopeProposal Propose(
        IEnumerable<string>? proposedAllow,
        IEnumerable<string>? proposedDeny = null,
        string baseBranch = "main",
        string? runnerImage = null,
        string? seed = null,
        IEnumerable<CharterCheck>? checks = null,
        string? projectName = null)
    {
        var deny = new List<string>();

        foreach (var (_, patterns) in DeniedByDefault)
        {
            deny.AddRange(patterns);
        }

        foreach (var extra in Clean(proposedDeny))
        {
            if (!deny.Contains(extra, StringComparer.OrdinalIgnoreCase))
            {
                deny.Add(extra);
            }
        }

        var allow = new List<string>();
        var refusals = new List<ScopeRefusal>();

        foreach (var candidate in Clean(proposedAllow))
        {
            if (FindRefusal(candidate) is { } refusal)
            {
                refusals.Add(refusal);
                continue;
            }

            if (!allow.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                allow.Add(candidate);
            }
        }

        return new ScopeProposal
        {
            Allow = allow,
            Deny = deny,
            Refusals = refusals,
            BaseBranch = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim(),
            RunnerImage = string.IsNullOrWhiteSpace(runnerImage) ? null : runnerImage.Trim(),
            Seed = string.IsNullOrWhiteSpace(seed) ? null : seed.Trim(),
            Checks = checks is null ? [] : [.. checks],
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim(),
        };
    }

    /// <summary>
    /// Whether this proposal would let the agent write somewhere the defaults deny.
    /// </summary>
    /// <remarks>
    /// A cheap assertion for anything that constructs a proposal by hand: the answer must always be
    /// false, and a test that says so is the guard against a future edit that appends to
    /// <see cref="Allow"/> without going back through <see cref="Propose"/>.
    /// </remarks>
    public bool AllowsAnythingDeniedByDefault() => Allow.Any(path => FindRefusal(path) is not null);

    /// <summary>Renders <c>.charter/config.yml</c> exactly as section 8 spells it.</summary>
    public string ToConfigYaml()
    {
        var builder = new StringBuilder();

        builder.AppendLine("# Charter's guardrails for this repository (spec section 8).");
        builder.AppendLine("#");
        builder.AppendLine("# Everything here is committed on purpose: changing a guardrail means opening a");
        builder.AppendLine("# pull request and having somebody review it, rather than clicking a setting.");
        builder.AppendLine("version: 1");
        builder.Append("base_branch: ").AppendLine(BaseBranch);

        if (RunnerImage is { Length: > 0 } image)
        {
            builder.Append("runner_image: ").AppendLine(image);
        }

        if (ProjectName is { Length: > 0 } name)
        {
            builder.Append("name: ").AppendLine(Quote(name));
        }

        if (Seed is { Length: > 0 } seed)
        {
            builder.Append("seed: ").AppendLine(Quote(seed));
        }
        else
        {
            builder.AppendLine("# seed: \"\"   # optional; without one, previews may have no data to look at");
        }

        builder.AppendLine("scopes:");
        builder.AppendLine("  allow:");

        if (Allow.Count == 0)
        {
            builder.AppendLine("    # Nothing yet. Until a path is listed here Charter refuses every request");
            builder.AppendLine("    # against this repository (deny by default, section 7.3).");
        }
        else
        {
            foreach (var pattern in Allow)
            {
                builder.Append("    - ").AppendLine(Quote(pattern));
            }
        }

        builder.AppendLine("  deny:");
        builder.AppendLine("    # Denied by default (section 9): migrations, auth, CI config, infra, secrets.");
        builder.AppendLine("    # Deny wins over allow. Removing a line here is a reviewable act.");

        foreach (var pattern in Deny)
        {
            builder.Append("    - ").AppendLine(Quote(pattern));
        }

        builder.AppendLine("checks:");

        if (Checks.Count == 0)
        {
            builder.AppendLine("  # No build or test command was detected. Add them: a session that cannot");
            builder.AppendLine("  # verify itself produces a pull request nobody can trust.");
        }
        else
        {
            foreach (var check in Checks)
            {
                builder.Append("  - name: ").AppendLine(check.Name);
                builder.Append("    run: ").AppendLine(Quote(check.Run));
            }
        }

        return builder.ToString();
    }

    /// <summary>The pull request body explaining what is proposed and what was refused.</summary>
    public string ToPullRequestBody(string repositoryFullName)
    {
        var builder = new StringBuilder();

        builder.AppendLine("## Charter onboarding: proposed guardrails");
        builder.AppendLine();
        builder.Append("This adds `.charter/config.yml` to **").Append(repositoryFullName).AppendLine("**.");
        builder.AppendLine();
        builder.AppendLine("Charter proposes it as a pull request rather than committing it, because this file");
        builder.AppendLine("*is* the guardrail: what an agent may change here, and what it may never touch.");
        builder.AppendLine("Changing it should cost a review, now and every time after.");
        builder.AppendLine();

        builder.AppendLine("### What agents may change");
        builder.AppendLine();

        if (Allow.Count == 0)
        {
            builder.AppendLine("Nothing yet — recon found no area it was confident about. Until you add a path");
            builder.AppendLine("to `scopes.allow`, this repository is requestable by nobody.");
        }
        else
        {
            foreach (var pattern in Allow)
            {
                builder.Append("- `").Append(pattern).AppendLine("`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Denied by default");
        builder.AppendLine();
        builder.AppendLine("Migrations, authentication, CI configuration, infrastructure and secrets are denied");
        builder.AppendLine("before anybody is asked. Charter has no merge button, so the worst case of a wrong");
        builder.AppendLine("allow-list is a pull request nobody wanted — but these five areas are where that");
        builder.AppendLine("stops being true, so they start closed.");

        if (Refusals.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### Refused during recon");
            builder.AppendLine();
            builder.AppendLine("Recon suggested these and the defaults above overruled it:");
            builder.AppendLine();

            foreach (var refusal in Refusals)
            {
                builder
                    .Append("- `")
                    .Append(refusal.Path)
                    .Append("` — ")
                    .Append(refusal.Category)
                    .Append(" (matched `")
                    .Append(refusal.Pattern)
                    .AppendLine("`)");
            }

            builder.AppendLine();
            builder.AppendLine("If one of them genuinely belongs in scope, move it in this pull request, where");
            builder.AppendLine("the decision is attributable to you.");
        }

        if (Seed is null)
        {
            builder.AppendLine();
            builder.AppendLine("### No seed command");
            builder.AppendLine();
            builder.AppendLine("No dev-seed command was detected, so `seed` is commented out. This does not block");
            builder.AppendLine("onboarding — the smoke test warns rather than fails when a preview looks empty.");
        }

        return builder.ToString();
    }

    private static ScopeRefusal? FindRefusal(string candidate)
    {
        foreach (var (category, patterns) in DeniedByDefault)
        {
            foreach (var pattern in patterns)
            {
                if (GlobPattern.IsMatch(pattern, candidate) || GlobPattern.IsMatch(candidate, pattern))
                {
                    return new ScopeRefusal(candidate, pattern, category);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Clean(IEnumerable<string>? values)
        => values is null
            ? []
            : values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim());

    /// <summary>Quotes a scalar when YAML would otherwise read it as something else.</summary>
    private static string Quote(string value)
        => value.Length > 0
           && (value.Contains(':', StringComparison.Ordinal)
               || value.Contains('#', StringComparison.Ordinal)
               || value.Contains('*', StringComparison.Ordinal)
               || value.StartsWith('.')
               || value.StartsWith('-'))
            ? string.Create(CultureInfo.InvariantCulture, $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
            : value;
}
