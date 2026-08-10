import type {
  CreateRequestBody,
  FileDiff,
  Id,
  InstanceInfo,
  PairingToken,
  PendingApproval,
  Project,
  RequestDetail,
  RequestStreamEvent,
  RequestSummary,
  RunnersView,
  SendRefinementMessageBody,
  SetupChecklist,
  SubmitFeedbackBody,
  TranscriptPane,
  TranscriptQuery,
  UserPreferences,
  Viewer,
} from '@/api/types';

/**
 * Every server call the SPA makes, in one interface.
 *
 * The backend does not exist yet, so the app runs against `mockApi` (see `api/mock/mockApi.ts`).
 * Swapping to the real thing is changing which implementation `resolveApi()` returns — no component
 * imports either implementation directly, they all take `CharterApi` from context.
 *
 * The endpoint each method calls is written above it. That list *is* the request half of the
 * contract; `api/types.ts` is the response half.
 */
export interface CharterApi {
  /** GET /api/instance */
  getInstance(signal?: AbortSignal): Promise<InstanceInfo>;

  /** GET /api/me */
  getViewer(signal?: AbortSignal): Promise<Viewer>;

  /** PATCH /api/me/preferences — accepts a partial, returns the full resolved set. */
  updatePreferences(patch: Partial<UserPreferences>, signal?: AbortSignal): Promise<UserPreferences>;

  /** POST /api/me/onboarding/requester/complete */
  completeRequesterOnboarding(signal?: AbortSignal): Promise<Viewer>;

  /** GET /api/projects — only projects the viewer is scoped to and that passed their smoke test. */
  listProjects(signal?: AbortSignal): Promise<Project[]>;

  /** GET /api/requests */
  listRequests(signal?: AbortSignal): Promise<RequestSummary[]>;

  /** GET /api/requests/{id} */
  getRequest(id: Id, signal?: AbortSignal): Promise<RequestDetail>;

  /** POST /api/requests */
  createRequest(body: CreateRequestBody, signal?: AbortSignal): Promise<RequestDetail>;

  /** POST /api/requests/{id}/refinement */
  sendRefinementMessage(
    id: Id,
    body: SendRefinementMessageBody,
    signal?: AbortSignal,
  ): Promise<void>;

  /** POST /api/requests/{id}/spec/{version}/approve — the ownership moment (§10). */
  approveSpec(id: Id, version: number, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/spec/{version}/changes-requested */
  requestSpecChanges(id: Id, version: number, note: string, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/feedback */
  submitFeedback(id: Id, body: SubmitFeedbackBody, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/cancel — must kill the runner and settle token cost (§11). */
  cancelRequest(id: Id, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/artifacts/{artifactId}/rebuild — the `expired` primary action (§27.7). */
  rebuildArtifact(id: Id, artifactId: Id, signal?: AbortSignal): Promise<void>;

  /** GET /api/approvals — the spend gate queue (§7.5). */
  listPendingApprovals(signal?: AbortSignal): Promise<PendingApproval[]>;

  /* ---- Panes 2 and 3 (§12) ------------------------------------------------ */

  /**
   * GET /api/requests/{id}/transcript?cursor=&aroundSeq=&limit=
   *
   * Pane 2 pages backwards from the tail, and jumps to an arbitrary event with `aroundSeq` when a
   * pane-1 milestone is clicked. **403 for a viewer without repo read** — the same rule that keeps
   * `transcript` out of `RequestDetail` (§7.4).
   */
  getTranscript(id: Id, query: TranscriptQuery, signal?: AbortSignal): Promise<TranscriptPane>;

  /**
   * GET /api/requests/{id}/changes/{path}
   *
   * One file's before and after, for Monaco. Per-file rather than bundled into `RequestDetail`
   * because a session can touch a hundred files, and none of them belong in a requester's payload.
   */
  getFileDiff(id: Id, path: string, signal?: AbortSignal): Promise<FileDiff>;

  /* ---- Post-hoc session actions (§7.5) ------------------------------------ */

  /** POST /api/requests/{id}/session/approve */
  approveSession(id: Id, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/session/steer — same branch, same thread. */
  steerSession(id: Id, instruction: string, signal?: AbortSignal): Promise<void>;

  /** POST /api/requests/{id}/session/revise — forks the spec onto a fresh session, same branch. */
  reviseSession(id: Id, revisedSpecMd: string, signal?: AbortSignal): Promise<void>;

  /**
   * POST /api/requests/{id}/session/take-over
   *
   * Marks the session `handed_off` and **stops all further agent writes to the branch**. §7.5 calls
   * concurrent human and agent edits "the one genuinely destructive failure mode in this design",
   * so this must be irreversible server-side rather than a flag the next dispatch can ignore.
   */
  takeOverSession(id: Id, signal?: AbortSignal): Promise<void>;

  /* ---- Settings → Runners (§33.3) ----------------------------------------- */

  /** GET /api/runners — agents plus the sessions currently waiting on one (§27.3). */
  listRunners(signal?: AbortSignal): Promise<RunnersView>;

  /** POST /api/runners/pairing-tokens — single-use, short-TTL, returned exactly once (§33.3). */
  createPairingToken(signal?: AbortSignal): Promise<PairingToken>;

  /**
   * DELETE /api/runners/{agentId}
   *
   * §33.3 step 5: revocation **kills in-flight jobs** and invalidates the credential, instantly.
   */
  revokeAgent(agentId: Id, signal?: AbortSignal): Promise<void>;

  /* ---- Admin setup checklist (§30.2) -------------------------------------- */

  /**
   * GET /api/setup/checklist
   *
   * Resolves to `null` for a viewer who is not an admin — absence, not a 403 the dashboard would
   * have to treat as an error. The dashboard simply has no checklist on it.
   */
  getSetupChecklist(signal?: AbortSignal): Promise<SetupChecklist | null>;

  /** POST /api/setup/checklist/dismiss — allowed only once every task is done. */
  dismissSetupChecklist(signal?: AbortSignal): Promise<SetupChecklist>;

  /**
   * SignalR hub `/hub/requests`, group-joined per request. Returns an unsubscribe function.
   * Transport is deliberately behind this method so the mock can drive the same reducer.
   */
  subscribeToRequest(id: Id, onEvent: (event: RequestStreamEvent) => void): () => void;
}

/** Thrown for any non-2xx. `status` lets callers distinguish 403 (no access) from 404 (gone). */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init?.body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    throw new ApiError(response.status, `${init?.method ?? 'GET'} ${path} returned ${response.status}`);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

/**
 * The real implementation. Complete for `/api/instance`, which exists; every other method is
 * written against the contract in `types.ts` and is unexercised until the backend lands.
 */
export const httpApi: CharterApi = {
  getInstance: (signal) => request<InstanceInfo>('/api/instance', { signal: signal ?? null }),

  getViewer: (signal) => request<Viewer>('/api/me', { signal: signal ?? null }),

  updatePreferences: (patch, signal) =>
    request<UserPreferences>('/api/me/preferences', {
      method: 'PATCH',
      body: JSON.stringify(patch),
      signal: signal ?? null,
    }),

  completeRequesterOnboarding: (signal) =>
    request<Viewer>('/api/me/onboarding/requester/complete', {
      method: 'POST',
      signal: signal ?? null,
    }),

  listProjects: (signal) => request<Project[]>('/api/projects', { signal: signal ?? null }),

  listRequests: (signal) => request<RequestSummary[]>('/api/requests', { signal: signal ?? null }),

  getRequest: (id, signal) =>
    request<RequestDetail>(`/api/requests/${encodeURIComponent(id)}`, { signal: signal ?? null }),

  createRequest: (body, signal) =>
    request<RequestDetail>('/api/requests', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  sendRefinementMessage: (id, body, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/refinement`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  approveSpec: (id, version, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/spec/${version}/approve`, {
      method: 'POST',
      signal: signal ?? null,
    }),

  requestSpecChanges: (id, version, note, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/spec/${version}/changes-requested`, {
      method: 'POST',
      body: JSON.stringify({ note }),
      signal: signal ?? null,
    }),

  submitFeedback: (id, body, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/feedback`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  cancelRequest: (id, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/cancel`, {
      method: 'POST',
      signal: signal ?? null,
    }),

  rebuildArtifact: (id, artifactId, signal) =>
    request<void>(
      `/api/requests/${encodeURIComponent(id)}/artifacts/${encodeURIComponent(artifactId)}/rebuild`,
      { method: 'POST', signal: signal ?? null },
    ),

  listPendingApprovals: (signal) =>
    request<PendingApproval[]>('/api/approvals', { signal: signal ?? null }),

  getTranscript: (id, query, signal) => {
    const search = new URLSearchParams();
    if (query.cursor !== undefined) search.set('cursor', query.cursor);
    if (query.aroundSeq !== undefined) search.set('aroundSeq', String(query.aroundSeq));
    if (query.limit !== undefined) search.set('limit', String(query.limit));
    const qs = search.size > 0 ? `?${search.toString()}` : '';
    return request<TranscriptPane>(
      `/api/requests/${encodeURIComponent(id)}/transcript${qs}`,
      { signal: signal ?? null },
    );
  },

  getFileDiff: (id, path, signal) =>
    request<FileDiff>(
      // The path is a path, so it is encoded as one segment rather than interpolated raw.
      `/api/requests/${encodeURIComponent(id)}/changes/${encodeURIComponent(path)}`,
      { signal: signal ?? null },
    ),

  approveSession: (id, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/session/approve`, {
      method: 'POST',
      signal: signal ?? null,
    }),

  steerSession: (id, instruction, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/session/steer`, {
      method: 'POST',
      body: JSON.stringify({ instruction }),
      signal: signal ?? null,
    }),

  reviseSession: (id, revisedSpecMd, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/session/revise`, {
      method: 'POST',
      body: JSON.stringify({ revisedSpecMd }),
      signal: signal ?? null,
    }),

  takeOverSession: (id, signal) =>
    request<void>(`/api/requests/${encodeURIComponent(id)}/session/take-over`, {
      method: 'POST',
      signal: signal ?? null,
    }),

  listRunners: (signal) => request<RunnersView>('/api/runners', { signal: signal ?? null }),

  createPairingToken: (signal) =>
    request<PairingToken>('/api/runners/pairing-tokens', {
      method: 'POST',
      signal: signal ?? null,
    }),

  revokeAgent: (agentId, signal) =>
    request<void>(`/api/runners/${encodeURIComponent(agentId)}`, {
      method: 'DELETE',
      signal: signal ?? null,
    }),

  getSetupChecklist: (signal) =>
    request<SetupChecklist | null>('/api/setup/checklist', { signal: signal ?? null }),

  dismissSetupChecklist: (signal) =>
    request<SetupChecklist>('/api/setup/checklist/dismiss', {
      method: 'POST',
      signal: signal ?? null,
    }),

  subscribeToRequest: () => {
    // Wired to the SignalR hub once the backend exposes it (§2.1). Until then the mock supplies
    // the stream, and returning a no-op keeps the real client type-complete.
    return () => {};
  },
};
