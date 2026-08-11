import type {
  AcceptInvitationBody,
  AuditLog,
  AuthProviders,
  CompleteSetupBody,
  ConfirmScopeBody,
  ConnectRepoBody,
  CreateRequestBody,
  FileDiff,
  ForgotPasswordAcknowledgement,
  Id,
  InstanceInfo,
  Member,
  OnboardingAction,
  PairingToken,
  PendingApproval,
  Project,
  PublishPrimerBody,
  Repo,
  RepoAccess,
  RepoAccessGrantBody,
  RepoOnboarding,
  RequestDetail,
  RequestStreamEvent,
  RequestSummary,
  ResetPasswordBody,
  RunnersView,
  SendRefinementMessageBody,
  Session,
  SetMemberRoleBody,
  SetupChecklist,
  SetupStatus,
  SignInBody,
  SmokeTestOutcome,
  SubmitFeedbackBody,
  TranscriptPane,
  TranscriptQuery,
  UserPreferences,
  Viewer,
} from '@/api/types';

/**
 * Every server call the SPA makes, in one interface.
 *
 * Every method here is backed by a route on a running instance. The app still runs against
 * `mockApi` (see `api/mock/mockApi.ts`) unless it is built with `VITE_CHARTER_LIVE_API=true`, which
 * is what keeps the fixtures out of the production bundle entirely — no component imports either
 * implementation directly, they all take `CharterApi` from context.
 *
 * **Keep the two in step.** The mock decides what a viewer may see exactly as the server does, so a
 * refusal is a refusal in both. A mock that answered something the API withholds would be the same
 * lie as a type nothing sends.
 *
 * The endpoint each method calls is written above it. That list *is* the request half of the
 * contract; `api/types.ts` is the response half.
 */
export interface CharterApi {
  /** GET /api/instance */
  getInstance(signal?: AbortSignal): Promise<InstanceInfo>;

  /* ---- First run and sign-in (§30.1, §21) --------------------------------- */

  /**
   * GET /api/setup/status
   *
   * The only route an unclaimed instance answers besides the redemption itself. Everything else
   * under `/api` is 503 until an admin exists, so this is what tells the SPA to render the setup
   * page rather than a sign-in form it would immediately fail against.
   */
  getSetupStatus(signal?: AbortSignal): Promise<SetupStatus>;

  /**
   * POST /api/setup/complete
   *
   * Redeems the one-time token from the container logs, creates **exactly one** admin, and signs
   * them in — the response sets the session cookie. The token then expires and setup mode ends
   * permanently.
   */
  completeSetup(body: CompleteSetupBody, signal?: AbortSignal): Promise<Session>;

  /** GET /api/auth/providers — what this instance actually has configured. */
  getAuthProviders(signal?: AbortSignal): Promise<AuthProviders>;

  /**
   * POST /api/auth/sign-in
   *
   * Rejects with 401 and **one sentence that is the same for an unknown account and a wrong
   * password**, and with 429 when the throttle has had enough. The client must not improve on
   * either: telling the two 401 cases apart is an account-enumeration oracle.
   */
  signIn(body: SignInBody, signal?: AbortSignal): Promise<Session>;

  /** POST /api/auth/sign-out — clears the cookie server-side. */
  signOut(signal?: AbortSignal): Promise<void>;

  /** POST /api/auth/forgot-password — always accepted, always the same sentence, never a link. */
  forgotPassword(email: string, signal?: AbortSignal): Promise<ForgotPasswordAcknowledgement>;

  /**
   * POST /api/auth/reset-password
   *
   * Sets the password and issues **no session**: proving control of a mailbox is enough to choose a
   * password and not enough to be handed a signed-in browser. The page sends them to sign in with it.
   */
  resetPassword(body: ResetPasswordBody, signal?: AbortSignal): Promise<void>;

  /** POST /api/auth/invitations/accept — creates the account and signs them in. */
  acceptInvitation(body: AcceptInvitationBody, signal?: AbortSignal): Promise<Session>;

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

  /* ---- Repo onboarding (§9) ------------------------------------------------ */

  /** GET /api/repos — engineer and admin only; a requester gets `listProjects` instead. */
  listRepos(signal?: AbortSignal): Promise<Repo[]>;

  /** POST /api/repos — §9 step 1, connect. */
  connectRepo(body: ConnectRepoBody, signal?: AbortSignal): Promise<Repo>;

  /** GET /api/repos/{id} — where this repository is in the wizard, and what recon proposed. */
  getRepoOnboarding(id: Id, signal?: AbortSignal): Promise<RepoOnboarding>;

  /** POST /api/repos/{id}/recon — §9 step 2, a read-only agent run over the repository. */
  startRecon(id: Id, signal?: AbortSignal): Promise<OnboardingAction>;

  /**
   * POST /api/repos/{id}/scope
   *
   * §9 step 3. Opens `.charter/config.yml` as a pull request **and queues the smoke test**, so this
   * is the call that starts the run the engineer then watches.
   */
  confirmScope(id: Id, body: ConfirmScopeBody, signal?: AbortSignal): Promise<OnboardingAction>;

  /**
   * GET /api/repos/{id}/smoke-test
   *
   * A read, never a trigger — a GET that could spend money on refresh would be a mistake. Resolves
   * to `null` when no smoke test has run yet, which is a step to do rather than a 404.
   */
  getSmokeTest(id: Id, signal?: AbortSignal): Promise<SmokeTestOutcome | null>;

  /** POST /api/repos/{id}/primer — §9 step 5, publish the primer the engineer edited. */
  publishPrimer(id: Id, body: PublishPrimerBody, signal?: AbortSignal): Promise<OnboardingAction>;

  /**
   * GET /api/repos/{id}/access — who may file against this repository (§7.3, guardrail 1).
   *
   * Deny by default: a newly connected repository comes back with one grant, the person who
   * connected it, and everybody else has to be added deliberately.
   */
  getRepoAccess(id: Id, signal?: AbortSignal): Promise<RepoAccess>;

  /** POST /api/repos/{id}/access — grant or withhold, one row at a time. Returns the new list. */
  setRepoAccess(id: Id, body: RepoAccessGrantBody, signal?: AbortSignal): Promise<RepoAccess>;

  /* ---- Members, roles and the audit log (§7.1) ---------------------------- */

  /** GET /api/members — administrators only; everybody else is refused, not filtered. */
  listMembers(signal?: AbortSignal): Promise<Member[]>;

  /**
   * POST /api/members/{id}/roles — add or remove one role.
   *
   * Refuses two things with a 409 and a sentence: leaving somebody with no role at all, and removing
   * the last administrator on the instance.
   */
  setMemberRole(id: Id, body: SetMemberRoleBody, signal?: AbortSignal): Promise<Member>;

  /** GET /api/audit — the most recent entries, newest first. Administrators only (§7.1). */
  getAuditLog(signal?: AbortSignal): Promise<AuditLog>;

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

/**
 * Thrown for any non-2xx. `status` lets callers distinguish 403 (no access) from 404 (gone), 401
 * (not signed in) from 503 (instance not set up yet).
 *
 * `message` is the server's own `detail` from `application/problem+json` whenever there is one. That
 * matters more here than anywhere else in the app: every sentence the auth endpoints produce is
 * written for the person reading it — "that setup token has expired, restart Charter and use the new
 * token from the logs" — and replacing it with "POST /api/setup/complete returned 400" would throw
 * away the only useful part of the response.
 */
export class ApiError extends Error {
  readonly status: number;

  /** Seconds the server asked us to wait, from `Retry-After`. Only ever set on a 429. */
  readonly retryAfterSeconds?: number;

  constructor(status: number, message: string, retryAfterSeconds?: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    if (retryAfterSeconds !== undefined) {
      this.retryAfterSeconds = retryAfterSeconds;
    }
  }
}

interface ProblemDetails {
  title?: string;
  detail?: string;
}

/** The server's sentence, or a last-resort one. Never an exception string, never a status code. */
async function describeFailure(response: Response, method: string, path: string): Promise<string> {
  try {
    const problem = (await response.json()) as ProblemDetails | null;
    const detail = problem?.detail ?? problem?.title;
    if (typeof detail === 'string' && detail.trim().length > 0) {
      return detail;
    }
  } catch {
    // A body that is not JSON tells us nothing a reader could use; fall through.
  }

  return `${method} ${path} returned ${response.status}`;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    // The session is an HTTP-only cookie the server sets. The client never sees it, never stores
    // it, and never attaches it by hand — this is the whole of the client's involvement.
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...(init?.body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    const method = init?.method ?? 'GET';
    const retryAfter = Number(response.headers.get('Retry-After'));
    throw new ApiError(
      response.status,
      await describeFailure(response, method, path),
      Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter : undefined,
    );
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

  getSetupStatus: (signal) => request<SetupStatus>('/api/setup/status', { signal: signal ?? null }),

  completeSetup: (body, signal) =>
    request<Session>('/api/setup/complete', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  getAuthProviders: (signal) =>
    request<AuthProviders>('/api/auth/providers', { signal: signal ?? null }),

  signIn: (body, signal) =>
    request<Session>('/api/auth/sign-in', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  signOut: (signal) =>
    request<void>('/api/auth/sign-out', { method: 'POST', signal: signal ?? null }),

  forgotPassword: (email, signal) =>
    request<ForgotPasswordAcknowledgement>('/api/auth/forgot-password', {
      method: 'POST',
      body: JSON.stringify({ email }),
      signal: signal ?? null,
    }),

  resetPassword: (body, signal) =>
    request<void>('/api/auth/reset-password', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  acceptInvitation: (body, signal) =>
    request<Session>('/api/auth/invitations/accept', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

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

  // `GET /api/repos` answers `{ repos: [...] }`; the list is what every caller wants.
  listRepos: (signal) =>
    request<{ repos: Repo[] }>('/api/repos', { signal: signal ?? null }).then(
      (response) => response.repos,
    ),

  // `POST /api/repos` answers 201 with the whole wizard state, because connecting is step one of
  // six and the engineer is going straight to it. The caller wants the repository it just made.
  connectRepo: (body, signal) =>
    request<RepoOnboarding>('/api/repos', {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }).then((response) => response.repo),

  getRepoOnboarding: (id, signal) =>
    request<RepoOnboarding>(`/api/repos/${encodeURIComponent(id)}`, { signal: signal ?? null }),

  startRecon: (id, signal) =>
    request<OnboardingAction>(`/api/repos/${encodeURIComponent(id)}/recon`, {
      method: 'POST',
      signal: signal ?? null,
    }),

  confirmScope: (id, body, signal) =>
    request<OnboardingAction>(`/api/repos/${encodeURIComponent(id)}/scope`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  getSmokeTest: (id, signal) =>
    request<SmokeTestOutcome | null>(`/api/repos/${encodeURIComponent(id)}/smoke-test`, {
      signal: signal ?? null,
    }),

  publishPrimer: (id, body, signal) =>
    request<OnboardingAction>(`/api/repos/${encodeURIComponent(id)}/primer`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  getRepoAccess: (id, signal) =>
    request<RepoAccess>(`/api/repos/${encodeURIComponent(id)}/access`, { signal: signal ?? null }),

  setRepoAccess: (id, body, signal) =>
    request<RepoAccess>(`/api/repos/${encodeURIComponent(id)}/access`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  // `GET /api/members` answers `{ members: [...] }`; the list is what every caller wants.
  listMembers: (signal) =>
    request<{ members: Member[] }>('/api/members', { signal: signal ?? null }).then(
      (response) => response.members,
    ),

  setMemberRole: (id, body, signal) =>
    request<Member>(`/api/members/${encodeURIComponent(id)}/roles`, {
      method: 'POST',
      body: JSON.stringify(body),
      signal: signal ?? null,
    }),

  getAuditLog: (signal) => request<AuditLog>('/api/audit', { signal: signal ?? null }),

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
