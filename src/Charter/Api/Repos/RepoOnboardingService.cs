using System.Globalization;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace Charter.Api.Repos;

/// <summary>
/// The HTTP surface of section 9: connect, recon, scope confirmation, smoke test, primer.
/// </summary>
/// <remarks>
/// <para>
/// Every decision about <em>whether a step may happen</em> stays in
/// <see cref="OnboardingStateMachine"/>, reached through <see cref="OnboardingService"/>. This type
/// checks who is asking, translates, and reads the wizard's progress back — it holds no ordering
/// rules of its own, so there is exactly one place that knows a repository cannot skip from
/// <c>pending</c> to <c>ready</c>.
/// </para>
/// <para>
/// <strong>Progress is read out of the audit log rather than a status column.</strong> The smoke
/// test's outcome and the merge-gate assessment are already written there by
/// <see cref="OnboardingService"/>, attributed to the person who triggered them. A second copy on
/// <c>repos</c> would be a second thing to keep true, and the copy would be the one that drifts.
/// </para>
/// <para>
/// Nothing here is reachable by a requester. Section 7.1 says a requester never sees a repository
/// name, and the way that is kept true is that the endpoints refuse them rather than that the
/// projection hides fields.
/// </para>
/// </remarks>
public sealed class RepoOnboardingService
{
    private readonly CharterDbContext database;
    private readonly OnboardingService onboarding;
    private readonly IRepoScopeAdministration scopes;

    public RepoOnboardingService(
        CharterDbContext database,
        OnboardingService onboarding,
        IRepoScopeAdministration scopes)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(onboarding);
        ArgumentNullException.ThrowIfNull(scopes);

        this.database = database;
        this.onboarding = onboarding;
        this.scopes = scopes;
    }

    /// <summary>Every connected repository. Engineers and admins only (section 7.1).</summary>
    public async Task<(CommandOutcome Outcome, ReposResponse? Repos)> ListAsync(
        MemberSnapshot member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!CanConfigure(member))
        {
            return (Refuse(), null);
        }

        var repos = await database.Repos
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId)
            .OrderBy(row => row.FullName)
            .ToListAsync(cancellationToken);

        return (CommandOutcome.Ok(), new ReposResponse { Repos = [.. repos.Select(Describe)] });
    }

    /// <summary>Connects a repository (section 9, step 1). Admin only, and requestable by nobody.</summary>
    public async Task<(CommandOutcome Outcome, RepoOnboardingResponse? Repo)> ConnectAsync(
        MemberSnapshot member,
        ConnectRepoBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        // Section 7.1: repo connections are an admin column. An engineer configures a repository
        // that is already connected.
        if (!member.HasRole(MemberRole.Admin))
        {
            return (CommandOutcome.Forbidden("connecting a repository is an administrator action"), null);
        }

        var fullName = body.FullName?.Trim();

        if (string.IsNullOrWhiteSpace(fullName) || !fullName.Contains('/', StringComparison.Ordinal))
        {
            return (CommandOutcome.Invalid("Enter the repository as owner/name, for example acme/website."), null);
        }

        if (body.InstallationId is not { } installationId || installationId <= 0)
        {
            return (
                CommandOutcome.Invalid(
                    "Install the Charter GitHub App on this repository first; the installation it "
                    + "creates is what Charter uses to reach the code."),
                null);
        }

        var repo = await onboarding.ConnectAsync(
            member.OrgId,
            installationId,
            fullName,
            string.IsNullOrWhiteSpace(body.BaseBranch) ? "main" : body.BaseBranch.Trim(),
            member.UserId,
            cancellationToken);

        // The one grant a newly connected repository gets: the person who connected it (section
        // 7.3). Everybody else is added deliberately, one row at a time, through SetAccessAsync.
        // This does not make the repository requestable — readiness is a separate condition and it
        // has not been earned — it makes the admin who connected it able to drive the smoke test.
        await scopes.GrantOnConnectAsync(member, repo.Id, cancellationToken);

        return (CommandOutcome.Ok(), await DescribeOnboardingAsync(member, repo, cancellationToken));
    }

    /// <summary>Who may file against this repository (section 7.3, guardrail 1).</summary>
    public async Task<(CommandOutcome Outcome, RepoAccessResponse? Access)> DescribeAccessAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!CanConfigure(member))
        {
            return (Refuse(), null);
        }

        var repo = await FindAsync(member, repoId, cancellationToken);

        if (repo is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var grants = await database.RepoScopes
            .AsNoTracking()
            .Where(row => row.RepoId == repo.Id)
            .OrderBy(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        // One join rather than a lookup per row, and only for the members a grant actually names.
        var namedIds = grants.Where(grant => grant.MemberId.HasValue).Select(grant => grant.MemberId!.Value).ToList();

        var named = await database.Members
            .AsNoTracking()
            .Where(row => row.OrgId == member.OrgId && namedIds.Contains(row.Id))
            .Join(
                database.Users.AsNoTracking(),
                row => row.UserId,
                user => user.Id,
                (row, user) => new { row.Id, user.DisplayName, user.Email })
            .ToListAsync(cancellationToken);

        var people = named.ToDictionary(row => row.Id);

        return (
            CommandOutcome.Ok(),
            new RepoAccessResponse
            {
                Grants =
                [
                    .. grants.Select(grant =>
                    {
                        var person = grant.MemberId is { } id ? people.GetValueOrDefault(id) : null;

                        return new RepoAccessGrantResponse
                        {
                            MemberId = grant.MemberId?.ToString(),
                            MemberName = person?.DisplayName,
                            MemberEmail = person?.Email,
                            Role = grant.Role?.ToApi(),
                            CanRequest = grant.CanRequest,
                        };
                    }),
                ],
                RequesterVisible = repo.IsRequesterVisible,
            });
    }

    /// <summary>
    /// Grants or withholds one member's — or one role's — ability to file against this repository.
    /// </summary>
    /// <remarks>
    /// The write itself is <see cref="IRepoScopeAdministration"/>'s, which is the single place that
    /// can undo deny-by-default and the place that audits it. This method validates the shape of the
    /// request and nothing else.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, RepoAccessResponse? Access)> SetAccessAsync(
        MemberSnapshot member,
        Guid repoId,
        RepoAccessGrantBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(body);

        var hasMember = !string.IsNullOrWhiteSpace(body.MemberId);

        if (hasMember == body.Role.HasValue)
        {
            return (
                CommandOutcome.Invalid("Name either one person or one role, and not both."),
                null);
        }

        var repo = await FindAsync(member, repoId, cancellationToken);

        if (repo is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        AuthorizationDecision decision;

        if (hasMember)
        {
            if (!Guid.TryParse(body.MemberId, CultureInfo.InvariantCulture, out var targetMemberId))
            {
                return (CommandOutcome.NotFound(), null);
            }

            // Section 7.2a: one organisation per instance, but an id out of a request body is still
            // input, so the target is confirmed to be a member of this one before a row is written.
            var belongs = await database.Members
                .AsNoTracking()
                .AnyAsync(
                    row => row.Id == targetMemberId && row.OrgId == member.OrgId,
                    cancellationToken);

            if (!belongs)
            {
                return (CommandOutcome.NotFound(), null);
            }

            decision = await scopes.SetMemberScopeAsync(
                member,
                repo.Id,
                targetMemberId,
                body.CanRequest,
                cancellationToken);
        }
        else
        {
            decision = await scopes.SetRoleScopeAsync(
                member,
                repo.Id,
                body.Role!.Value.ToDomain(),
                body.CanRequest,
                cancellationToken);
        }

        return decision.IsDenied
            ? (CommandOutcome.Forbidden(decision.Reason), null)
            : await DescribeAccessAsync(member, repoId, cancellationToken);
    }

    /// <summary>Where this repository is in section 9, and what the last runs said.</summary>
    public async Task<(CommandOutcome Outcome, RepoOnboardingResponse? Repo)> DescribeAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!CanConfigure(member))
        {
            return (Refuse(), null);
        }

        var repo = await FindAsync(member, repoId, cancellationToken);

        return repo is null
            ? (CommandOutcome.NotFound(), null)
            : (CommandOutcome.Ok(), await DescribeOnboardingAsync(member, repo, cancellationToken));
    }

    /// <summary>Queues the read-only recon run (section 9, step 2).</summary>
    public Task<(CommandOutcome Outcome, OnboardingActionResponse? Result)> StartReconAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
        => StepAsync(
            member,
            repoId,
            repo => onboarding.StartReconAsync(repo.Id, member.UserId, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Confirms the scope, which opens the config pull request and queues the smoke test
    /// (section 9, step 3).
    /// </summary>
    public Task<(CommandOutcome Outcome, OnboardingActionResponse? Result)> ConfirmScopeAsync(
        MemberSnapshot member,
        Guid repoId,
        ConfirmScopeBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        return StepAsync(
            member,
            repoId,
            repo => onboarding.ConfirmScopeAsync(
                repo.Id,
                body.Allow,
                body.Deny,
                member.UserId,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>Publishes the primer an engineer edited (section 9, step 5).</summary>
    public Task<(CommandOutcome Outcome, OnboardingActionResponse? Result)> PublishPrimerAsync(
        MemberSnapshot member,
        Guid repoId,
        PublishPrimerBody body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.Markdown))
        {
            return Task.FromResult<(CommandOutcome, OnboardingActionResponse?)>(
                (CommandOutcome.Invalid("Write the primer before publishing it."), null));
        }

        return StepAsync(
            member,
            repoId,
            repo => onboarding.PublishPrimerAsync(repo.Id, body.Markdown, member.UserId, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// What the last smoke test did (section 9, step 4).
    /// </summary>
    /// <remarks>
    /// A read, never a trigger. The smoke test is queued by confirming the scope and reported by the
    /// execution plane through <see cref="IOnboardingRunCallbacks"/>; an endpoint that could re-run
    /// it on a GET would be a way to spend money with a refresh.
    /// </remarks>
    public async Task<(CommandOutcome Outcome, SmokeTestOutcomeResponse? SmokeTest)> SmokeTestAsync(
        MemberSnapshot member,
        Guid repoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!CanConfigure(member))
        {
            return (Refuse(), null);
        }

        var repo = await FindAsync(member, repoId, cancellationToken);

        if (repo is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var history = await HistoryAsync(member.OrgId, repo.Id, cancellationToken);

        return (CommandOutcome.Ok(), ReadSmokeTest(history));
    }

    private async Task<(CommandOutcome Outcome, OnboardingActionResponse? Result)> StepAsync(
        MemberSnapshot member,
        Guid repoId,
        Func<Repo, Task<OnboardingOutcome>> step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (!CanConfigure(member))
        {
            return (Refuse(), null);
        }

        var repo = await FindAsync(member, repoId, cancellationToken);

        if (repo is null)
        {
            return (CommandOutcome.NotFound(), null);
        }

        var outcome = await step(repo);

        var response = new OnboardingActionResponse
        {
            Status = outcome.Status.ToApi(),
            Explanation = outcome.Explanation,
            Warnings = outcome.Warnings,
            PullRequestNumber = outcome.PullRequestNumber,
            PullRequestUrl = outcome.PullRequestUrl,
        };

        // A refused step is a 409, not a 500: the state machine said this cannot happen from here,
        // and its own words are the explanation (section 11).
        return outcome.Succeeded
            ? (CommandOutcome.Ok(), response)
            : (CommandOutcome.Conflict(outcome.Explanation), response);
    }

    private async Task<RepoOnboardingResponse> DescribeOnboardingAsync(
        MemberSnapshot member,
        Repo repo,
        CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(member.OrgId, repo.Id, cancellationToken);
        var recon = ReadRecon(history);

        return new RepoOnboardingResponse
        {
            Repo = Describe(repo),
            Steps = Steps(repo, history),
            ScopeConfigPullRequest = ReadInt(history, OnboardingAuditActions.ScopeProposed, "pull_request"),
            LastSmokeTest = ReadSmokeTest(history),
            MergeGate = ReadMergeGate(repo, history),
            ProposedScope = recon is null ? null : DescribeProposal(recon),

            // The published primer wins: editing a published page starts from the page, not from the
            // draft it was written over months ago.
            PrimerDraftMd = repo.PrimerMd ?? recon?.DraftPrimer(repo.FullName),
        };
    }

    /// <summary>The recon proposal, from the newest run that recorded one.</summary>
    private static ReconSnapshot? ReadRecon(IReadOnlyList<AuditLog> history)
    {
        foreach (var entry in history.Where(row => row.Action == OnboardingAuditActions.ScopeProposed))
        {
            if (ReconSnapshot.FromJson(Metadata(entry).GetValueOrDefault("recon")) is { } snapshot)
            {
                return snapshot;
            }
        }

        return null;
    }

    private static ScopeProposalResponse DescribeProposal(ReconSnapshot recon) => new()
    {
        DetectedStack = recon.DetectedStack,
        Commands = [.. recon.Commands.Select(command => new ScopeCommandResponse
        {
            Label = command.Label,
            Command = command.Command,
        })],

        // Absent rather than empty: the screen says "imported your AGENTS.md" only when there was
        // one, and an empty array would have it say it about nothing.
        ImportedFrom = recon.ImportedFrom.Count == 0 ? null : recon.ImportedFrom,
        Entries = [.. recon.Entries.Select(entry => new ScopeEntryResponse
        {
            Path = entry.Path,
            Kind = entry.Directory ? "directory" : "file",
            Allowed = entry.Allowed,
            Locked = entry.Locked ? true : null,
            Reason = entry.Reason,
        })],
    };

    private Task<Repo?> FindAsync(MemberSnapshot member, Guid repoId, CancellationToken cancellationToken)
        => database.Repos
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == repoId && row.OrgId == member.OrgId, cancellationToken);

    private async Task<IReadOnlyList<AuditLog>> HistoryAsync(
        Guid orgId,
        Guid repoId,
        CancellationToken cancellationToken)
    {
        var target = repoId.ToString();

        return await database.AuditLogs
            .AsNoTracking()
            .Where(row => row.OrgId == orgId
                && row.TargetType == nameof(Repo)
                && row.TargetId == target)
            .OrderByDescending(row => row.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    private RepoResponse Describe(Repo repo) => new()
    {
        Id = repo.Id.ToString(),
        FullName = repo.FullName,
        BaseBranch = repo.BaseBranch,
        Status = repo.Status.ToApi(),

        // Section 9: readiness is earned, and this is the flag the whole guardrail hangs on.
        RequesterVisible = repo.IsRequesterVisible,
        HasPrimer = !string.IsNullOrWhiteSpace(repo.PrimerMd),
        ConnectedAt = repo.CreatedAt,
        UpdatedAt = repo.UpdatedAt,
    };

    /// <summary>
    /// The wizard, as a checklist rather than a modal (the same argument as section 30.2).
    /// </summary>
    /// <remarks>
    /// Done-ness is derived from the status the repository has actually reached, so an engineer who
    /// closes the tab and comes back tomorrow sees where they are rather than where a client-side
    /// step counter thought they were.
    /// </remarks>
    private static IReadOnlyList<OnboardingStepResponse> Steps(Repo repo, IReadOnlyList<AuditLog> history)
    {
        var reached = Rank(repo.Status);

        var primerDone = !string.IsNullOrWhiteSpace(repo.PrimerMd);
        var mergeGateDone = history.Any(row => row.Action == OnboardingAuditActions.MergeGateChecked);

        var steps = new List<(ApiOnboardingStepId Id, string Label, bool Done)>
        {
            (ApiOnboardingStepId.Connect, "Connect the repository", true),
            (ApiOnboardingStepId.Recon, "Run recon over the repository", reached > Rank(RepoStatus.Recon)),
            (ApiOnboardingStepId.ConfirmScope, "Confirm the scope config", reached > Rank(RepoStatus.Configuring)),
            (ApiOnboardingStepId.SmokeTest, "Pass the smoke test", repo.Status == RepoStatus.Ready),
            (ApiOnboardingStepId.Primer, "Publish the primer", primerDone),
            (ApiOnboardingStepId.MergeGate, "Check the merge gate", mergeGateDone),
        };

        var current = steps.FindIndex(step => !step.Done);

        return
        [
            .. steps.Select((step, index) => new OnboardingStepResponse
            {
                Id = step.Id,
                Label = step.Label,
                Done = step.Done,
                Current = index == current,
            }),
        ];
    }

    /// <summary>How far through section 9 a status is. Disabled is nowhere, by design.</summary>
    private static int Rank(RepoStatus status) => status switch
    {
        RepoStatus.Pending => 1,
        RepoStatus.Recon => 2,
        RepoStatus.Configuring => 3,
        RepoStatus.SmokeTest => 4,
        RepoStatus.Ready => 5,
        _ => 0,
    };

    private static SmokeTestOutcomeResponse? ReadSmokeTest(IReadOnlyList<AuditLog> history)
    {
        var entry = history.FirstOrDefault(row =>
            row.Action == OnboardingAuditActions.RepoReady
            || row.Action == OnboardingAuditActions.SmokeTestFailed);

        if (entry is null)
        {
            return null;
        }

        var metadata = Metadata(entry);
        var passed = entry.Action == OnboardingAuditActions.RepoReady;

        // Section 9's empty-preview warning, rebuilt from the same fact the report carried. It warns
        // and never blocks, so it rides on a passing outcome rather than turning it into a failure.
        var warnings = metadata.ContainsKey("preview_has_data") && !ReadBool(metadata, "preview_has_data")
            ?
            [
                "Preview deployed but appears to have no data — requesters may not be able to "
                + "evaluate changes.",
            ]
            : (IReadOnlyList<string>)[];

        return new SmokeTestOutcomeResponse
        {
            Passed = passed,
            At = entry.CreatedAt,
            PullRequestNumber = ReadInt(metadata, "pull_request"),
            PreviewBound = ReadBool(metadata, "preview_bound"),
            Checkpoints = ReadCheckpoints(metadata),
            Warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    /// <summary>
    /// The six integration points, in the order the loop exercises them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent — not six pending rows — for a run recorded before the six were written down. The
    /// client reconstructs what it can prove from the pull request and the preview binding and marks
    /// the rest unknown, which is the honest answer; six invented ticks would not be.
    /// </para>
    /// <para>
    /// Everything after the first failure is <c>skipped</c> rather than <c>failed</c>. A preview
    /// that was never asked to deploy did not break, and saying it did sends the engineer to the
    /// wrong subsystem.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SmokeTestCheckpointResponse>? ReadCheckpoints(
        IReadOnlyDictionary<string, string?> metadata)
    {
        if (!metadata.ContainsKey("request_filed"))
        {
            return null;
        }

        var points = new (ApiSmokeTestCheckpointId Id, string Label, bool Reached, string? Detail)[]
        {
            (ApiSmokeTestCheckpointId.RequestFiled, "Filed a trivial request",
                ReadBool(metadata, "request_filed"), null),
            (ApiSmokeTestCheckpointId.AgentRan, "The agent ran",
                ReadBool(metadata, "agent_ran"), null),
            (ApiSmokeTestCheckpointId.ChecksPassed, "Your checks passed",
                ReadBool(metadata, "checks_passed"), null),
            (ApiSmokeTestCheckpointId.PullRequest, "A pull request opened",
                ReadBool(metadata, "pull_request_opened"),
                ReadInt(metadata, "pull_request") is { } number
                    ? string.Create(CultureInfo.InvariantCulture, $"Pull request #{number}.")
                    : null),
            (ApiSmokeTestCheckpointId.PreviewDeployed, "A preview deployed",
                ReadBool(metadata, "preview_deployed"),
                metadata.ContainsKey("preview_has_data") && !ReadBool(metadata, "preview_has_data")
                    ? "It came up with no data in it."
                    : null),
            (ApiSmokeTestCheckpointId.UrlBound, "The preview URL bound back",
                ReadBool(metadata, "preview_bound"), null),
        };

        var broken = false;

        return
        [
            .. points.Select(point =>
            {
                var state = point.Reached
                    ? ApiSmokeTestCheckpointState.Passed
                    : broken
                        ? ApiSmokeTestCheckpointState.Skipped
                        : ApiSmokeTestCheckpointState.Failed;

                if (!point.Reached)
                {
                    broken = true;
                }

                return new SmokeTestCheckpointResponse
                {
                    Id = point.Id,
                    Label = point.Label,
                    State = state,
                    Detail = state == ApiSmokeTestCheckpointState.Skipped ? "Never reached." : point.Detail,
                };
            }),
        ];
    }

    private static MergeGateResponse? ReadMergeGate(Repo repo, IReadOnlyList<AuditLog> history)
    {
        var entry = history.FirstOrDefault(row => row.Action == OnboardingAuditActions.MergeGateChecked);

        if (entry is null)
        {
            return null;
        }

        var metadata = Metadata(entry);
        var enforcement = metadata.GetValueOrDefault("enforcement") ?? "advisory";
        var configured = ReadBool(metadata, "protection_configured");
        var requiresReview = ReadBool(metadata, "requires_review");
        var branch = metadata.GetValueOrDefault("branch") ?? repo.BaseBranch;

        return new MergeGateResponse
        {
            Enforcement = enforcement,
            Branch = branch,
            ProtectionConfigured = configured,
            RequiresReview = requiresReview,
            CheckedAt = entry.CreatedAt,

            // Change spec 001 part A.5: an advisory gate is stated plainly rather than left to be
            // inferred from a flag nobody reads.
            Warning = string.Equals(enforcement, "provider_enforced", StringComparison.Ordinal)
                ? null
                : $"No enforced review gate covers '{branch}'. Charter will not merge, and it cannot "
                  + "stop anyone else from doing so.",
        };
    }

    private static int? ReadInt(IReadOnlyList<AuditLog> history, string action, string key)
    {
        var entry = history.FirstOrDefault(row => row.Action == action);

        return entry is null ? null : ReadInt(Metadata(entry), key);
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string?> metadata, string key)
        => metadata.GetValueOrDefault(key) is { Length: > 0 } value
           && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool ReadBool(IReadOnlyDictionary<string, string?> metadata, string key)
        => bool.TryParse(metadata.GetValueOrDefault(key), out var parsed) && parsed;

    private static IReadOnlyDictionary<string, string?> Metadata(AuditLog entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Metadata))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(entry.Metadata)
                   ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Metadata is written by Charter, but it is jsonb and a hand-edited row must not take
            // the onboarding screen down.
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    /// <summary>Section 7.1: engineers configure repos and scopes; admins own the connection.</summary>
    private static bool CanConfigure(MemberSnapshot member)
        => member.HasAnyRole(MemberRole.Engineer, MemberRole.Admin);

    private static CommandOutcome Refuse()
        => CommandOutcome.Forbidden("repository onboarding belongs to engineers and administrators");
}
