import { ApiError, type CharterApi } from '@/api/client';
import type { MockPersona } from '@/api/mock/fixtures';
import {
  makeInstance,
  makePendingApprovals,
  makeProjects,
  makeRequests,
  makeViewer,
  TRANSCRIPT_PAGE_SIZE,
} from '@/api/mock/fixtures';
import {
  INVITATION_EXPIRED,
  INVITATION_REFUSAL,
  makeAuthProviders,
  makeSession,
  MINIMUM_PASSWORD_LENGTH,
  MOCK_CREDENTIALS,
  MOCK_EXPIRED_INVITATION_TOKEN,
  MOCK_EXPIRED_RESET_TOKEN,
  MOCK_INVITATION_TOKEN,
  MOCK_RESET_TOKEN,
  MOCK_SETUP_TOKEN,
  RESET_LINK_EXPIRED,
  RESET_LINK_REFUSAL,
  SETUP_ALREADY_COMPLETED,
  SETUP_TOKEN_REFUSAL,
  SIGN_IN_ATTEMPT_LIMIT,
  SIGN_IN_REFUSAL,
  SIGN_IN_THROTTLED,
} from '@/api/mock/fixtures-auth';
import {
  makeAdvisoryMergeGate,
  makeEnforcedMergeGate,
  makePrimerDraft,
  makeRepos,
  makeScopeProposal,
  makeSmokeTest,
  makeSteps,
} from '@/api/mock/fixtures-repos';
import { makePairingToken, makeRunners } from '@/api/mock/fixtures-runners';
import { fileDiffFor, transcriptFor } from '@/api/mock/fixtures-session';
import { makeSetupChecklist } from '@/api/mock/fixtures-setup';
import type {
  AcceptInvitationBody,
  CompleteSetupBody,
  ConfirmScopeBody,
  ConnectRepoBody,
  CreateRequestBody,
  Id,
  MergeGate,
  Milestone,
  PublishPrimerBody,
  RefinementMessage,
  Repo,
  RepoOnboarding,
  RequestDetail,
  RequestStreamEvent,
  RequestSummary,
  ResetPasswordBody,
  RunnersView,
  ScopeProposal,
  SendRefinementMessageBody,
  SetupChecklist,
  SignInBody,
  SmokeTestOutcome,
  SubmitFeedbackBody,
  TranscriptPane,
  TranscriptQuery,
  UserPreferences,
  Viewer,
} from '@/api/types';

/**
 * An in-memory stand-in for the control plane.
 *
 * It exists so the requester flow can be built, reviewed and demonstrated before the minimal APIs
 * land, and so swapping to the real backend is one line in `resolveApi()`. It deliberately mimics
 * the *server's* responsibilities, which is why it is the thing that decides what a viewer may see:
 * the requester persona simply has no `details` and no `transcript` in its payloads, exactly as the
 * real API will omit them (§7.4, §27.7).
 *
 * It holds state in module scope for the life of the page. That is not the browser-storage ban
 * being bent — nothing is persisted, and a reload starts over, which is what a mock should do.
 */

const clone = <T>(value: T): T => structuredClone(value);

/**
 * One connected repository, mid-§9. The onboarding view is derived from these facts on every read
 * rather than stored, so the smoke test progresses simply because time passed — which is what makes
 * it watchable.
 */
interface MockRepoState {
  repo: Repo;
  proposedScope?: ScopeProposal;
  primerDraftMd?: string;
  mergeGate?: MergeGate;
  scopeConfirmedAt?: number;
  scopeConfigPullRequest?: number;
}

interface MockState {
  persona: MockPersona;
  now: number;
  viewer: Viewer;
  requests: RequestDetail[];
  runners: RunnersView;
  setup: SetupChecklist;

  /** §30.1: true means this instance has no users and answers nothing but setup. */
  setupRequired: boolean;
  /** Whether a session cookie would exist. The mock owns this exactly as the server does. */
  signedIn: boolean;
  /** Consecutive failures, for the throttle. Reset by a success, as the real one is. */
  failedSignIns: number;
  repos: MockRepoState[];
}

/**
 * The persona is read from the query string (`?mock=engineer`) purely so the engineer-visible
 * branches — the Details disclosure, panes 2 and 3 — can be exercised in development. It stands in
 * for *the server* deciding who you are; it is not a client-side permission switch, and it
 * disappears with this file the moment the real API is wired up.
 */
function readPersona(): MockPersona {
  if (typeof window === 'undefined') {
    return 'requester';
  }
  const requested = new URLSearchParams(window.location.search).get('mock');
  return requested === 'engineer' || requested === 'new-requester' ? requested : 'requester';
}

/**
 * Which of the three states an instance can be in when a browser arrives: claimed and signed in
 * (the default), claimed and signed out, or never claimed at all.
 *
 * `?instance=setup` and `?instance=signed-out` exist so the two screens that are otherwise
 * unreachable in development — the first-run page and the sign-in page — can be worked on. Like the
 * persona switch above, this stands in for *the server* deciding, and it disappears with this file.
 */
function readInstanceState(): 'ready' | 'signed-out' | 'setup' {
  if (typeof window === 'undefined') {
    return 'ready';
  }
  const requested = new URLSearchParams(window.location.search).get('instance');
  return requested === 'setup' || requested === 'signed-out' ? requested : 'ready';
}

function createState(): MockState {
  const now = Date.now();
  const persona = readPersona();
  const instance = readInstanceState();
  return {
    persona,
    now,
    viewer: makeViewer(persona, now),
    requests: makeRequests(persona, now),
    runners: makeRunners(now),
    setup: makeSetupChecklist(),
    setupRequired: instance === 'setup',
    signedIn: instance === 'ready',
    failedSignIns: 0,
    repos: makeRepos(now).map((repo) => ({
      repo,
      proposedScope: makeScopeProposal(),
      primerDraftMd: makePrimerDraft(repo.fullName),
      mergeGate:
        repo.status === 'ready' ? makeEnforcedMergeGate(now) : makeAdvisoryMergeGate(now),
      ...(repo.status === 'ready'
        ? { scopeConfirmedAt: now - 3 * 60 * 60_000, scopeConfigPullRequest: 118 }
        : {}),
    })),
  };
}

/**
 * Built lazily rather than at module scope. A top-level `createState()` call is a side effect, so
 * the bundler cannot drop this module even when `resolveApi()` never selects it - the whole fixture
 * set (~55 kB raw) was shipping inside the production entry chunk. Deferring construction lets the
 * mock fall out entirely once VITE_CHARTER_LIVE_API is set.
 */
let state: MockState | null = null;

function mockState(): MockState {
  return (state ??= createState());
}

/** Test seam: resets module state between cases. */
export function __resetMockState(): void {
  state = createState();
}

const latency = (ms = 180) =>
  new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });

function find(id: Id): RequestDetail {
  const found = mockState().requests.find((request) => request.id === id);
  if (!found) {
    throw new Error(`Mock API: no request ${id}`);
  }
  return found;
}

function toSummary(request: RequestDetail): RequestSummary {
  const summary: RequestSummary = {
    id: request.id,
    projectId: request.projectId,
    projectName: request.projectName,
    title: request.title,
    status: request.status,
    createdAt: request.createdAt,
    updatedAt: request.updatedAt,
    needsAttention: request.needsAttention,
  };
  if (request.lastMilestoneLabel !== undefined) {
    summary.lastMilestoneLabel = request.lastMilestoneLabel;
  }
  if (request.awaitingApprovalFrom !== undefined) {
    summary.awaitingApprovalFrom = request.awaitingApprovalFrom;
  }
  return summary;
}

/* -------------------------------------------------------------------------- */
/* Scripted streams                                                           */
/* -------------------------------------------------------------------------- */

/**
 * §11 requires that *something* streams: "a 5–20 minute silent gap reads as broken". These scripts
 * are what the SignalR hub will send for real. They run on a short clock so the behaviour is
 * observable in a demo rather than in twenty minutes.
 */
type ScriptStep = { after: number; event: RequestStreamEvent };

function liveBuildScript(requestId: Id): ScriptStep[] {
  if (requestId !== 'req-pdf') {
    return [];
  }

  const at = (offset: number) => new Date(Date.now() + offset).toISOString();

  const changing: Milestone = {
    id: 'pm3',
    kind: 'changing',
    label: 'Making the changes',
    detail: 'Added the installer block to the PDF template.',
    occurredAt: at(-6 * 60_000),
    state: 'done',
  };

  return [
    {
      after: 6_000,
      event: { type: 'milestone_updated', milestone: changing },
    },
    {
      after: 6_500,
      event: {
        type: 'milestone',
        milestone: {
          id: 'pm4',
          kind: 'checking',
          label: 'Checking it works',
          detail: 'Regenerating a handful of old quotes to make sure nothing shifted.',
          occurredAt: at(0),
          state: 'active',
        },
      },
    },
    {
      after: 16_000,
      event: {
        type: 'milestone_updated',
        milestone: {
          id: 'pm4',
          kind: 'checking',
          label: 'Checking it works',
          detail: 'All existing quotes regenerated unchanged.',
          occurredAt: at(0),
          state: 'done',
        },
      },
    },
    {
      after: 16_500,
      event: {
        type: 'milestone',
        milestone: {
          id: 'pm5',
          kind: 'assembling',
          label: 'Putting it together',
          detail: 'Building a copy of the quote tool with your change in it.',
          occurredAt: at(0),
          state: 'active',
        },
      },
    },
    {
      after: 26_000,
      event: {
        type: 'milestone_updated',
        milestone: {
          id: 'pm5',
          kind: 'assembling',
          label: 'Putting it together',
          occurredAt: at(0),
          state: 'done',
        },
      },
    },
    {
      after: 26_200,
      event: {
        type: 'artifact',
        artifact: {
          id: 'art-pdf',
          kind: 'hosted_preview',
          state: 'ready',
          audience: 'requester',
          label: 'Preview',
          primary: true,
          expiresAt: new Date(Date.now() + 6 * 60 * 60_000).toISOString(),
          instructionsMd:
            'A copy of the quote tool with your change in it, loaded with test data. Open any quote and download its PDF.',
          payload: {
            url: 'https://pr-144.preview.northbeam.charter.app/quotes',
            displayUrl: 'pr-144.preview.northbeam…/quotes',
            reachability: 'reachable',
          },
        },
      },
    },
    {
      after: 26_400,
      event: {
        type: 'milestone',
        milestone: {
          id: 'pm6',
          kind: 'outcome',
          label: 'Ready to try',
          occurredAt: at(0),
          state: 'done',
        },
      },
    },
    { after: 26_600, event: { type: 'status', status: 'preview_ready' } },
    { after: 26_800, event: { type: 'ended', endedAt: at(0) } },
  ];
}

/** Charter's side of the refinement conversation, replayed after the requester sends something. */
const pendingReplies = new Map<Id, ScriptStep[]>();

function scriptCharterReply(body: string): ScriptStep[] {
  const at = () => new Date().toISOString();
  const id = `charter-${Math.random().toString(36).slice(2, 10)}`;
  const mentionsDeniedArea = /\b(login|password|sign in|auth|permission)\b/i.test(body);

  if (mentionsDeniedArea) {
    // §10: refinement refuses to produce a spec touching denied paths, explains in plain English,
    // and routes to an engineer rather than leaving the requester at a dead end.
    const refusal: RefinementMessage = {
      id,
      author: 'charter',
      kind: 'refusal',
      createdAt: at(),
      body: 'I have stopped here rather than guessing. This touches how people sign in, which is one of the areas Northbeam has marked as off limits to me — changes there go through an engineer.\n\nTomas has the request and the conversation so far. Nothing is lost, and you do not need to re-explain it.',
      routedToEngineer: true,
    };
    return [
      { after: 700, event: { type: 'charter_thinking', thinking: true } },
      { after: 2_400, event: { type: 'charter_thinking', thinking: false } },
      { after: 2_500, event: { type: 'refinement_message', message: refusal } },
    ];
  }

  const reply: RefinementMessage = {
    id,
    author: 'charter',
    kind: 'question',
    createdAt: at(),
    body: 'Got it. One more thing so nobody has to guess later: who should see this change — everyone using the quote tool, or only your team?',
    choices: [
      { id: `${id}-a`, label: 'Everyone' },
      { id: `${id}-b`, label: 'Only my team' },
    ],
  };

  return [
    { after: 600, event: { type: 'charter_thinking', thinking: true } },
    { after: 2_200, event: { type: 'charter_thinking', thinking: false } },
    { after: 2_300, event: { type: 'refinement_message', message: reply } },
  ];
}

/* -------------------------------------------------------------------------- */

/* -------------------------------------------------------------------------- */
/* Repositories (§9)                                                          */
/* -------------------------------------------------------------------------- */

function findRepo(id: Id): MockRepoState {
  const found = mockState().repos.find((candidate) => candidate.repo.id === id);
  if (!found) {
    throw new ApiError(404, 'We could not find that. It may have been removed.');
  }
  return found;
}

/**
 * §9's readiness rule, enforced where the server enforces it: a repository becomes requester-visible
 * because its smoke test passed, and by no other route. There is no mock method that sets it.
 */
function smokeTestFor(entry: MockRepoState): SmokeTestOutcome | null {
  if (entry.scopeConfirmedAt === undefined) {
    return null;
  }

  const outcome = makeSmokeTest(entry.scopeConfirmedAt, Date.now());

  if (outcome.passed && entry.repo.status === 'smoke_test') {
    entry.repo = { ...entry.repo, status: 'ready', requesterVisible: true, updatedAt: new Date().toISOString() };
  }

  return outcome;
}

function describeRepo(entry: MockRepoState): RepoOnboarding {
  const smokeTest = smokeTestFor(entry);

  return {
    repo: clone(entry.repo),
    steps: makeSteps(entry.repo, entry.proposedScope !== undefined, smokeTest?.passed ?? false),
    ...(entry.scopeConfigPullRequest === undefined
      ? {}
      : { scopeConfigPullRequest: entry.scopeConfigPullRequest }),
    ...(smokeTest === null ? {} : { lastSmokeTest: smokeTest }),
    ...(entry.mergeGate === undefined || !(smokeTest?.passed ?? false)
      ? {}
      : { mergeGate: clone(entry.mergeGate) }),
    ...(entry.proposedScope === undefined ? {} : { proposedScope: clone(entry.proposedScope) }),
    ...(entry.primerDraftMd === undefined || entry.repo.hasPrimer
      ? {}
      : { primerDraftMd: entry.primerDraftMd }),
  };
}

export const mockApi: CharterApi = {
  async getInstance() {
    await latency(80);
    return makeInstance(mockState().now);
  },

  /* ---- First run and sign-in (§30.1, §21) -------------------------------- */

  async getSetupStatus() {
    await latency(60);
    return { setupRequired: mockState().setupRequired };
  },

  async completeSetup(body: CompleteSetupBody) {
    await latency(400);
    const state = mockState();

    if (!state.setupRequired) {
      throw new ApiError(409, SETUP_ALREADY_COMPLETED);
    }
    if (body.token.trim() !== MOCK_SETUP_TOKEN) {
      // Recoverable, and it says where a correct token comes from. The form keeps what was typed.
      throw new ApiError(400, SETUP_TOKEN_REFUSAL);
    }
    if (body.password.length < MINIMUM_PASSWORD_LENGTH) {
      throw new ApiError(400, `Choose a password of at least ${MINIMUM_PASSWORD_LENGTH} characters.`);
    }

    // Exactly one admin, and setup mode ends permanently.
    state.setupRequired = false;
    state.signedIn = true;
    state.viewer = {
      ...state.viewer,
      displayName: body.displayName,
      email: body.email,
      ...(body.organizationName === undefined || body.organizationName.trim() === ''
        ? {}
        : { organization: { ...state.viewer.organization, name: body.organizationName } }),
    };

    return makeSession(body.displayName, body.email);
  },

  async getAuthProviders() {
    await latency(80);
    return makeAuthProviders();
  },

  async signIn(body: SignInBody) {
    await latency(320);
    const state = mockState();

    if (state.failedSignIns >= SIGN_IN_ATTEMPT_LIMIT) {
      throw new ApiError(429, SIGN_IN_THROTTLED, 60);
    }

    const matches =
      body.email.trim().toLowerCase() === MOCK_CREDENTIALS.email &&
      body.password === MOCK_CREDENTIALS.password;

    if (!matches) {
      state.failedSignIns += 1;
      // §21: an unknown address and a wrong password are the same refusal, word for word. Anything
      // that told them apart would answer "does this person have an account here" for free.
      throw new ApiError(401, SIGN_IN_REFUSAL);
    }

    state.failedSignIns = 0;
    state.signedIn = true;
    return makeSession(state.viewer.displayName, state.viewer.email);
  },

  async signOut() {
    await latency(120);
    mockState().signedIn = false;
  },

  async forgotPassword() {
    await latency(200);
    // The same sentence for an address with an account and for one without, and never a link.
    return {
      message:
        'If that address has an account, a reset link is on its way. Check your spam folder if it does not arrive.',
    };
  },

  async resetPassword(body: ResetPasswordBody) {
    await latency(300);
    if (body.token === MOCK_EXPIRED_RESET_TOKEN) {
      throw new ApiError(400, RESET_LINK_EXPIRED);
    }
    if (body.token !== MOCK_RESET_TOKEN) {
      throw new ApiError(400, RESET_LINK_REFUSAL);
    }
    if (body.password.length < MINIMUM_PASSWORD_LENGTH) {
      throw new ApiError(400, `Choose a password of at least ${MINIMUM_PASSWORD_LENGTH} characters.`);
    }
    // No session: proving control of a mailbox sets a password and does not sign a browser in.
  },

  async acceptInvitation(body: AcceptInvitationBody) {
    await latency(360);
    const state = mockState();

    if (body.token === MOCK_EXPIRED_INVITATION_TOKEN) {
      throw new ApiError(400, INVITATION_EXPIRED);
    }
    if (body.token !== MOCK_INVITATION_TOKEN) {
      throw new ApiError(400, INVITATION_REFUSAL);
    }
    if (body.password.length < MINIMUM_PASSWORD_LENGTH) {
      throw new ApiError(400, `Choose a password of at least ${MINIMUM_PASSWORD_LENGTH} characters.`);
    }

    state.signedIn = true;
    state.viewer = { ...state.viewer, displayName: body.displayName };
    return makeSession(body.displayName, state.viewer.email);
  },

  async getViewer(): Promise<Viewer> {
    await latency(80);
    const state = mockState();

    // The mock stands in for the server, so the two refusals the app routes on come from here: an
    // unclaimed instance answers 503 for everything but setup, and a browser with no cookie gets 401.
    if (state.setupRequired) {
      throw new ApiError(
        503,
        'This instance has no users. Read the one-time setup token from the container logs and complete setup at /setup.',
      );
    }
    if (!state.signedIn) {
      throw new ApiError(401, 'Sign in again and we will bring you back here.');
    }

    return clone(state.viewer);
  },

  async updatePreferences(patch: Partial<UserPreferences>) {
    await latency(120);
    mockState().viewer = { ...mockState().viewer, preferences: { ...mockState().viewer.preferences, ...patch } };
    return clone(mockState().viewer.preferences);
  },

  async completeRequesterOnboarding() {
    await latency(120);
    mockState().viewer = {
      ...mockState().viewer,
      requesterOnboardingCompletedAt: new Date().toISOString(),
    };
    return clone(mockState().viewer);
  },

  async listProjects() {
    await latency();
    return clone(makeProjects(mockState().persona));
  },

  async listRequests() {
    await latency();
    return clone(
      [...mockState().requests]
        .sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt))
        .map(toSummary),
    );
  },

  async getRequest(id) {
    await latency();
    return clone(find(id));
  },

  async createRequest(body: CreateRequestBody) {
    await latency(400);
    const projects = makeProjects(mockState().persona);
    const project = projects.find((candidate) => candidate.id === body.projectId) ?? projects[0];
    const created: RequestDetail = {
      id: `req-${Math.random().toString(36).slice(2, 8)}`,
      projectId: project?.id ?? 'proj-quotes',
      projectName: project?.name ?? 'Quote tool',
      title: body.rawText.split('\n')[0]?.slice(0, 80) ?? 'New request',
      status: 'refining',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      needsAttention: false,
      rawText: body.rawText,
      cancellable: false,
      ...(body.templateId === undefined ? {} : { templateId: body.templateId }),
      refinement: {
        mode: 'plan',
        canReply: true,
        charterIsThinking: true,
        messages: [
          {
            id: 'seed',
            author: 'requester',
            kind: 'message',
            createdAt: new Date().toISOString(),
            body: body.rawText,
          },
        ],
      },
      thread: { live: false, milestones: [] },
      artifacts: [],
    };

    mockState().requests = [created, ...mockState().requests];
    pendingReplies.set(created.id, scriptCharterReply(body.rawText));
    return clone(created);
  },

  async sendRefinementMessage(id, body: SendRefinementMessageBody) {
    await latency(120);
    const request = find(id);
    request.refinement.messages.push({
      id: `req-msg-${Math.random().toString(36).slice(2, 10)}`,
      author: 'requester',
      kind: 'message',
      createdAt: new Date().toISOString(),
      body: body.body,
    });
    request.updatedAt = new Date().toISOString();
    pendingReplies.set(id, scriptCharterReply(body.body));
  },

  async approveSpec(id, version) {
    await latency(300);
    const request = find(id);
    if (request.spec && request.spec.version === version) {
      request.spec.approvedAt = new Date().toISOString();
      request.spec.approvedByName = mockState().viewer.displayName;
    }
    request.status = 'queued';
    request.refinement.canReply = false;
    request.refinement.mode = 'build';
    request.cancellable = true;
    request.thread = {
      live: true,
      startedAt: new Date().toISOString(),
      milestones: [
        {
          id: `ms-approved-${request.id}`,
          kind: 'status',
          label: 'You approved the plan',
          occurredAt: new Date().toISOString(),
          state: 'done',
        },
        {
          id: `ms-start-${request.id}`,
          kind: 'understanding',
          label: 'Understanding the current setup',
          detail: 'Reading the parts of the quote tool this touches.',
          occurredAt: new Date().toISOString(),
          state: 'active',
        },
      ],
    };
    request.updatedAt = new Date().toISOString();
  },

  async requestSpecChanges(id, _version, note) {
    await latency(200);
    const request = find(id);
    request.refinement.messages.push({
      id: `changes-${Math.random().toString(36).slice(2, 10)}`,
      author: 'requester',
      kind: 'message',
      createdAt: new Date().toISOString(),
      body: note,
    });
    request.refinement.canReply = true;
    request.status = 'refining';
    request.updatedAt = new Date().toISOString();
    pendingReplies.set(id, scriptCharterReply(note));
  },

  async submitFeedback(id, body: SubmitFeedbackBody) {
    await latency(250);
    const request = find(id);
    request.thread.feedback = {
      verdict: body.verdict,
      ...(body.note === undefined ? {} : { note: body.note }),
      submittedAt: new Date().toISOString(),
    };
    request.needsAttention = false;

    if (body.verdict === 'not_quite') {
      // §11: "Not quite" becomes a new session on the same spec, in the same thread.
      request.status = 'queued';
      request.thread.live = true;
      request.thread.milestones.push({
        id: `ms-again-${Math.random().toString(36).slice(2, 8)}`,
        kind: 'status',
        label: 'Having another go',
        detail: 'Your note has gone back with the original plan.',
        occurredAt: new Date().toISOString(),
        state: 'active',
      });
    }
    request.updatedAt = new Date().toISOString();
  },

  async cancelRequest(id) {
    await latency(300);
    const request = find(id);
    request.status = 'cancelled';
    request.cancellable = false;
    request.thread.live = false;
    request.thread.endedAt = new Date().toISOString();
    request.thread.milestones.push({
      id: `ms-cancel-${Math.random().toString(36).slice(2, 8)}`,
      kind: 'outcome',
      label: 'You stopped this',
      detail: 'Nothing further will be spent on it.',
      occurredAt: new Date().toISOString(),
      state: 'done',
    });
    request.updatedAt = new Date().toISOString();
  },

  async rebuildArtifact(id, artifactId) {
    await latency(300);
    const request = find(id);
    const artifact = request.artifacts.find((candidate) => candidate.id === artifactId);
    if (artifact) {
      artifact.state = 'pending';
      delete artifact.expiresAt;
    }
    request.status = 'queued';
    request.thread.live = true;
    delete request.thread.endedAt;
    request.thread.startedAt = new Date().toISOString();
    request.thread.milestones.push({
      id: `ms-rebuild-${Math.random().toString(36).slice(2, 8)}`,
      kind: 'assembling',
      label: 'Putting it together again',
      occurredAt: new Date().toISOString(),
      state: 'active',
    });
    request.updatedAt = new Date().toISOString();
  },

  async listPendingApprovals() {
    await latency();
    return clone(makePendingApprovals(mockState().persona, mockState().now));
  },

  /* ---- Panes 2 and 3 ------------------------------------------------------ */

  /**
   * Pages backwards from the tail, or centres a window on `aroundSeq` when pane 1 links into the
   * middle of a twelve-thousand-event stream.
   *
   * The cursor is the index of the first event in the returned page, so the previous page is
   * everything ending there. Opaque to the client either way — it never parses one.
   */
  async getTranscript(id: Id, query: TranscriptQuery): Promise<TranscriptPane> {
    await latency(140);

    // The mock stands in for the server, so it is the thing that enforces §7.4: a persona without
    // repo read is refused here, not filtered in the component.
    if (!mockState().viewer.capabilities.canReadRepos) {
      throw new Error('Mock API: 403 — transcripts require repo read access');
    }

    const all = transcriptFor(id, mockState().now);
    const limit = query.limit ?? TRANSCRIPT_PAGE_SIZE;

    let from: number;
    if (query.aroundSeq !== undefined) {
      const centre = all.findIndex((event) => event.seq === query.aroundSeq);
      from = Math.max(0, (centre === -1 ? all.length : centre) - Math.floor(limit / 2));
    } else if (query.cursor !== undefined) {
      from = Math.max(0, Number(query.cursor) - limit);
    } else {
      from = Math.max(0, all.length - limit);
    }

    const to = Math.min(all.length, from + limit);

    return {
      events: clone(all.slice(from, to)),
      nextCursor: from > 0 ? String(from) : null,
      totalCount: all.length,
    };
  },

  async getFileDiff(_id, path) {
    await latency(160);
    if (!mockState().viewer.capabilities.canReadRepos) {
      throw new Error('Mock API: 403 — diffs require repo read access');
    }
    return fileDiffFor(path);
  },

  /* ---- Post-hoc session actions (§7.5) ------------------------------------ */

  async approveSession(id) {
    await latency(220);
    const request = find(id);
    request.status = 'in_review';
    request.updatedAt = new Date().toISOString();
  },

  async steerSession(id, instruction) {
    await latency(260);
    const request = find(id);
    // §7.5: "continue the existing session with a new instruction; same branch, same thread."
    request.status = 'running';
    request.thread.live = true;
    delete request.thread.endedAt;
    request.thread.milestones.push({
      id: `ms-steer-${Math.random().toString(36).slice(2, 8)}`,
      kind: 'status',
      label: 'An engineer sent it a new instruction',
      detail: instruction,
      occurredAt: new Date().toISOString(),
      state: 'active',
    });
    request.cancellable = true;
    request.updatedAt = new Date().toISOString();
  },

  async reviseSession(id) {
    await latency(280);
    const request = find(id);
    // §7.5: fork the spec, dispatch a fresh session onto the same branch.
    request.status = 'queued';
    request.thread.live = true;
    delete request.thread.endedAt;
    request.thread.startedAt = new Date().toISOString();
    request.thread.milestones.push({
      id: `ms-revise-${Math.random().toString(36).slice(2, 8)}`,
      kind: 'status',
      label: 'An engineer revised the plan and started again',
      occurredAt: new Date().toISOString(),
      state: 'active',
    });
    request.cancellable = true;
    request.updatedAt = new Date().toISOString();
  },

  async takeOverSession(id) {
    await latency(200);
    const request = find(id);

    // §7.5: Charter marks the session `handed_off` and stops touching it. Steer and Revise are
    // withdrawn here rather than merely greyed out, because the whole point is that no further
    // agent write to this branch is possible.
    if (request.sessionActions) {
      request.sessionActions = {
        ...request.sessionActions,
        canSteer: false,
        canRevise: false,
        canApprove: false,
        canTakeOver: false,
        handedOff: { at: new Date().toISOString(), byName: mockState().viewer.displayName },
      };
    }
    request.status = 'in_review';
    request.cancellable = false;
    request.thread.live = false;
    request.thread.endedAt = new Date().toISOString();
    request.thread.milestones.push({
      id: `ms-handoff-${Math.random().toString(36).slice(2, 8)}`,
      kind: 'status',
      label: 'An engineer took this over',
      detail: 'They are finishing it by hand. Nothing further is being changed automatically.',
      occurredAt: new Date().toISOString(),
      state: 'done',
    });
    request.updatedAt = new Date().toISOString();
  },

  /* ---- Settings → Runners (§33.3) ----------------------------------------- */

  async listRunners() {
    await latency();
    if (!mockState().viewer.capabilities.canAdminister) {
      throw new Error('Mock API: 403 — registering runners is an admin action');
    }
    return clone(mockState().runners);
  },

  async createPairingToken() {
    await latency(240);
    return makePairingToken(Date.now());
  },

  async revokeAgent(agentId) {
    await latency(260);
    mockState().runners = {
      ...mockState().runners,
      agents: mockState().runners.agents.filter((agent) => agent.id !== agentId),
    };
  },

  /* ---- Repo onboarding (§9) ----------------------------------------------- */

  async listRepos() {
    await latency();
    if (!mockState().viewer.capabilities.canReadRepos) {
      throw new ApiError(403, 'Connecting repositories is an engineer or administrator action.');
    }
    return mockState().repos.map((entry) => clone(entry.repo));
  },

  async connectRepo(body: ConnectRepoBody) {
    await latency(500);
    const now = Date.now();
    const repo: Repo = {
      id: `repo-${Math.random().toString(36).slice(2, 8)}`,
      fullName: body.fullName,
      baseBranch: body.baseBranch === undefined || body.baseBranch === '' ? 'main' : body.baseBranch,
      // §9: connecting is step one of six. Nothing is requestable yet, and nobody is scoped to it.
      status: 'pending',
      requesterVisible: false,
      hasPrimer: false,
      connectedAt: new Date(now).toISOString(),
      updatedAt: new Date(now).toISOString(),
    };

    mockState().repos = [{ repo }, ...mockState().repos];
    return clone(repo);
  },

  async getRepoOnboarding(id) {
    await latency(140);
    return describeRepo(findRepo(id));
  },

  async startRecon(id) {
    await latency(600);
    const entry = findRepo(id);
    entry.repo = { ...entry.repo, status: 'configuring', updatedAt: new Date().toISOString() };
    entry.proposedScope = makeScopeProposal();
    entry.primerDraftMd = makePrimerDraft(entry.repo.fullName);
    entry.mergeGate = makeAdvisoryMergeGate(Date.now());

    return {
      status: entry.repo.status,
      explanation:
        'Recon read the repository without writing to it, and has proposed what Charter may and may not touch.',
      warnings: ['An existing AGENTS.md was imported and extended rather than replaced.'],
    };
  },

  async confirmScope(id, body: ConfirmScopeBody) {
    await latency(520);
    const entry = findRepo(id);

    if (entry.proposedScope) {
      const allow = new Set(body.allow ?? []);
      const deny = new Set(body.deny ?? []);
      entry.proposedScope = {
        ...entry.proposedScope,
        entries: entry.proposedScope.entries.map((scope) =>
          // The deny-by-default floor is applied here because the server applies it there: a client
          // cannot widen scope past it, whatever it sends.
          scope.locked === true
            ? { ...scope, allowed: false }
            : { ...scope, allowed: allow.has(scope.path) ? true : deny.has(scope.path) ? false : scope.allowed },
        ),
      };
    }

    entry.scopeConfirmedAt = Date.now();
    entry.scopeConfigPullRequest = 127;
    entry.repo = { ...entry.repo, status: 'smoke_test', updatedAt: new Date().toISOString() };

    return {
      status: entry.repo.status,
      explanation:
        'The scope config is open as a pull request, and the smoke test has been queued against it.',
      warnings: [],
      pullRequestNumber: 127,
      pullRequestUrl: 'https://github.com/northbeam/quote-tool/pull/127',
    };
  },

  async getSmokeTest(id) {
    await latency(110);
    return smokeTestFor(findRepo(id));
  },

  async publishPrimer(id, body: PublishPrimerBody) {
    await latency(400);
    const entry = findRepo(id);
    entry.repo = { ...entry.repo, hasPrimer: true, updatedAt: new Date().toISOString() };
    entry.primerDraftMd = body.markdown;

    return {
      status: entry.repo.status,
      explanation: 'The primer is published. New requesters will read it once, before their first request.',
      warnings: [],
    };
  },

  /* ---- Admin setup checklist (§30.2) -------------------------------------- */

  async getSetupChecklist() {
    await latency(120);
    // Null rather than a throw: a requester's dashboard has no checklist, and that is not an error
    // state the page should have to render.
    return mockState().viewer.capabilities.canAdminister ? clone(mockState().setup) : null;
  },

  async dismissSetupChecklist() {
    await latency(160);
    mockState().setup = { ...mockState().setup, dismissedAt: new Date().toISOString() };
    return clone(mockState().setup);
  },

  subscribeToRequest(id, onEvent) {
    const timers: ReturnType<typeof setTimeout>[] = [];

    const run = (steps: ScriptStep[]) => {
      for (const step of steps) {
        timers.push(
          setTimeout(() => {
            onEvent(step.event);
          }, step.after),
        );
      }
    };

    run(liveBuildScript(id));

    const queued = pendingReplies.get(id);
    if (queued) {
      pendingReplies.delete(id);
      run(queued);
    }

    // Anything queued while the subscription is open (a reply the requester just sent) is picked
    // up on the next tick, which is how the real hub will behave.
    const poll = setInterval(() => {
      const next = pendingReplies.get(id);
      if (next) {
        pendingReplies.delete(id);
        run(next);
      }
    }, 250);

    return () => {
      clearInterval(poll);
      for (const timer of timers) {
        clearTimeout(timer);
      }
    };
  },
};
