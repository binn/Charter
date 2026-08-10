import type { CharterApi } from '@/api/client';
import type { MockPersona } from '@/api/mock/fixtures';
import {
  makeInstance,
  makePendingApprovals,
  makeProjects,
  makeRequests,
  makeViewer,
} from '@/api/mock/fixtures';
import type {
  CreateRequestBody,
  Id,
  Milestone,
  RefinementMessage,
  RequestDetail,
  RequestStreamEvent,
  RequestSummary,
  SendRefinementMessageBody,
  SubmitFeedbackBody,
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

interface MockState {
  persona: MockPersona;
  now: number;
  viewer: Viewer;
  requests: RequestDetail[];
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

function createState(): MockState {
  const now = Date.now();
  const persona = readPersona();
  return {
    persona,
    now,
    viewer: makeViewer(persona, now),
    requests: makeRequests(persona, now),
  };
}

let state: MockState = createState();

/** Test seam: resets module state between cases. */
export function __resetMockState(): void {
  state = createState();
}

const latency = (ms = 180) =>
  new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });

function find(id: Id): RequestDetail {
  const found = state.requests.find((request) => request.id === id);
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

export const mockApi: CharterApi = {
  async getInstance() {
    await latency(80);
    return makeInstance(state.now);
  },

  async getViewer() {
    await latency(80);
    return clone(state.viewer);
  },

  async updatePreferences(patch: Partial<UserPreferences>) {
    await latency(120);
    state.viewer = { ...state.viewer, preferences: { ...state.viewer.preferences, ...patch } };
    return clone(state.viewer.preferences);
  },

  async completeRequesterOnboarding() {
    await latency(120);
    state.viewer = {
      ...state.viewer,
      requesterOnboardingCompletedAt: new Date().toISOString(),
    };
    return clone(state.viewer);
  },

  async listProjects() {
    await latency();
    return clone(makeProjects(state.persona));
  },

  async listRequests() {
    await latency();
    return clone(
      [...state.requests]
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
    const projects = makeProjects(state.persona);
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

    state.requests = [created, ...state.requests];
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
      request.spec.approvedByName = state.viewer.displayName;
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
    return clone(makePendingApprovals(state.persona, state.now));
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
