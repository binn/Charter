import type {
  MergeGate,
  Repo,
  RepoOnboarding,
  ScopeProposal,
  SmokeTestCheckpoint,
  SmokeTestOutcome,
} from '@/api/types';

/**
 * §9, repo onboarding — "a wizard that ends in **proof**, not configuration".
 *
 * The fixture is built so all three of the things §9 cares about are reachable without a backend:
 *
 * - **The smoke test runs on screen.** `smokeCheckpoints()` is a function of elapsed time, so the
 *   six integration points light up one after another while the engineer watches. §9 calls that run
 *   the demo; a boolean at the end of it is not.
 * - **Scope is denied by default.** The proposal below marks migrations, auth, CI config, infra and
 *   secrets as locked denials, because the server filters whatever the client sends through that
 *   floor anyway and a toggle that silently does nothing would be a lie.
 * - **One repository is advisory.** §7.4's guarantee is only as strong as the provider makes it, and
 *   the only way to know whether the wizard says so plainly is to have a repository where it must.
 */

/** The smoke-test clock, compressed so the run is watchable in a demo rather than in ten minutes. */
const CHECKPOINTS: { id: SmokeTestCheckpoint['id']; label: string; at: number; detail: string }[] = [
  {
    id: 'request_filed',
    label: 'Filed a trivial request',
    at: 1_500,
    detail: 'Change the footer year. Nothing that could matter if it goes wrong.',
  },
  {
    id: 'agent_ran',
    label: 'The agent ran',
    at: 7_000,
    detail: 'Claude Code, on the shared Docker runner.',
  },
  {
    id: 'checks_passed',
    label: 'Your checks passed',
    at: 11_000,
    detail: 'npm test, npm run build — the commands recon found.',
  },
  {
    id: 'pull_request',
    label: 'A pull request opened',
    at: 15_000,
    detail: 'charter/smoke-test → main',
  },
  {
    id: 'preview_deployed',
    label: 'A preview deployed',
    at: 21_000,
    detail: 'The provider built the branch and returned a URL.',
  },
  {
    id: 'url_bound',
    label: 'The preview URL bound back',
    at: 25_000,
    detail: 'Charter can now show a requester something to click.',
  },
];

export const SMOKE_TEST_DURATION_MS = 26_000;

/**
 * The six integration points at whatever moment the wizard asked. Nothing here is a prediction:
 * a step is `running` because the one before it finished, never because of an estimate (§6).
 */
export function smokeCheckpoints(elapsedMs: number): SmokeTestCheckpoint[] {
  let running = true;
  return CHECKPOINTS.map((checkpoint) => {
    if (elapsedMs >= checkpoint.at) {
      return { id: checkpoint.id, label: checkpoint.label, state: 'passed', detail: checkpoint.detail };
    }
    if (running) {
      running = false;
      return { id: checkpoint.id, label: checkpoint.label, state: 'running', detail: checkpoint.detail };
    }
    return { id: checkpoint.id, label: checkpoint.label, state: 'pending' };
  });
}

export function makeSmokeTest(startedAt: number, now: number): SmokeTestOutcome {
  const elapsed = now - startedAt;
  const checkpoints = smokeCheckpoints(elapsed);
  const finished = elapsed >= SMOKE_TEST_DURATION_MS;

  return {
    passed: finished,
    at: new Date(startedAt + Math.min(elapsed, SMOKE_TEST_DURATION_MS)).toISOString(),
    previewBound: elapsed >= 25_000,
    checkpoints,
    ...(elapsed >= 21_000
      ? {
          // §9 seed data: an empty preview warns, it never blocks.
          warnings: [
            'The preview deployed but looks empty — without seed data, requesters may not be able to judge a change.',
          ],
        }
      : {}),
    ...(elapsed >= 15_000 ? { pullRequestNumber: 128 } : {}),
  };
}

/**
 * What recon proposed. §9 step 3: "visual file tree with allow/deny toggles. Defaults denied:
 * migrations, auth, CI config, infra, secrets."
 */
export function makeScopeProposal(): ScopeProposal {
  return {
    detectedStack: ['ASP.NET Core 10', 'React 19', 'PostgreSQL 17', 'Playwright'],
    commands: [
      { label: 'Tests', command: 'npm test && dotnet test' },
      { label: 'Build', command: 'npm run build' },
      { label: 'Seed', command: 'dotnet run --project tools/Seed' },
    ],
    importedFrom: ['AGENTS.md'],
    entries: [
      { path: 'src/app/', kind: 'directory', allowed: true, reason: 'The application itself' },
      { path: 'src/components/', kind: 'directory', allowed: true, reason: 'Shared UI' },
      { path: 'src/lib/', kind: 'directory', allowed: true, reason: 'Helpers' },
      { path: 'tests/', kind: 'directory', allowed: true, reason: 'Test suites' },
      { path: 'docs/', kind: 'directory', allowed: false, reason: 'Documentation — usually written by hand' },
      {
        path: 'db/migrations/',
        kind: 'directory',
        allowed: false,
        locked: true,
        reason: 'Database migrations',
      },
      {
        path: 'src/auth/',
        kind: 'directory',
        allowed: false,
        locked: true,
        reason: 'How people sign in',
      },
      {
        path: '.github/workflows/',
        kind: 'directory',
        allowed: false,
        locked: true,
        reason: 'CI configuration',
      },
      { path: 'infra/', kind: 'directory', allowed: false, locked: true, reason: 'Infrastructure' },
      { path: '.env.example', kind: 'file', allowed: false, locked: true, reason: 'Secrets' },
    ],
  };
}

export function makePrimerDraft(fullName: string): string {
  const name = fullName.split('/')[1] ?? fullName;
  return [
    `# ${name}`,
    '',
    'This is the quote tool the sales team uses. Someone creates a quote, adds line items, and sends',
    'the customer a PDF. Everything else in here exists to support that.',
    '',
    '## Words that mean something specific here',
    '',
    '- **Quote** — a priced offer for one customer. It has versions; the customer sees the latest.',
    '- **Vertical** — which industry the customer is in. It changes which line items are offered.',
    '',
    '## What changes are usually about',
    '',
    'Wording on the PDF, which fields appear on the quote form, and how totals are rounded.',
  ].join('\n');
}

/** §7.4, enforced: branch protection requires review, so Charter having no merge button holds. */
export function makeEnforcedMergeGate(now: number): MergeGate {
  return {
    enforcement: 'provider_enforced',
    branch: 'main',
    protectionConfigured: true,
    requiresReview: true,
    checkedAt: new Date(now - 20 * 60_000).toISOString(),
  };
}

/**
 * §7.4, advisory: no protection rule at all. Charter still will not merge, and it cannot stop
 * anyone else from doing so. This warns; it never blocks.
 */
export function makeAdvisoryMergeGate(now: number): MergeGate {
  return {
    enforcement: 'advisory',
    branch: 'main',
    protectionConfigured: false,
    requiresReview: false,
    checkedAt: new Date(now - 4 * 60_000).toISOString(),
    warning:
      'main has no branch protection rule, so nothing stops a person merging an agent’s pull request without review. Charter will not merge it, but that is the only half of the guarantee Charter owns.',
  };
}

export function makeRepos(now: number): Repo[] {
  return [
    {
      id: 'repo-quote-tool',
      fullName: 'northbeam/quote-tool',
      baseBranch: 'main',
      status: 'ready',
      requesterVisible: true,
      hasPrimer: true,
      connectedAt: new Date(now - 9 * 24 * 60 * 60_000).toISOString(),
      updatedAt: new Date(now - 2 * 60 * 60_000).toISOString(),
    },
    {
      id: 'repo-billing',
      fullName: 'northbeam/billing-api',
      baseBranch: 'main',
      // Recon has finished and proposed a scope; confirming it is the step waiting for an engineer.
      status: 'configuring',
      requesterVisible: false,
      hasPrimer: false,
      connectedAt: new Date(now - 40 * 60_000).toISOString(),
      updatedAt: new Date(now - 6 * 60_000).toISOString(),
    },
  ];
}

/** The step list §9 spells out, with `current` on the one thing to do next. */
export function makeSteps(repo: Repo, hasScope: boolean, smokePassed: boolean): RepoOnboarding['steps'] {
  const done: Record<string, boolean> = {
    connect: true,
    recon: hasScope || repo.status !== 'pending',
    confirm_scope: repo.status === 'smoke_test' || repo.status === 'ready',
    smoke_test: smokePassed,
    primer: repo.hasPrimer,
    merge_gate: smokePassed,
  };

  const labels: [RepoOnboarding['steps'][number]['id'], string][] = [
    ['connect', 'Connect the repository'],
    ['recon', 'Let Charter read it'],
    ['confirm_scope', 'Confirm the scope config'],
    ['smoke_test', 'Watch the smoke test'],
    ['primer', 'Publish the primer'],
    ['merge_gate', 'Check the merge gate'],
  ];

  const firstUndone = labels.find(([id]) => !done[id])?.[0];

  return labels.map(([id, label]) => ({
    id,
    label,
    done: done[id] ?? false,
    current: id === firstUndone,
  }));
}
