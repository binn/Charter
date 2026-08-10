import type {
  CreateRequestBody,
  Id,
  InstanceInfo,
  PendingApproval,
  Project,
  RequestDetail,
  RequestStreamEvent,
  RequestSummary,
  SendRefinementMessageBody,
  SubmitFeedbackBody,
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

  subscribeToRequest: () => {
    // Wired to the SignalR hub once the backend exposes it (§2.1). Until then the mock supplies
    // the stream, and returning a no-op keeps the real client type-complete.
    return () => {};
  },
};
