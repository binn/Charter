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
  pullRequestNumber: number;
  pullRequestUrl: string;
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

export interface TranscriptEvent {
  seq: number;
  type: string;
  summary: string;
  createdAt: Iso8601;
  /** Set on file-write events; clicking one opens pane 3 at this path. */
  path?: string;
}

export interface TranscriptPane {
  events: TranscriptEvent[];
  /** Cursor pagination; `null` when the beginning has been reached. */
  nextCursor: string | null;
}

export interface ChangedFile {
  path: string;
  additions: number;
  deletions: number;
  /** §14: auth, migrations, money math and external calls float to the top. */
  risk: 'high' | 'medium' | 'low';
}

export interface ChangesPane {
  files: ChangedFile[];
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
