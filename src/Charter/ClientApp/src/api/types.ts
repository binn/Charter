/**
 * The Charter HTTP API contract, as the SPA understands it.
 *
 * This file is the frontend's half of a contract the backend does not implement yet. It is written
 * to be read by whoever builds the minimal APIs: every endpoint the SPA calls appears in
 * `api/client.ts`, and every shape those endpoints return appears here.
 *
 * Three rules govern everything below, and all three come from the spec:
 *
 * 1. **Authorisation is not a rendering concern** (§7.4, §27.7). Fields a viewer may not see are
 *    *absent from the response*, and are therefore optional here. The client decides what to draw
 *    from whether a field is present, never from the viewer's role. `RequesterSpec` has no
 *    `technicalApproach` property at all — not an optional one — because the requester's payload
 *    must not carry it even as `undefined`.
 * 2. **Never an ETA** (§6). No shape in this file carries a predicted completion time. Timestamps
 *    are starts, ends, and expiries only; everything the UI shows about duration is computed
 *    backwards from `startedAt`.
 * 3. **`acceptanceCriteria` are the contract** (§10b). They are authored in plain language, shared
 *    verbatim between the requester and engineer renderings of a Spec, and rendered verbatim as the
 *    "What to check" list on the verification artifact card. They are never regenerated per surface.
 *
 * JSON is camelCase. Timestamps are ISO 8601 with an offset. Ids are opaque strings (UUIDs
 * server-side); the client never parses them.
 */

export type Iso8601 = string;
export type Id = string;

/* -------------------------------------------------------------------------- */
/* Instance — GET /api/instance (already implemented, see Program.cs)          */
/* -------------------------------------------------------------------------- */

/**
 * AGPL §13 compliance data. The footer renders `sourceUrl` on every page; it is a licence
 * obligation for a network-interactive instance, not a credit link.
 */
export interface InstanceInfo {
  version: string;
  commit: string;
  buildDate: Iso8601;
  sourceUrl: string;
  license: string;
  serviceName: string;
}

/* -------------------------------------------------------------------------- */
/* First run and sign-in (§30.1, §21)                                         */
/* -------------------------------------------------------------------------- */

/**
 * `GET /api/setup/status` — the one thing an unclaimed instance will tell anybody.
 *
 * Everything else under `/api` answers 503 until an admin exists, which is why this is the first
 * call the app makes when `/api/me` refuses it.
 */
export interface SetupStatus {
  setupRequired: boolean;
}

/**
 * `POST /api/setup/complete`.
 *
 * `token` is the one-time value the server wrote to **stdout** on boot. There is no endpoint that
 * reads it back and no default password: the operator reads it from the container logs (§30.1), and
 * saying so on the page is the difference between a two-minute setup and a filed issue.
 */
export interface CompleteSetupBody {
  token: string;
  email: string;
  displayName: string;
  password: string;
  /** Optional — §30.2 asks for it again on the dashboard checklist. */
  organizationName?: string;
}

export type AuthProviderStyle = 'credential' | 'redirect';

/**
 * One sign-in method **this instance actually has configured**.
 *
 * The sign-in page renders buttons from this list and from nothing else. A provider the operator
 * never configured must never appear, because a button that leads to a misconfiguration error is
 * worse than no button.
 */
export interface AuthProvider {
  /** Stable key: `password`, `github`, `google`, … Also what labels the button. */
  name: string;
  style: AuthProviderStyle;
  /** Where the browser goes for a redirect sign-in. Absent for the password form. */
  startUrl?: string;
}

export interface AuthProviders {
  providers: AuthProvider[];
  /**
   * False when this instance cannot send email, so the page says who to ask instead of offering a
   * reset button that could never deliver anything.
   */
  selfServicePasswordReset: boolean;
}

export interface SignInBody {
  email: string;
  password: string;
}

/**
 * What a successful sign-in, setup completion or invitation acceptance answers with — and what
 * `GET /api/auth/session` returns.
 *
 * **No token appears here, and none ever will.** The session is an HTTP-only cookie the server sets
 * on this response; the client cannot read it, does not store it, and has no way to attach it by
 * hand. This object exists so the page can greet the right person before `GET /api/me` lands.
 */
export interface Session {
  userId: Id;
  displayName: string;
  email: string;
  organizationId: Id;
  roles: Role[];
  /** Which provider this session signed in with. */
  provider: string;
}

/** `POST /api/auth/invitations/accept` (§30.2). Ends signed in. */
export interface AcceptInvitationBody {
  token: string;
  /** What they want to be called. Ignored when the account already exists. */
  displayName: string;
  password: string;
}

/** `POST /api/auth/reset-password`. Sets the password; does **not** issue a session. */
export interface ResetPasswordBody {
  token: string;
  password: string;
}

/**
 * `POST /api/auth/forgot-password`.
 *
 * The same sentence comes back for an address with an account and for one without — anybody can type
 * anybody's address into that form, so a different answer would be an enumeration oracle. The
 * response carries a message and never a link.
 */
export interface ForgotPasswordAcknowledgement {
  message: string;
}

/* -------------------------------------------------------------------------- */
/* Viewer — GET /api/me, PATCH /api/me/preferences                            */
/* -------------------------------------------------------------------------- */

/** §7.1. Additive — a member may hold several. */
export type Role = 'requester' | 'approver' | 'engineer' | 'admin';

/** §13. Named for what the reader wants, never for what they lack. */
export type TeachingLevel = 'explain_everything' | 'skip_the_basics' | 'just_the_decisions';

/** §12. Named for the user: Simple / Detailed / Developer. */
export type PanePreference = 'simple' | 'detailed' | 'developer';

export type ThemePreference = 'system' | 'light' | 'dark';

/**
 * Preferences live server-side against the user record. There is no browser storage in this app,
 * so this object is the only place a preference exists on the client, and it is refetched rather
 * than cached across reloads.
 */
export interface UserPreferences {
  theme: ThemePreference;
  pane: PanePreference;
  teachingLevel: TeachingLevel;
}

/**
 * Server-computed capabilities. These drive *navigation and affordances only* — which links exist,
 * whether a button is offered. They never gate the rendering of data, because data a viewer may not
 * see is not in the payload. Keeping the role arithmetic server-side also means the client cannot
 * drift from the authorisation code path (§7.2).
 */
export interface ViewerCapabilities {
  canFileRequests: boolean;
  canApproveSpend: boolean;
  /** Repo read access. Governs whether the server *sends* transcripts, diffs and engineer details. */
  canReadRepos: boolean;
  canAdminister: boolean;
}

export interface Viewer {
  id: Id;
  displayName: string;
  email: string;
  organization: { id: Id; name: string };
  roles: Role[];
  capabilities: ViewerCapabilities;
  preferences: UserPreferences;
  /** §30.4. Null until the requester has completed the three onboarding screens. */
  requesterOnboardingCompletedAt?: Iso8601;
}

/* -------------------------------------------------------------------------- */
/* Projects — GET /api/projects                                               */
/* -------------------------------------------------------------------------- */

/**
 * The requester-facing projection of a Repo.
 *
 * §7.1: a requester never sees a repo name, branch, diff, or token count. `name` is the operator's
 * display name for the project, never `owner/repo`. A repo that has not passed its smoke test (§9)
 * is not in this list at all, and a repo the viewer is not scoped to (§7.3, deny by default) is not
 * in this list either — absence is the enforcement, not a disabled state.
 */
export interface Project {
  id: Id;
  name: string;
  description?: string;
  /** §8 `primer.md`, rendered as Markdown. "How this app is put together", for requesters. */
  primerMd?: string;
  templates: RequestTemplate[];
}

/**
 * §8 `templates/`. "A requester picking 'change some text' instead of free-typing skips half the
 * refinement round-trips. Cheapest quality win available."
 */
export interface RequestTemplate {
  id: Id;
  name: string;
  /** One line, requester-facing: what this template is for. */
  description: string;
  /** Stable key the client maps to an icon. Unknown values fall back to a generic mark. */
  icon?: 'text' | 'bug' | 'field' | 'layout' | 'export' | 'access' | 'generic';
  /** Seed text placed into the intake box, or a scaffold with `{{field}}` placeholders. */
  prompt: string;
  /** Optional guided fields. When present the intake form renders them instead of a bare box. */
  fields?: RequestTemplateField[];
}

export interface RequestTemplateField {
  key: string;
  label: string;
  placeholder?: string;
  required: boolean;
  multiline: boolean;
}

/* -------------------------------------------------------------------------- */
/* Repositories — the §9 onboarding wizard                                     */
/* -------------------------------------------------------------------------- */

/**
 * Where a repository has got to in §9. A requester never sees any of this — `GET /api/repos` is
 * engineer and admin only, and a requester's payload is `Project`, which carries no repository.
 */
export type RepoStatus = 'pending' | 'recon' | 'configuring' | 'smoke_test' | 'ready' | 'disabled';

export type OnboardingStepId =
  | 'connect'
  | 'recon'
  | 'confirm_scope'
  | 'smoke_test'
  | 'primer'
  | 'merge_gate';

export interface Repo {
  id: Id;
  /** `owner/name`, as the provider spells it. */
  fullName: string;
  baseBranch: string;
  status: RepoStatus;
  /** §9: false until the smoke test passes. Readiness is earned, never set by hand. */
  requesterVisible: boolean;
  hasPrimer: boolean;
  connectedAt: Iso8601;
  updatedAt: Iso8601;
}

export interface OnboardingStep {
  id: OnboardingStepId;
  label: string;
  done: boolean;
  /** True for the one step the engineer should do next. */
  current: boolean;
}

/**
 * One file or folder recon proposed a decision about (§9 step 3).
 *
 * `locked` marks the deny-by-default floor — migrations, auth, CI config, infra, secrets. The server
 * filters whatever the client sends back through that floor regardless, so these render as denied
 * with the reason attached rather than as a toggle that would silently not take effect.
 */
export interface ScopeEntry {
  path: string;
  kind: 'file' | 'directory';
  allowed: boolean;
  locked?: boolean;
  /** Why recon proposed this — "database migrations", "how people sign in". */
  reason?: string;
}

/**
 * What the recon session found.
 *
 * **Not yet served by `GET /api/repos/{id}`** — the endpoint reports the steps and the pull request
 * but not recon's own output, so this is the frontend's half of a contract the control plane has
 * still to fill in. Absent means recon has not proposed a scope yet, and the wizard says so rather
 * than rendering an empty tree.
 */
export interface ScopeProposal {
  /** "ASP.NET Core 10", "React 19" — recon's detected stack, shown verbatim. */
  detectedStack: string[];
  /** Test and build commands recon found, so the engineer can sanity-check them. */
  commands: { label: string; command: string }[];
  /** §9: an existing `CLAUDE.md` / `AGENTS.md` is imported and extended, never overwritten. */
  importedFrom?: string[];
  entries: ScopeEntry[];
}

/** One of the six integration points the smoke test proves (§9 step 4). */
export type SmokeTestCheckpointId =
  | 'request_filed'
  | 'agent_ran'
  | 'checks_passed'
  | 'pull_request'
  | 'preview_deployed'
  | 'url_bound';

export interface SmokeTestCheckpoint {
  id: SmokeTestCheckpointId;
  label: string;
  state: 'pending' | 'running' | 'passed' | 'failed' | 'skipped';
  /** One line, engineer-facing: what this step actually did. */
  detail?: string;
}

/**
 * The last smoke test.
 *
 * §9's point is that onboarding **ends in proof**: "nothing else validates all six integration
 * points at once". `checkpoints` is what makes that watchable rather than a boolean — the server
 * sends them as the run progresses. Absent, the wizard reconstructs what it can prove from
 * `pullRequestNumber` and `previewBound` and says the rest is unknown.
 */
export interface SmokeTestOutcome {
  passed: boolean;
  at: Iso8601;
  /** The change request the smoke test opened, when it got that far. */
  pullRequestNumber?: number;
  /** §18: whether the preview URL bound back to the change request. */
  previewBound: boolean;
  checkpoints?: SmokeTestCheckpoint[];
  /** §9 seed data: an empty preview **warns rather than blocks**. */
  warnings?: string[];
}

/**
 * §7.4, the one place the trust boundary weakens: "the guarantee is only as strong as the provider
 * makes it". `advisory` means Charter still will not merge and cannot stop anyone else from doing
 * so, and the wizard has to say that in words rather than in a colour.
 */
export interface MergeGate {
  enforcement: 'provider_enforced' | 'advisory';
  branch: string;
  /** Whether a protection rule covers the base branch at all. Supported is not configured. */
  protectionConfigured: boolean;
  requiresReview: boolean;
  checkedAt: Iso8601;
  /** The plain warning, when the gate is advisory. Absent when it is enforced. */
  warning?: string;
}

/** `GET /api/repos/{id}` — where this repository is in §9. */
export interface RepoOnboarding {
  repo: Repo;
  steps: OnboardingStep[];
  /** The scope-config pull request, once recon has proposed one. */
  scopeConfigPullRequest?: number;
  lastSmokeTest?: SmokeTestOutcome;
  mergeGate?: MergeGate;
  proposedScope?: ScopeProposal;
  /** The primer draft the agent wrote, for the engineer to edit before publishing (§9 step 5). */
  primerDraftMd?: string;
}

/** `POST /api/repos` (§9 step 1). */
export interface ConnectRepoBody {
  fullName: string;
  /** The GitHub App installation that grants access to it. */
  installationId?: number;
  /** Defaults to `main`. */
  baseBranch?: string;
}

/**
 * `POST /api/repos/{id}/scope` (§9 step 3).
 *
 * Sending neither list accepts what recon proposed. Whatever arrives is filtered through the
 * deny-by-default floor server-side, so a client cannot widen scope past it.
 */
export interface ConfirmScopeBody {
  allow?: string[];
  deny?: string[];
}

/** What one onboarding step did. */
export interface OnboardingAction {
  status: RepoStatus;
  /** One line, safe to show an engineer. */
  explanation: string;
  /** Anything odd but survivable — a refused path, an empty preview. */
  warnings: string[];
  pullRequestNumber?: number;
  pullRequestUrl?: string;
}

/** `POST /api/repos/{id}/primer` (§9 step 5). */
export interface PublishPrimerBody {
  markdown: string;
}

/* -------------------------------------------------------------------------- */
/* Requests — the state machine (§6)                                          */
/* -------------------------------------------------------------------------- */

export type RequestStatus =
  | 'draft'
  | 'refining'
  | 'spec_ready'
  | 'rejected'
  | 'queued'
  | 'running'
  | 'needs_input'
  | 'pr_open'
  | 'preview_ready'
  | 'in_review'
  | 'merged'
  | 'no_changes_needed'
  | 'failed'
  | 'cancelled'
  | 'stale';

export interface RequestSummary {
  id: Id;
  projectId: Id;
  projectName: string;
  /** The Spec title once one exists; before that, a trimmed first line of the raw request. */
  title: string;
  status: RequestStatus;
  createdAt: Iso8601;
  updatedAt: Iso8601;
  /** Latest translated milestone label (§11), for the list row. Never raw transcript text. */
  lastMilestoneLabel?: string;
  /**
   * True only for the two states that notify (§6): `needs_input` and `preview_ready`. Computed
   * server-side so the badge, the email, and the row can never disagree.
   */
  needsAttention: boolean;
  /** Present when `status === 'spec_ready'` and auto-dispatch did not apply (§7.5). */
  awaitingApprovalFrom?: string;
}

export interface RequestDetail extends RequestSummary {
  rawText: string;
  templateId?: Id;

  /** §10b. Requester rendering only. See `RequesterSpec` for what is deliberately absent. */
  spec?: RequesterSpec;

  /** §10. The refinement conversation. A chat surface, not a form. */
  refinement: RefinementThread;

  /** §11. One thread per request, forever. Multiple sessions collapse into it. */
  thread: StatusThread;

  /** §27.1. A session may produce several; they render as tabs in one card, never as many cards. */
  artifacts: VerificationArtifact[];

  /** §11. Must actually kill the runner and settle token cost, so the server owns this flag. */
  cancellable: boolean;

  /**
   * Pane 2 (§12). **Omitted entirely unless the viewer has repo read access** (§7.4) — transcripts
   * leak file paths, environment variable names and error output. Absence is the permission check.
   */
  transcript?: TranscriptPane;

  /** Pane 3 (§12). Omitted on the same terms as `transcript`. Viewer, not editor, in v1. */
  changes?: ChangesPane;

  /**
   * §14. The engineer recap. Omitted on the same terms as `transcript` — it names files, branches
   * and deviations, and is written for someone who will read the diff.
   */
  recap?: EngineerRecap;

  /**
   * §7.5. The four post-hoc actions. Omitted entirely for a viewer who may perform none of them,
   * so the engineer controls are absent rather than disabled.
   */
  sessionActions?: SessionActions;
}

/* -------------------------------------------------------------------------- */
/* Spec (§10b)                                                                */
/* -------------------------------------------------------------------------- */

/**
 * An acceptance criterion. Authored in plain language, shared byte-for-byte between the requester
 * view, the engineer view, and the "What to check" list on the artifact card. If these can drift,
 * "the spec said X" stops meaning anything and §10's accountability model is gone.
 */
export interface AcceptanceCriterion {
  id: Id;
  text: string;
}

/**
 * The requester's rendering of a Spec.
 *
 * The structured Spec server-side additionally holds `technical_approach`, `scope { files, paths }`
 * and `risks[]`. **Those fields must not appear in this payload**, which is why there is no
 * optional property for them here: an optional property invites `spec.technicalApproach && ...`,
 * and that is exactly the CSS-hiding pattern §7.4 forbids. The engineer rendering is a different
 * endpoint and a different type.
 */
export interface RequesterSpec {
  id: Id;
  version: number;
  title: string;
  /** Plain language: what the requester will see change. */
  outcome: string;
  acceptanceCriteria: AcceptanceCriterion[];
  /** Refinement refuses to dispatch anything still ambiguous (§10); these are what is still open. */
  openQuestions?: string[];
  /** §8 glossary.yml terms referenced by this spec, for the inline Explain lens (§10b). */
  glossary?: GlossaryTerm[];
  approvedAt?: Iso8601;
  approvedByName?: string;
}

export interface GlossaryTerm {
  term: string;
  definition: string;
}

/* -------------------------------------------------------------------------- */
/* Refinement conversation (§10, §10b)                                        */
/* -------------------------------------------------------------------------- */

/** §10b. Modes of one conversation surface, promotable chat -> plan -> build without losing history. */
export type RefinementMode = 'chat' | 'plan' | 'build';

export type RefinementAuthor = 'requester' | 'charter';

export type RefinementMessageKind =
  /** Ordinary conversational turn. */
  | 'message'
  /** A clarifying question. The client may render `choices` as quick replies. */
  | 'question'
  /**
   * Refinement refused to produce a spec — typically because it would touch a denied path (§8).
   * `body` is the plain-English explanation; the request routes to an engineer.
   */
  | 'refusal'
  /** Charter has proposed a Spec version. The confirmation card renders below the thread. */
  | 'spec_proposed';

export interface RefinementChoice {
  id: Id;
  label: string;
}

export interface RefinementMessage {
  id: Id;
  author: RefinementAuthor;
  kind: RefinementMessageKind;
  body: string;
  createdAt: Iso8601;
  choices?: RefinementChoice[];
  /** Set on `spec_proposed`; identifies which Spec version this message introduced. */
  specVersion?: number;
  /** Set on `refusal`. The requester is told a human is now involved, not left at a dead end. */
  routedToEngineer?: boolean;
}

export interface RefinementThread {
  mode: RefinementMode;
  messages: RefinementMessage[];
  /** True while Charter is composing. Drives the typing indicator; carries no time estimate. */
  charterIsThinking: boolean;
  /** False once a spec is approved or the request has been dispatched. */
  canReply: boolean;
}

/* -------------------------------------------------------------------------- */
/* Status thread (§11)                                                        */
/* -------------------------------------------------------------------------- */

/**
 * §11: promote roughly four event types into pane 1. These are the translated milestones, never the
 * raw transcript. `status` covers thread-level notes (dispatched, approved, cancelled) and
 * `outcome` covers terminal entries.
 */
export type MilestoneKind =
  | 'understanding'
  | 'changing'
  | 'checking'
  | 'assembling'
  | 'status'
  | 'question'
  | 'outcome';

export type MilestoneState = 'active' | 'done' | 'failed';

export interface Milestone {
  id: Id;
  kind: MilestoneKind;
  /** Already translated to plain language server-side. The client does not paraphrase it. */
  label: string;
  detail?: string;
  /** §13. Teaching annotation, one sentence, generated lazily and only if the user opted in. */
  annotationMd?: string;
  occurredAt: Iso8601;
  state: MilestoneState;
  /**
   * §12: clicking a milestone in pane 1 scrolls pane 2 to the events that produced it. Present only
   * when `transcript` is present, i.e. only for viewers with repo read access.
   */
  eventSeq?: number;
}

export interface StatusThread {
  /** True while a session is live — the client keeps a stream open and shows elapsed time. */
  live: boolean;
  milestones: Milestone[];
  /** Elapsed time is computed from this. There is no estimated finish anywhere in the API. */
  startedAt?: Iso8601;
  endedAt?: Iso8601;
  /**
   * §11. Plain language, dignified. "This turned out to be bigger than expected." Budget
   * exhaustion, a stuck agent and failing checks all arrive here as the same sentence; the real
   * detail goes to the engineer view. Never a stack trace.
   */
  failureSummary?: string;
  feedback?: FeedbackRecord;
}

/** §11. Two buttons. Do not make them write a bug report. */
export type FeedbackVerdict = 'works' | 'not_quite';

export interface FeedbackRecord {
  verdict: FeedbackVerdict;
  /** Optional free text captured after "Not quite". Becomes a new session on the same spec. */
  note?: string;
  submittedAt: Iso8601;
}

/* -------------------------------------------------------------------------- */
/* Verification artifacts (§27.1, §27.7)                                      */
/* -------------------------------------------------------------------------- */

export type ArtifactKind =
  | 'hosted_preview'
  | 'build_artifact'
  | 'distribution_channel'
  | 'capture'
  | 'ephemeral_instance'
  | 'test_report'
  | 'hil_report'
  | 'none';

export type ArtifactState = 'pending' | 'ready' | 'expiring' | 'expired' | 'failed';

export type ArtifactAudience = 'requester' | 'engineer_only';

/**
 * §27.7. PR number, commit SHA, branch, runner, duration and cost.
 *
 * **Omitted by the API when the viewer lacks repo read access**, not hidden with CSS. Requesters
 * never see a SHA. The client's only test is `artifact.details !== undefined`.
 */
export interface EngineerDetails {
  changeRequestNumber: number;
  changeRequestUrl: string;

  /**
   * What this provider calls a change request, supplied by the server rather than hardcoded:
   * "pull request" on GitHub and Gitea, "merge request" on GitLab, "changelist" on Perforce.
   * Never assume GitHub's vocabulary in the UI.
   */
  changeRequestTerm: string;

  /** The short form of the same term - "PR", "MR", "CL". */
  changeRequestTermShort: string;
  commitSha: string;
  branch: string;
  runner: string;
  durationMs: number;
  costUsd: number;
  recapUrl?: string;
}

interface ArtifactBase {
  id: Id;
  state: ArtifactState;
  audience: ArtifactAudience;
  /** Tab label when a session produced several artifacts. Short: "Preview", "TestFlight". */
  label: string;
  /** Exactly one artifact per session is primary; it is the first tab and the default selection. */
  primary: boolean;
  /** §27.5. Mandatory for anything stored in object storage. Drives the countdown and `expired`. */
  expiresAt?: Iso8601;
  /** §27.1 `instructions_md` — how to actually use this thing. */
  instructionsMd?: string;
  /** Set when `state === 'failed'`. Plain language, requester-safe. */
  failureSummary?: string;
  details?: EngineerDetails;
}

export interface HostedPreviewPayload {
  url: string;
  /** Pre-truncated for the chip. The full `url` is what gets opened and copied. */
  displayUrl: string;
  reachability: 'checking' | 'reachable' | 'unreachable' | 'unknown';
}

export type BuildPlatform =
  | 'android'
  | 'ios'
  | 'windows'
  | 'macos'
  | 'linux'
  | 'embedded'
  | 'other';

export interface BuildArtifactPayload {
  platform: BuildPlatform;
  filename: string;
  sizeBytes: number;
  checksumAlgorithm: string;
  /** Already shortened server-side; the card shows it verbatim. */
  checksumShort: string;
  downloadUrl: string;
  installInstructionsMd?: string;
}

export interface DistributionChannelPayload {
  provider: 'testflight' | 'play_internal' | 'firebase' | 'expo_eas' | 'other';
  /** Rendered as "TestFlight · Build 42". */
  channelName: string;
  buildNumber?: string;
  /** Deep link. Also what the QR encodes. */
  openUrl: string;
  inviteRequired: boolean;
  inviteNote?: string;
}

export interface CaptureItem {
  id: Id;
  mediaType: 'image' | 'video';
  url: string;
  /** Video poster frame. Ignored for images. */
  posterUrl?: string;
  caption: string;
  /** Present when a baseline exists; enables the before/after toggle. */
  baselineUrl?: string;
  width?: number;
  height?: number;
}

export interface CapturePayload {
  items: CaptureItem[];
}

export interface EphemeralInstancePayload {
  protocol: string;
  connectString: string;
  region?: string;
  note?: string;
}

export interface TestFailure {
  id: Id;
  name: string;
  suite?: string;
  /** The assertion text as the runner emitted it. Shown expanded, never re-worded. */
  assertion: string;
}

export interface TestReportPayload {
  passed: number;
  failed: number;
  skipped: number;
  durationMs: number;
  reportUrl?: string;
  failures: TestFailure[];
}

export interface HilTrace {
  id: Id;
  label: string;
  /** Image of a scope capture, a plotted signal, or similar. */
  imageUrl?: string;
  summary?: string;
}

export interface HilReportPayload {
  deviceId: string;
  deviceLabel: string;
  runDurationMs: number;
  outcome: 'pass' | 'fail';
  traces: HilTrace[];
  reportUrl?: string;
}

export interface NoVerificationPayload {
  /**
   * §27.4: "do not pretend parity". Plain explanation that this change type is engineer-verified
   * and the requester will be told when it is live.
   */
  explanation: string;
}

/**
 * Discriminated on `kind` so the card's polymorphic body is exhaustive at compile time. Adding a
 * kind server-side without adding a body here is a type error, which is the intent.
 */
export type VerificationArtifact =
  | (ArtifactBase & { kind: 'hosted_preview'; payload: HostedPreviewPayload })
  | (ArtifactBase & { kind: 'build_artifact'; payload: BuildArtifactPayload })
  | (ArtifactBase & { kind: 'distribution_channel'; payload: DistributionChannelPayload })
  | (ArtifactBase & { kind: 'capture'; payload: CapturePayload })
  | (ArtifactBase & { kind: 'ephemeral_instance'; payload: EphemeralInstancePayload })
  | (ArtifactBase & { kind: 'test_report'; payload: TestReportPayload })
  | (ArtifactBase & { kind: 'hil_report'; payload: HilReportPayload })
  | (ArtifactBase & { kind: 'none'; payload: NoVerificationPayload });

/* -------------------------------------------------------------------------- */
/* Panes 2 and 3 (§12) — present only with repo read access                   */
/* -------------------------------------------------------------------------- */

/**
 * A stable classification of an event, independent of which agent CLI produced it.
 *
 * §12b makes adapters data rather than code, so `type` below is whatever the adapter's own event
 * stream called this thing and must be shown verbatim — but the client cannot switch on it. `kind`
 * is the adapter-independent projection the adapter's `events.map` block resolves to, and it is
 * what pane 2 draws icons and linkage from.
 */
export type TranscriptEventKind =
  | 'tool_use'
  | 'file_write'
  | 'command'
  | 'message'
  | 'diagnostic'
  | 'lifecycle';

export interface TranscriptEvent {
  seq: number;
  kind: TranscriptEventKind;
  /** The adapter's own event name. Rendered verbatim; never parsed. */
  type: string;
  summary: string;
  createdAt: Iso8601;
  /** Set on `file_write`; clicking the event opens pane 3 at this path (§12). */
  path?: string;
  /**
   * §12: "clicking a file-write event in pane 2 opens pane 3 **at that hunk**". Index into the
   * `hunks` of that path's `FileDiff`. Absent when the write could not be attributed to one hunk.
   */
  hunkIndex?: number;
  /**
   * The pane-1 milestone this event was promoted into or sits underneath. The reverse of
   * `Milestone.eventSeq`, and what lets pane 2 mark the run of events a milestone produced rather
   * than only its first line — that marked run is what makes the linkage teach (§12).
   */
  milestoneId?: Id;
  /**
   * §27.7's rule generalises: never colour alone. The client pairs this with an icon and a word.
   */
  level?: 'info' | 'warning' | 'error';
}

export interface TranscriptPane {
  /**
   * One page, oldest-first within the page. A long session is tens of thousands of events, so this
   * is never the whole stream.
   */
  events: TranscriptEvent[];
  /**
   * Cursor for the page *before* this one — pane 2 pages backwards from the live tail. `null` when
   * the beginning of the session has been reached.
   */
  nextCursor: string | null;
  /** Total events in the session, so the pane can say "of 12,480" without loading them. */
  totalCount: number;
}

/** Which page of the transcript to fetch. All three are mutually exclusive. */
export interface TranscriptQuery {
  /** Page backwards from a cursor returned by a previous call. */
  cursor?: string;
  /**
   * §12 linkage: centre the window on this event. Needed because a milestone can point at event
   * 12 of 12,480 and paging backwards to reach it is not a user experience.
   */
  aroundSeq?: number;
  limit?: number;
}

export interface ChangedFile {
  path: string;
  additions: number;
  deletions: number;
  /** §14: auth, migrations, money math and external calls float to the top. */
  risk: 'high' | 'medium' | 'low';
  /**
   * Why this file ranks where it does — "touches authentication", "database migration". §14's
   * ranking is only useful if the reviewer can see the reasoning; an unexplained "high" is noise.
   */
  riskReasons?: string[];
}

export interface ChangesPane {
  /** **Server-ordered, risk-first (§14).** The client does not re-sort; that would discard it. */
  files: ChangedFile[];
}

/** One contiguous run of changed lines, as the diff tool found them. */
export interface DiffHunk {
  id: Id;
  /** "@@ -12,7 +12,9 @@ …" — shown verbatim as the hunk's label. */
  header: string;
  /** 1-based line in the modified file. Pane 3 reveals this line when the hunk is selected. */
  modifiedStartLine: number;
  originalStartLine: number;
}

/**
 * One file's before and after, for Monaco's `DiffEditor` (§3, §12).
 *
 * Fetched per file rather than shipped inside `RequestDetail`: a session can touch a hundred files
 * and the requester's payload must not carry any of them.
 */
export interface FileDiff {
  path: string;
  /** Monaco language id, resolved from the path server-side. `plaintext` when unrecognised. */
  language: string;
  /** Empty string when the file was added. */
  originalText: string;
  /** Empty string when the file was deleted. */
  modifiedText: string;
  hunks: DiffHunk[];
  /** No text to show. The pane says so rather than rendering an empty editor. */
  binary: boolean;
  /** Very large file: `modifiedText` is a prefix. The pane says so and links out. */
  truncated: boolean;
}

/* -------------------------------------------------------------------------- */
/* Engineer recap (§14)                                                       */
/* -------------------------------------------------------------------------- */

/**
 * §14's highest-value section: where the agent departed from the spec, or made a call the spec did
 * not cover. `specSaid` is absent for the second case, and the difference matters — "the spec said
 * X and it did Y" and "the spec was silent and it chose Y" need different amounts of scrutiny.
 */
export interface RecapDeviation {
  id: Id;
  specSaid?: string;
  agentDid: string;
  /** Where to look first. Links pane 3 straight to the file. */
  path?: string;
}

export interface RecapNote {
  id: Id;
  text: string;
}

/**
 * §14. Structurally the walkthrough (§13) with the opposite audience: same event stream, different
 * prompt.
 *
 * **It must never say "looks good."** That is a rule about generation, but the client honours it
 * too: nothing here is rendered as a verdict, there is no pass/fail badge on this object, and the
 * card labels itself an orientation aid. The moment it editorialises on quality, reviewers start
 * trusting it instead of reading the diff.
 */
export interface EngineerRecap {
  /**
   * §7.5: when a session was auto-dispatched nobody vetted the spec, and the recap **leads** with
   * that. The client renders it first and renders `specMd` in full rather than collapsed.
   */
  autoDispatched: boolean;
  /** One paragraph, what and why, tied back to the approved spec. Markdown. */
  summaryMd: string;
  /** The spec in full. Present when `autoDispatched`, because a summary is not reviewable. */
  specMd?: string;
  deviations: RecapDeviation[];
  /** **Risk-ranked, not alphabetical** (§14). Server-ordered; the client never re-sorts. */
  files: ChangedFile[];
  /** Tests not written, edge cases noticed and skipped. */
  couldNotVerify: RecapNote[];
  /** Paths in suggested review order, starting where the risk is. */
  reviewOrder: string[];
  /**
   * §14: post it as a change request comment where the provider has one, and in the session view
   * where it does not. Absent means there was nowhere to post it, and this view is the only copy.
   */
  postedToUrl?: string;
  /** "pull request", "merge request" — supplied by the server, never assumed (see EngineerDetails). */
  postedToTerm?: string;
  generatedAt: Iso8601;
}

/* -------------------------------------------------------------------------- */
/* Post-hoc session actions (§7.5)                                            */
/* -------------------------------------------------------------------------- */

/**
 * §7.5's four post-hoc actions, all first-class.
 *
 * The booleans drive *affordances only* — whether a control is offered. They are not the
 * authorisation check; the server refuses the POST regardless of what the client drew. The whole
 * object is omitted for a viewer who may perform none of them, so the panel is absent rather than
 * present-and-empty.
 */
export interface SessionActions {
  canApprove: boolean;
  canSteer: boolean;
  canRevise: boolean;
  canTakeOver: boolean;
  /**
   * The branch take-over stops agent writes to. Named in the confirmation, because "stops writes
   * to that branch" is only meaningful if the reader can see which branch.
   */
  branch: string;
  /**
   * Set once someone has taken over. Charter has marked the session `handed_off` and stops touching
   * it — no further agent writes to that branch. Steer and Revise are gone for good at that point.
   */
  handedOff?: { at: Iso8601; byName: string };
}

/* -------------------------------------------------------------------------- */
/* Charter Agents — Settings → Runners (§33.3, §32.2, §27.3)                   */
/* -------------------------------------------------------------------------- */

/** §33.2. `native` exists because macOS with Xcode cannot be containerised. */
export type AgentMode = 'docker' | 'native';

export type AgentStatus = 'online' | 'offline' | 'draining' | 'revoked';

/**
 * §32.2: a runner **probes and reports** rather than being told what it has. `probedBy` is the
 * command that found it, which is the difference between a claim and a measurement — and it is what
 * lets an engineer answer "why does this agent think it has Xcode 16.2".
 */
export interface AgentCapability {
  /** The matchable identifier a session's requirements are checked against: "xcode:16.2". */
  id: string;
  /** The family it groups under: "xcode", "dotnet", "usb_device", "os". */
  family: string;
  /** Human label: ".NET SDK", "USB device". */
  label: string;
  version?: string;
  probedBy?: string;
  probedAt: Iso8601;
}

export interface RunnerAgent {
  id: Id;
  name: string;
  mode: AgentMode;
  /** Agent build version, e.g. "0.4.1". */
  version: string;
  /**
   * §33.6: agent and control plane negotiate a protocol version on connect. False means it has
   * refused to claim work — a clear message now beats subtle failures three sessions later.
   */
  protocolCompatible: boolean;
  protocolNote?: string;
  status: AgentStatus;
  /** §33.4: missed heartbeats mark it offline and its in-flight jobs are re-queued. */
  lastHeartbeatAt?: Iso8601;
  registeredAt: Iso8601;
  capabilities: AgentCapability[];
  /** §33.4: concurrency limit per agent, defaulting conservatively. */
  concurrency: { limit: number; inFlight: number };
  os: string;
  arch: string;
}

/**
 * §33.3 step 1. Single-use, short-TTL. Shown exactly once — there is no endpoint that reads it
 * back, so the UI must not offer to "show it again".
 */
export interface PairingToken {
  token: string;
  /**
   * The exact command to run, assembled server-side so `--server` carries the instance's real base
   * URL rather than whatever the browser happens to be pointed at.
   */
  command: string;
  expiresAt: Iso8601;
}

/**
 * §27.3. A session with no eligible runner **queues with a clear explanation** rather than failing,
 * and this is that explanation's data.
 */
export interface QueuedSessionDemand {
  requestId: Id;
  title: string;
  /** What the session requires: ["macos", "xcode:16"]. */
  requires: string[];
  /** Server-computed. Empty means nothing on this instance can run it. */
  eligibleAgentIds: Id[];
  /** Plain language, already written server-side. Rendered verbatim. */
  queuedReason?: string;
}

export interface RunnersView {
  agents: RunnerAgent[];
  /** Sessions waiting on a runner right now, with what each one needs. */
  waiting: QueuedSessionDemand[];
}

/* -------------------------------------------------------------------------- */
/* Admin setup checklist (§30.2)                                              */
/* -------------------------------------------------------------------------- */

export type SetupTaskId =
  | 'name_organisation'
  | 'connect_github'
  | 'add_model_credential'
  | 'connect_repository'
  | 'set_budgets'
  | 'invite_people'
  | 'notification_channels';

export interface SetupTask {
  id: SetupTaskId;
  title: string;
  /** One line saying why this one matters. Not instructions — the destination has those. */
  description: string;
  done: boolean;
  /** Where the task actually gets done. An in-app route, or an external URL for a GitHub App install. */
  href: string;
  external?: boolean;
  /**
   * Not a lock — an explanation. "Connect GitHub first" is information; the checklist never
   * disables a row, because §30.2's whole point is that someone can leave and come back in any
   * order they like.
   */
  blockedBy?: SetupTaskId;
  /** What is configured, once done: "3 repositories", "Anthropic". Proof, not a tick. */
  doneSummary?: string;
}

/**
 * §30.2. A **persistent dashboard checklist, not a modal wizard** — "modal wizards trap people who
 * need to go find a token". Resumable, showing progress, dismissible once complete.
 *
 * Omitted by the API for anyone who is not an admin, so a requester's dashboard has no checklist
 * rather than an empty one.
 */
export interface SetupChecklist {
  tasks: SetupTask[];
  /** Set once dismissed. Dismissal is a server-side preference like every other (no browser storage). */
  dismissedAt?: Iso8601;
}

/* -------------------------------------------------------------------------- */
/* Approvals (§7.5) — the spend gate                                          */
/* -------------------------------------------------------------------------- */

export interface PendingApproval {
  requestId: Id;
  specId: Id;
  title: string;
  outcome: string;
  requesterName: string;
  projectName: string;
  estimatedCostUsd: number;
  submittedAt: Iso8601;
}

/* -------------------------------------------------------------------------- */
/* Request bodies                                                             */
/* -------------------------------------------------------------------------- */

export interface CreateRequestBody {
  projectId: Id;
  rawText: string;
  templateId?: Id;
}

export interface SendRefinementMessageBody {
  body: string;
  /** Set when the requester picked a quick reply rather than typing. */
  choiceId?: Id;
}

export interface SubmitFeedbackBody {
  verdict: FeedbackVerdict;
  note?: string;
}

/** §7.5 "Steer" — continue the existing session with a new instruction; same branch, same thread. */
export interface SteerSessionBody {
  instruction: string;
}

/** §7.5 "Revise and rebuild" — fork the spec, edit it, dispatch a fresh session onto the branch. */
export interface ReviseSessionBody {
  /** The edited spec. Sent in full, because forking a spec means replacing it, not patching it. */
  revisedSpecMd: string;
}

/* -------------------------------------------------------------------------- */
/* Live updates                                                               */
/* -------------------------------------------------------------------------- */

/**
 * Pushed over the SignalR hub at `/hub/requests` (§2.1). The client subscribes per request and
 * applies these to the detail it already holds — §11 requires that *something* streams, because a
 * 5–20 minute silent gap reads as broken.
 *
 * Every event is idempotent by id, because the container can restart mid-session and the client
 * resubscribes by refetching (§2.3: no in-memory orchestration state).
 */
export type RequestStreamEvent =
  | { type: 'milestone'; milestone: Milestone }
  | { type: 'milestone_updated'; milestone: Milestone }
  | { type: 'status'; status: RequestStatus; awaitingApprovalFrom?: string }
  | { type: 'refinement_message'; message: RefinementMessage }
  | { type: 'charter_thinking'; thinking: boolean }
  | { type: 'spec_proposed'; spec: RequesterSpec }
  | { type: 'artifact'; artifact: VerificationArtifact }
  | { type: 'artifact_state'; artifactId: Id; state: ArtifactState; expiresAt?: Iso8601 }
  | { type: 'failed'; failureSummary: string }
  | { type: 'ended'; endedAt: Iso8601 };
