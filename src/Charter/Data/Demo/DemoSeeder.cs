using Charter.Api;
using Charter.Auth;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Domain;
using Charter.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace Charter.Data.Demo;

/// <summary>What a seeding attempt found and did.</summary>
public enum DemoSeedOutcome
{
    /// <summary>The database was empty and now holds the demonstration data.</summary>
    Seeded,

    /// <summary>
    /// The demonstration data is already here, from an earlier boot. Nothing was written, and the
    /// seeded accounts still sign in.
    /// </summary>
    AlreadySeeded,

    /// <summary>
    /// The database holds something that is not the demonstration data, so nothing was written.
    /// </summary>
    Occupied,
}

/// <summary>One seeded sign-in, as the startup banner needs to describe it.</summary>
/// <param name="Email">The address to sign in with.</param>
/// <param name="DisplayName">The person's name, as the instance shows it.</param>
/// <param name="Roles">The section 7.1 roles the member holds.</param>
/// <param name="Sees">One line on what this account's view is for.</param>
public sealed record DemoAccount(string Email, string DisplayName, string Roles, string Sees);

/// <summary>
/// The fake organisation, repository and completed sessions of section 30.6.
/// </summary>
/// <remarks>
/// <para>
/// Section 30.6 states the reason plainly: for an open-source project the gap between
/// <c>docker compose up</c> and understanding what the thing does is the single biggest adoption
/// cliff. Every screen in Charter is a list of things that have happened, so an instance with an
/// empty database demonstrates only its own empty states. This writes one plausible week of history
/// - a repository that finished onboarding, two sessions that ran to completion with their
/// transcripts, verification artifacts and engineer recaps, and one request still being refined -
/// so the three panes, the status thread and the artifact card all have something real to render.
/// </para>
/// <para>
/// It writes ordinary rows through the ordinary domain factories. There is no demo column, no
/// <c>is_fake</c> flag and no branch anywhere else in the application: the same rule as section 7.2's
/// personal mode, for the same reason - a second code path is the one that stops being tested.
/// </para>
/// <para>
/// Nothing here needs schema that <c>InitialCreate</c> does not already have, which is deliberate. A
/// demonstration feature is a poor reason to touch a migration.
/// </para>
/// <para>
/// The two accounts are the point of the exercise, not a convenience. Charter's claim is that a
/// requester and an engineer see genuinely different instances of the same request, and that the
/// difference is server-side omission rather than a hidden div (section 7.4). One account cannot
/// demonstrate that; two, signed into in turn, demonstrate it in about a minute. They authenticate
/// through <see cref="PasswordIdentityProvider"/> like every other account - the seeder writes a
/// PBKDF2 hash through <see cref="ICharterPasswordHasher"/> and nothing else, so there is no demo
/// branch in sign-in, no magic address and no short-circuited permission check.
/// </para>
/// </remarks>
public sealed class DemoSeeder(CharterDbContext database, TimeProvider time, ICharterPasswordHasher hasher)
{
    /// <summary>The organisation the demo data hangs from.</summary>
    public const string OrganizationName = "Northwind Coffee";

    /// <summary>The repository the demo requests are filed against.</summary>
    public const string RepositoryFullName = "northwind-coffee/storefront";

    /// <summary>The admin, engineer and approver account.</summary>
    public const string EngineerEmail = "ada@northwind.example";

    /// <summary>The requester account, whose view is the one section 30.4 cares about.</summary>
    public const string RequesterEmail = "priya@northwind.example";

    /// <summary>
    /// The password both seeded accounts sign in with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published, and deliberately so: it is printed at startup and written in the documentation,
    /// because an evaluator who has to read source to get past the sign-in page has already been lost.
    /// Anything unguessable would have to be generated, and a generated secret that changes on every
    /// container restart cannot appear in a getting-started page at all.
    /// </para>
    /// <para>
    /// What makes that safe is not the string, which is public, but where it can exist. These accounts
    /// are only ever written into a database with nothing in it (see <see cref="SeedAsync"/>), so a
    /// well-known password can never appear on an instance that holds real work. The residual risk is
    /// an operator who evaluates on a reachable host and then keeps the database, which is what the
    /// startup banner warns about in as many words.
    /// </para>
    /// </remarks>
    public const string Password = "charter-demo-password";

    /// <summary>The seeded sign-ins, in the order an evaluator should try them.</summary>
    /// <remarks>
    /// Requester first. Section 7.4's difference only reads as a difference if the narrower view is
    /// the one seen first; starting as the admin makes the requester's view look like a broken page
    /// rather than a deliberate one.
    /// </remarks>
    public static IReadOnlyList<DemoAccount> Accounts { get; } =
    [
        new DemoAccount(
            RequesterEmail,
            "Priya Raman",
            "Requester",
            "the plain-language view: status thread, previews, no repo name, branch, diff or token count"),
        new DemoAccount(
            EngineerEmail,
            "Ada Okafor",
            "Admin, Engineer, Approver",
            "the three-pane view: transcripts, diffs, recaps, cost, members and the audit log"),
    ];

    /// <summary>
    /// Writes the demo data, unless this database already holds something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard is "any organisation or any user exists", not "the demo organisation exists".
    /// Section 7.2a gives an instance exactly one organisation, so a database with one is either
    /// already seeded or already in real use, and in both cases writing a second one would be wrong.
    /// The user half of the condition is the security-relevant one: an instance that has been claimed
    /// through section 30.1 but has not named its organisation yet must not acquire two accounts with
    /// a published password. This makes the seeder safe to run on every boot, which is the only way
    /// it can be a hosted service.
    /// </para>
    /// <para>
    /// The distinction between <see cref="DemoSeedOutcome.AlreadySeeded"/> and
    /// <see cref="DemoSeedOutcome.Occupied"/> exists so the caller knows whether the printed
    /// credentials are true. On a restart they still are; over somebody's real data they are not, and
    /// printing them would be an invitation to try them.
    /// </para>
    /// </remarks>
    public async Task<DemoSeedOutcome> SeedAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(hasher);

        if (await database.Organizations.AnyAsync(cancellationToken).ConfigureAwait(false)
            || await database.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            // "Is this our own data?" is asked of the users rather than the organisation name, because
            // the organisation is renameable from the settings page and the account identities are not.
            var mine = await database.Users
                .CountAsync(user => user.Email == RequesterEmail || user.Email == EngineerEmail, cancellationToken)
                .ConfigureAwait(false);

            return mine == 2 ? DemoSeedOutcome.AlreadySeeded : DemoSeedOutcome.Occupied;
        }

        var now = time.GetUtcNow();

        var organization = Organization.Create(OrganizationName, OrganizationMode.Organization, now.AddDays(-38));
        database.Organizations.Add(organization);

        var engineer = User.Create(EngineerEmail, "Ada Okafor", TeachingLevel.JustTheDecisions, now.AddDays(-38));
        var requester = User.Create(RequesterEmail, "Priya Raman", TeachingLevel.ExplainEverything, now.AddDays(-31));
        requester.CompleteRequesterOnboarding(now.AddDays(-31));
        database.Users.AddRange(engineer, requester);

        // Hashed once per account rather than once for both: the password is shared and public, but a
        // shared salt would be a habit worth not forming in code somebody will copy.
        database.Identities.AddRange(
            PasswordIdentityProvider.NewPasswordIdentity(engineer.Id, HashDemoPassword(), now.AddDays(-38)),
            PasswordIdentityProvider.NewPasswordIdentity(requester.Id, HashDemoPassword(), now.AddDays(-31)));

        var engineerMember = Member.Create(
            organization.Id,
            engineer.Id,
            [MemberRole.Admin, MemberRole.Engineer, MemberRole.Approver],
            [MemberCapability.CanCreateRepo],
            now.AddDays(-38));

        var requesterMember = Member.Create(
            organization.Id,
            requester.Id,
            [MemberRole.Requester],
            now: now.AddDays(-31));

        database.Members.AddRange(engineerMember, requesterMember);

        var repo = SeedRepository(organization.Id, now);

        // Section 7.3, guardrail 1: deny by default, so a repository is requestable only because a
        // grant says so. The demo instance shows the grants rather than implying repos are open.
        //
        // Both roles are granted, and the engineer's grant is not a courtesy. Deny by default applies
        // to everyone, so without it Ada holds three roles and can still file nothing: her project
        // list is empty, `canFileRequests` is false, and an evaluator signed in as the administrator
        // sees an instance that looks broken rather than one that looks locked down.
        database.RepoScopes.AddRange(
            RepoScope.ForRole(repo.Id, MemberRole.Requester, canRequest: true, now.AddDays(-34)),
            RepoScope.ForRole(repo.Id, MemberRole.Engineer, canRequest: true, now.AddDays(-34)));

        SeedBudgets(organization.Id, requester.Id, now);

        SeedExportSession(organization, repo, requester, engineer, now);
        SeedCheckoutSession(organization, repo, requester, engineer, now);
        SeedRefiningRequest(organization, repo, requester, now);

        SeedAuditTrail(organization.Id, repo, engineer, requester, now);

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DemoSeedOutcome.Seeded;
    }

    /// <summary>The budgets every seeded cost is booked against. Set before the sessions are written.</summary>
    private Guid[] budgetIds = [];

    /// <summary>
    /// The stored verifier for <see cref="Password"/>, produced by the same hasher sign-in verifies
    /// against.
    /// </summary>
    private string HashDemoPassword() => hasher.Hash(new Secret(Password));

    /// <summary>
    /// The section 34 spend gate, with something in it.
    /// </summary>
    /// <remarks>
    /// An org cap and one per-person cap, which is the smallest pair that shows the two questions
    /// budgets answer - "how much does this organisation spend" and "how much may this person spend
    /// without asking". <see cref="BudgetBehaviour.RequireApproval"/> rather than
    /// <see cref="BudgetBehaviour.Block"/>, for the section 7.5 reason: the merge gate is immovable,
    /// so the worst case above a spend cap is a decision, not a refusal.
    /// </remarks>
    private void SeedBudgets(Guid organizationId, Guid requesterId, DateTimeOffset now)
    {
        var organizationBudget = Budget.Create(
            organizationId,
            "Engineering, monthly",
            BudgetScopeType.Org,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 400m,
            BudgetBehaviour.RequireApproval,
            approvalThreshold: 5m,
            now: now.AddDays(-37));

        var requesterBudget = Budget.Create(
            organizationId,
            "Priya Raman, monthly",
            BudgetScopeType.User,
            LedgerUnit.Usd,
            BudgetPeriod.Monthly,
            amount: 60m,
            BudgetBehaviour.RequireApproval,
            scopeId: requesterId.ToString(),
            approvalThreshold: 3m,
            now: now.AddDays(-31));

        database.Budgets.AddRange(organizationBudget, requesterBudget);
        budgetIds = [organizationBudget.Id, requesterBudget.Id];
    }

    /// <summary>
    /// Books a settled cost against the budgets, so the ledger and the session agree.
    /// </summary>
    /// <remarks>
    /// Reserved and then settled rather than written straight to settled, because that is the only
    /// sequence section 34.4 allows and the only one the domain will accept. A demo whose ledger were
    /// empty would show every budget at zero next to sessions that plainly cost money, which reads as
    /// a bug in the accounting rather than as a gap in the fixture.
    /// </remarks>
    private void SeedLedgerEntry(
        Guid organizationId,
        Guid userId,
        Guid sessionId,
        LedgerCategory category,
        decimal usd,
        DateTimeOffset at)
    {
        var entry = LedgerEntry.ReserveUsd(
            organizationId,
            userId,
            category,
            usd,
            budgetIds,
            sessionId,
            now: at);

        entry.Settle(usd, quotaSessions: 0m, imputedUsd: 0m, at.AddMinutes(20));
        database.LedgerEntries.Add(entry);
    }

    /// <summary>
    /// The week's history as the audit log tells it (section 7.3, guardrail 5).
    /// </summary>
    /// <remarks>
    /// Every entry is attributable to a named human, which is the guarantee the audit page exists to
    /// make visible. Without these rows the page renders only the evaluator's own sign-in - true, and
    /// the least informative possible answer to "what has this instance done".
    /// </remarks>
    private void SeedAuditTrail(
        Guid organizationId,
        Repo repo,
        User engineer,
        User requester,
        DateTimeOffset now)
    {
        var repository = $$"""{"full_name":"{{RepositoryFullName}}"}""";
        var repoId = repo.Id.ToString();

        Record(AuditActions.SetupCompleted, "organization", engineer.Id, organizationId.ToString(), null, -38);
        Record(OnboardingAuditActions.RepoConnected, "repo", engineer.Id, repoId, repository, -36);
        Record(OnboardingAuditActions.ReconStarted, "repo", engineer.Id, repoId, repository, -36);
        Record(OnboardingAuditActions.ScopeProposed, "repo", null, repoId, repository, -35);
        Record(OnboardingAuditActions.ScopeConfirmed, "repo", engineer.Id, repoId, repository, -35);
        Record(OnboardingAuditActions.RepoReady, "repo", null, repoId, repository, -34);
        Record(OnboardingAuditActions.PrimerPublished, "repo", engineer.Id, repoId, repository, -34);

        Record(
            AuditActions.MemberInvited,
            "user",
            engineer.Id,
            requester.Id.ToString(),
            $$"""{"member_email":"{{RequesterEmail}}","role":"requester"}""",
            -32);

        Record(AuditActions.MemberInviteAccepted, "user", requester.Id, requester.Id.ToString(), null, -31);
        Record(AuditActions.RepoScopeGranted, "repo", engineer.Id, repoId, repository, -34);

        // The spend gate, twice, on the two requests that were built (section 7.5).
        Record(AuditActions.SpecApproved, "session", engineer.Id, null, null, -9);
        Record(ApiAuditActions.SessionApproved, "session", engineer.Id, null, null, -8);
        Record(AuditActions.SpecApproved, "session", engineer.Id, null, null, -2);

        void Record(
            string action,
            string targetType,
            Guid? actor,
            string? targetId,
            string? metadata,
            int daysAgo)
            => database.AuditLogs.Add(AuditLog.Record(
                organizationId,
                action,
                targetType,
                actor,
                targetId,
                metadata,
                now.AddDays(daysAgo)));
    }

    private Repo SeedRepository(Guid organizationId, DateTimeOffset now)
    {
        var repo = Repo.Connect(organizationId, githubInstallationId: 48211903, RepositoryFullName, "main", now.AddDays(-36));

        // Section 9: readiness is earned. A repo the smoke test never passed is invisible to
        // requesters, and a demo that showed one would be showing the wrong thing.
        repo.TransitionTo(RepoStatus.Ready, now.AddDays(-34));
        repo.PublishPrimer(
            """
            # Northwind Coffee storefront

            The public shop. Customers browse the range, build a basket and check out; a small
            admin area lets the roastery edit products and read orders.

            It is a Next.js front end talking to a Rails API, with Postgres behind it and Stripe
            for payments. Orders, subscriptions and the loyalty scheme all live in the API.

            Changes here go live to customers, so every request runs the checkout smoke test
            before anyone is asked to look at a preview.
            """,
            now.AddDays(-34));

        repo.RecordConfigSnapshot(
            """
            {"scopes":{"allow":["app/**","components/**","api/app/**"],"deny":["api/app/models/payment_*.rb","infra/**",".github/**"]},"project_type":"web","verification":{"kind":"hosted_preview"}}
            """,
            now.AddDays(-34));

        database.Repos.Add(repo);
        return repo;
    }

    /// <summary>The finished, merged request: filed, refined, approved, built, reviewed, shipped.</summary>
    private void SeedExportSession(
        Organization organization,
        Repo repo,
        User requester,
        User engineer,
        DateTimeOffset now)
    {
        var filed = now.AddDays(-9);

        var request = Request.File(
            organization.Id,
            repo.Id,
            requester.Id,
            "People keep emailing us asking for a list of everything they've ordered, and we're "
            + "copying it out of the admin by hand. Could they download it themselves from their "
            + "account page?",
            now: filed);

        request.TransitionTo(RequestStatus.Merged, filed.AddDays(1).AddHours(3));
        database.Requests.Add(request);

        var spec = Spec.Draft(
            request.Id,
            version: 1,
            "Let customers download their own order history",
            "On your account page there is a new Download order history button. It gives you a "
            + "spreadsheet of every order you have placed, with the date, the items and what you paid.",
            """
            ## What changes

            The account page gains a **Download order history** button, next to the existing
            address book section. Pressing it produces a CSV containing one row per order line:
            order number, date placed, product, quantity, unit price and total.

            ## Why now

            Support currently answers this by hand out of the admin, roughly a dozen times a week.

            ## Boundaries

            Only the signed-in customer's own orders. No admin-facing export, no scheduled email,
            no PDF.
            """,
            """
            ["A signed-in customer sees a Download order history button on their account page.","The downloaded file opens in a spreadsheet and lists every order that customer has placed.","Each row shows the order date, the product, the quantity and the price paid.","A customer with no orders gets a file with only its header row, and no error.","No customer can download another customer's orders."]
            """,
            technicalApproach:
            "New `GET /api/account/orders.csv` on the Rails side, scoped through `current_customer` "
            + "rather than by an id in the path. Streams with `ActionController::Live` so a long "
            + "history does not buffer. The front end links to it; no client-side generation.",
            scope: """
            {"files":["app/account/page.tsx","api/app/controllers/account/orders_controller.rb","api/config/routes.rb"],"paths":["app/**","api/app/**"]}
            """,
            risks: """
            ["The export reads the orders table directly; a customer with thousands of orders makes a slow query.","CSV injection if a product name begins with '=' - values are prefixed defensively."]
            """,
            now: filed.AddHours(2));

        spec.Approve(engineer.Id, filed.AddHours(5));
        database.Specs.Add(spec);

        var session = Session.Queue(
            spec.Id,
            RunnerKind.GitHubActions,
            "anthropic/claude-opus-5",
            now: filed.AddHours(5));

        session.Start("6f2a1c94d0b7e35a8c1f4d2e9b60a7c3d5e81f42", filed.AddHours(5).AddMinutes(1));
        session.AddCost(1.86m);
        session.TransitionTo(SessionStatus.Merged, filed.AddDays(1).AddHours(3));
        database.Sessions.Add(session);

        // Booked to the requester, not the engineer: section 34 governs whose budget a request
        // spends, and it is the person who asked for it.
        SeedLedgerEntry(organization.Id, requester.Id, session.Id, LedgerCategory.Build, 1.86m, filed.AddHours(5));
        SeedLedgerEntry(organization.Id, requester.Id, session.Id, LedgerCategory.Recap, 0.09m, filed.AddDays(1));

        var events = AppendTranscript(session.Id, filed.AddHours(5).AddMinutes(1),
        [
            (EventTypes.SessionStarted, """{"adapter":"claude-code","model":"anthropic/claude-opus-5","base_sha":"6f2a1c94"}"""),
            (EventTypes.Message, """{"role":"assistant","text":"Reading the account page and the orders API to see how a customer's orders are already fetched."}"""),
            (EventTypes.ToolUse, """{"tool":"grep","args":{"pattern":"current_customer","path":"api/app"},"result_lines":31}"""),
            (EventTypes.Message, """{"role":"assistant","text":"Orders are already scoped through current_customer in the orders controller, so the export can reuse that scope rather than taking an id."}"""),
            (EventTypes.FileWrite, """{"path":"api/app/controllers/account/orders_controller.rb","added":34,"removed":2}"""),
            (EventTypes.FileWrite, """{"path":"api/config/routes.rb","added":1,"removed":0}"""),
            (EventTypes.FileWrite, """{"path":"app/account/page.tsx","added":18,"removed":1}"""),
            (EventTypes.Command, """{"command":"bundle exec rspec spec/requests/account/orders_spec.rb","cwd":"api"}"""),
            (EventTypes.CheckResult, """{"check":"rspec","status":"passed","tests":14,"failures":0,"duration_ms":9120}"""),
            (EventTypes.Command, """{"command":"npm run test -- account","cwd":"."}"""),
            (EventTypes.CheckResult, """{"check":"vitest","status":"passed","tests":6,"failures":0,"duration_ms":3410}"""),
            (EventTypes.Message, """{"role":"assistant","text":"Product names are written into the CSV with a leading apostrophe when they start with =, +, - or @, so a spreadsheet does not evaluate them as formulas."}"""),
            (EventTypes.Cost, """{"input_tokens":184120,"output_tokens":11840,"cost_usd":1.86}"""),
            (EventTypes.SessionEnded, """{"outcome":"pr_open","pull_request":482}"""),
        ]);

        database.Milestones.AddRange(
            Milestone.Promote(session.Id, events[1].Id, MilestoneLabel.UnderstandingSetup, events[1].CreatedAt),
            Milestone.Promote(session.Id, events[4].Id, MilestoneLabel.MakingChanges, events[4].CreatedAt),
            Milestone.Promote(session.Id, events[8].Id, MilestoneLabel.CheckingItWorks, events[8].CreatedAt),
            Milestone.Promote(session.Id, events[13].Id, MilestoneLabel.PuttingItTogether, events[13].CreatedAt));

        var change = ChangeRequest.Open(
            session.Id,
            number: 482,
            url: "https://github.com/" + RepositoryFullName + "/pull/482",
            headSha: "b91d7c4e2f08a5361d9c7be04a23e5187fd60c9a",
            state: ChangeRequestState.Merged,
            now: filed.AddHours(7),
            headBranch: "charter/order-history-export",
            authorLogin: "northwind-charter[bot]");

        database.ChangeRequests.Add(change);

        var artifact = VerificationArtifact.Pending(
            session.Id,
            VerificationArtifactKind.HostedPreview,
            VerificationArtifactAudience.Requester,
            filed.AddHours(7));

        artifact.MarkReady(
            url: "https://storefront-pr-482.preview.northwind.example",
            instructionsMd:
            "Sign in as **demo@northwind.example**, open **Account**, and press **Download order "
            + "history**. The file should list the four orders on the demo account.",
            expiresAt: filed.AddDays(4),
            payload: """{"kind":"hosted_preview","reachable":true,"checked_at":"2026-08-01T09:14:00Z"}""");

        database.VerificationArtifacts.Add(artifact);

        database.Recaps.Add(Recap.Generate(
            session.Id,
            """
            ## What this session did

            Added a customer-facing CSV export of order history: one new streaming endpoint, one
            route, and a button on the account page.

            ## Where to look first

            1. `api/app/controllers/account/orders_controller.rb` — the new `export` action. The
               scope comes from `current_customer`; there is no id in the path to tamper with.
            2. `app/account/page.tsx` — the button, which is a plain link to the endpoint.
            3. `api/config/routes.rb` — one line.

            ## Deviations from the specification

            None.

            ## What could not be verified here

            The query is not paginated. The test data has four orders; behaviour against a customer
            with several thousand has not been observed.
            """,
            """
            [{"path":"api/app/controllers/account/orders_controller.rb","risk":"high","why":"new externally reachable endpoint returning customer data"},{"path":"api/config/routes.rb","risk":"medium","why":"routing change"},{"path":"app/account/page.tsx","risk":"low","why":"presentational"}]
            """,
            costUsd: 0.09m,
            now: filed.AddDays(1),
            payloadJson: """
            {"summary":"Customer-facing CSV export of order history.","review_order":["api/app/controllers/account/orders_controller.rb","api/config/routes.rb","app/account/page.tsx"],"deviations":[],"unverified":["Performance against a customer with thousands of orders."]}
            """));
    }

    /// <summary>The request waiting on a human: built, preview up, nobody has clicked it yet.</summary>
    private void SeedCheckoutSession(
        Organization organization,
        Repo repo,
        User requester,
        User engineer,
        DateTimeOffset now)
    {
        var filed = now.AddDays(-2);

        var request = Request.File(
            organization.Id,
            repo.Id,
            requester.Id,
            "On my phone the checkout page takes ages before I can type anything into the card box. "
            + "On my laptop it's fine. Two customers have mentioned it this week.",
            now: filed);

        request.TransitionTo(RequestStatus.PreviewReady, filed.AddHours(6));
        database.Requests.Add(request);

        var spec = Spec.Draft(
            request.Id,
            version: 1,
            "Make the checkout page usable sooner on a phone",
            "The checkout page becomes ready to type into much sooner on a phone. Nothing about it "
            + "looks different; it just stops being unresponsive for the first few seconds.",
            """
            ## What changes

            The checkout route currently loads the whole product-recommendation bundle before it
            renders. That bundle is not used anywhere on the checkout page. It is moved behind a
            dynamic import so it is fetched only where it is shown.

            ## Boundaries

            No visual change, no change to the payment flow, and no change to what is charged.
            """,
            """
            ["The checkout page accepts typing in the card field noticeably sooner on a mid-range phone.","Nothing on the checkout page looks different from how it looks today.","Paying still works, with a test card, end to end.","The recommendation carousel on the product page still appears."]
            """,
            technicalApproach:
            "`next/dynamic` around `RecommendationRail`, and drop the eager import from the "
            + "checkout layout. Lighthouse mobile trace before and after, captured on the preview.",
            scope: """
            {"files":["app/checkout/layout.tsx","components/RecommendationRail.tsx"],"paths":["app/**","components/**"]}
            """,
            risks: """
            ["The rail is also imported by the product page; a careless change removes it from both."]
            """,
            now: filed.AddHours(1));

        spec.Approve(engineer.Id, filed.AddHours(2));
        database.Specs.Add(spec);

        var session = Session.Queue(
            spec.Id,
            RunnerKind.GitHubActions,
            "anthropic/claude-opus-5",
            now: filed.AddHours(2));

        session.Start("d40b8e17ac6592f3b8071ea45cd9236f8b1a0e77", filed.AddHours(2).AddMinutes(2));
        session.AddCost(0.94m);
        session.TransitionTo(SessionStatus.PreviewReady, filed.AddHours(6));
        database.Sessions.Add(session);

        SeedLedgerEntry(organization.Id, requester.Id, session.Id, LedgerCategory.Build, 0.94m, filed.AddHours(2));
        SeedLedgerEntry(organization.Id, requester.Id, session.Id, LedgerCategory.Recap, 0.06m, filed.AddHours(6));

        var events = AppendTranscript(session.Id, filed.AddHours(2).AddMinutes(2),
        [
            (EventTypes.SessionStarted, """{"adapter":"claude-code","model":"anthropic/claude-opus-5","base_sha":"d40b8e17"}"""),
            (EventTypes.Message, """{"role":"assistant","text":"Looking at what the checkout route imports before it can render."}"""),
            (EventTypes.ToolUse, """{"tool":"read","args":{"path":"app/checkout/layout.tsx"},"bytes":2841}"""),
            (EventTypes.Message, """{"role":"assistant","text":"The layout imports RecommendationRail eagerly. Nothing on the checkout page renders it, so it is pure download cost on this route."}"""),
            (EventTypes.FileWrite, """{"path":"app/checkout/layout.tsx","added":3,"removed":4}"""),
            (EventTypes.FileWrite, """{"path":"components/RecommendationRail.tsx","added":2,"removed":0}"""),
            (EventTypes.Command, """{"command":"npm run build","cwd":"."}"""),
            (EventTypes.CheckResult, """{"check":"next build","status":"passed","route":"/checkout","first_load_kb_before":412,"first_load_kb_after":188}"""),
            (EventTypes.Command, """{"command":"npx playwright test checkout.spec.ts","cwd":"."}"""),
            (EventTypes.CheckResult, """{"check":"playwright","status":"passed","tests":5,"failures":0,"duration_ms":24380}"""),
            (EventTypes.Cost, """{"input_tokens":96240,"output_tokens":5310,"cost_usd":0.94}"""),
            (EventTypes.SessionEnded, """{"outcome":"preview_ready","pull_request":486}"""),
        ]);

        database.Milestones.AddRange(
            Milestone.Promote(session.Id, events[1].Id, MilestoneLabel.UnderstandingSetup, events[1].CreatedAt),
            Milestone.Promote(session.Id, events[4].Id, MilestoneLabel.MakingChanges, events[4].CreatedAt),
            Milestone.Promote(session.Id, events[7].Id, MilestoneLabel.CheckingItWorks, events[7].CreatedAt),
            Milestone.Promote(session.Id, events[11].Id, MilestoneLabel.PuttingItTogether, events[11].CreatedAt));

        database.ChangeRequests.Add(ChangeRequest.Open(
            session.Id,
            number: 486,
            url: "https://github.com/" + RepositoryFullName + "/pull/486",
            headSha: "1c8f39ba07de462705ab8c31fd9e6042bb75c1a8",
            state: ChangeRequestState.Open,
            now: filed.AddHours(5),
            headBranch: "charter/checkout-defer-recommendations",
            authorLogin: "northwind-charter[bot]"));

        var artifact = VerificationArtifact.Pending(
            session.Id,
            VerificationArtifactKind.HostedPreview,
            VerificationArtifactAudience.Requester,
            filed.AddHours(5));

        artifact.MarkReady(
            url: "https://storefront-pr-486.preview.northwind.example",
            instructionsMd:
            "Open this on your phone, put anything in the basket, and go to checkout. You should be "
            + "able to type into the card field almost straight away.",
            expiresAt: now.AddDays(1),
            payload: """{"kind":"hosted_preview","reachable":true,"checked_at":"2026-08-08T15:02:00Z"}""");

        database.VerificationArtifacts.Add(artifact);

        database.Recaps.Add(Recap.Generate(
            session.Id,
            """
            ## What this session did

            Deferred the recommendation bundle on the checkout route. Two files, six lines.

            ## Where to look first

            1. `app/checkout/layout.tsx` — the eager import is gone; check nothing else on this
               route referenced it.
            2. `components/RecommendationRail.tsx` — now a default export, which is what
               `next/dynamic` needs. The product page imports it the same way it always did.

            ## Deviations from the specification

            None.

            ## What could not be verified here

            The improvement was measured on the build's route report, not on a real handset.
            """,
            """
            [{"path":"app/checkout/layout.tsx","risk":"medium","why":"changes what the checkout route loads"},{"path":"components/RecommendationRail.tsx","risk":"low","why":"export shape only"}]
            """,
            costUsd: 0.06m,
            now: filed.AddHours(6),
            payloadJson: """
            {"summary":"Deferred the recommendation bundle on the checkout route.","review_order":["app/checkout/layout.tsx","components/RecommendationRail.tsx"],"deviations":[],"unverified":["Measured from the build report rather than on a handset."]}
            """));
    }

    /// <summary>A request still in refinement, so the instance is not all finished work.</summary>
    private void SeedRefiningRequest(Organization organization, Repo repo, User requester, DateTimeOffset now)
    {
        var request = Request.File(
            organization.Id,
            repo.Id,
            requester.Id,
            "Can we let people save a card so they don't have to type it in every time?",
            now: now.AddHours(-4));

        request.TransitionTo(RequestStatus.Refining, now.AddHours(-4));
        database.Requests.Add(request);
    }

    /// <summary>
    /// Appends a transcript, one event per minute, with the per-session monotonic sequence of
    /// section 5.
    /// </summary>
    private Event[] AppendTranscript(
        Guid sessionId,
        DateTimeOffset start,
        (string Type, string Payload)[] entries)
    {
        var events = entries
            .Select((entry, index) => Event.Append(
                sessionId,
                index + 1,
                entry.Type,
                entry.Payload,
                start.AddMinutes(index)))
            .ToArray();

        database.Events.AddRange(events);
        return events;
    }
}
