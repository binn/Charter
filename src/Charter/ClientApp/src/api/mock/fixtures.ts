import {
  derateRecap,
  derateSessionActions,
  HERO_CHANGED_FILES,
  HERO_MILESTONE_SEQS,
  heroRecap,
  heroSessionActions,
  transcriptFor,
} from '@/api/mock/fixtures-session';
import type {
  InstanceInfo,
  PendingApproval,
  Project,
  RequestDetail,
  TranscriptPane,
  Viewer,
  ViewerCapabilities,
} from '@/api/types';

/**
 * Fixture data for the mock API.
 *
 * The domain is the one the README uses — a small solar company's internal quoting tool — because
 * plausible copy is the only way to tell whether a status thread reads well. Everything is built
 * relative to `now` so countdowns, elapsed timers and the `expiring` threshold are live rather than
 * frozen at some point in the past.
 *
 * Nothing here is imported by a component. Components take `CharterApi` from context.
 */

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const iso = (offsetMs: number, now: number): string => new Date(now + offsetMs).toISOString();

/** Requesters can file, and nothing else. `canReadRepos: false` is why panes 2/3 and every
 *  `details` block are absent from the payloads below rather than present-and-hidden. */
const REQUESTER_CAPABILITIES: ViewerCapabilities = {
  canFileRequests: true,
  canApproveSpend: false,
  canReadRepos: false,
  canAdminister: false,
};

const ENGINEER_CAPABILITIES: ViewerCapabilities = {
  canFileRequests: true,
  canApproveSpend: true,
  canReadRepos: true,
  canAdminister: true,
};

export type MockPersona = 'requester' | 'engineer' | 'new-requester';

export function makeViewer(persona: MockPersona, now: number): Viewer {
  if (persona === 'engineer') {
    return {
      id: 'user-eng',
      displayName: 'Tomas Beck',
      email: 'tomas@example.com',
      organization: { id: 'org-1', name: 'Northbeam Solar' },
      roles: ['engineer', 'admin', 'requester'],
      capabilities: ENGINEER_CAPABILITIES,
      preferences: { theme: 'system', pane: 'developer', teachingLevel: 'just_the_decisions' },
      requesterOnboardingCompletedAt: iso(-90 * DAY, now),
    };
  }

  // §30.4 / §30.5: `new-requester` is the day-one instance — no completed onboarding, and
  // `makeRequests` returns nothing, so every list view renders its designed empty state.
  return {
    id: 'user-req',
    displayName: 'Ayesha Rahman',
    email: 'ayesha@example.com',
    organization: { id: 'org-1', name: 'Northbeam Solar' },
    roles: ['requester'],
    capabilities: REQUESTER_CAPABILITIES,
    preferences: { theme: 'system', pane: 'simple', teachingLevel: 'skip_the_basics' },
    ...(persona === 'new-requester' ? {} : { requesterOnboardingCompletedAt: iso(-12 * DAY, now) }),
  };
}

export function makeInstance(now: number): InstanceInfo {
  return {
    version: '0.1.0-dev',
    commit: 'a3f9c21',
    buildDate: iso(-2 * DAY, now),
    sourceUrl: 'https://github.com/binn/charter/tree/a3f9c21',
    license: 'AGPL-3.0-only',
    serviceName: 'Charter',
  };
}

export function makeProjects(persona: MockPersona): Project[] {
  if (persona === 'new-requester') {
    return [];
  }

  return [
    {
      id: 'proj-quotes',
      name: 'Quote tool',
      description: 'The internal quoting wizard the sales team uses to price installations.',
      primerMd: [
        'The quote tool walks a salesperson through four steps: pick a **vertical** (solar, battery,',
        'EV charging), enter the site details, choose equipment, and produce a **BOQ** — the itemised',
        'list of everything the install needs.',
        '',
        'Quotes are saved as soon as step one is complete, so a half-finished quote is a normal thing',
        'to find in the list. Prices come from the supplier price file, which is refreshed nightly.',
      ].join('\n'),
      templates: [
        {
          id: 'tpl-copy',
          name: 'Change some wording',
          description: 'A label, button, heading or message says the wrong thing.',
          icon: 'text',
          prompt: '',
          fields: [
            {
              key: 'where',
              label: 'Where do you see it?',
              placeholder: 'e.g. the last step of the quote wizard',
              required: true,
              multiline: false,
            },
            {
              key: 'current',
              label: 'What does it say now?',
              placeholder: 'Copy it exactly if you can',
              required: true,
              multiline: false,
            },
            {
              key: 'wanted',
              label: 'What should it say instead?',
              placeholder: '',
              required: true,
              multiline: false,
            },
          ],
        },
        {
          id: 'tpl-bug',
          name: 'Something is broken',
          description: 'It used to work, or it does the wrong thing.',
          icon: 'bug',
          prompt: '',
          fields: [
            {
              key: 'doing',
              label: 'What were you doing?',
              placeholder: 'Walk through it step by step',
              required: true,
              multiline: true,
            },
            {
              key: 'expected',
              label: 'What did you expect to happen?',
              placeholder: '',
              required: true,
              multiline: false,
            },
            {
              key: 'actual',
              label: 'What happened instead?',
              placeholder: '',
              required: true,
              multiline: false,
            },
          ],
        },
        {
          id: 'tpl-field',
          name: 'Add or change a field',
          description: 'A form is missing something, or a field should behave differently.',
          icon: 'field',
          prompt:
            'On the … screen I need a field for … because …\n\nIt should default to … and it is (required / optional).',
        },
        {
          id: 'tpl-export',
          name: 'Get data out',
          description: 'You need a report, an export, or a different column order.',
          icon: 'export',
          prompt:
            'I need … as a … (spreadsheet / PDF / on-screen list).\n\nIt should include … and be sorted by …\n\nI use this for …',
        },
      ],
    },
    {
      id: 'proj-survey',
      name: 'Site survey app',
      description: 'The tablet app surveyors take on roofs. iOS and Android.',
      templates: [
        {
          id: 'tpl-survey-bug',
          name: 'Something is broken',
          description: 'It used to work, or it does the wrong thing.',
          icon: 'bug',
          prompt: '',
        },
        {
          id: 'tpl-survey-field',
          name: 'Add or change a field',
          description: 'A survey form is missing something.',
          icon: 'field',
          prompt: '',
        },
      ],
    },
    {
      id: 'proj-logger',
      name: 'Roof logger firmware',
      description: 'The battery-powered irradiance logger. Verified by an engineer, not by clicking.',
      templates: [],
    },
  ];
}

export function makePendingApprovals(persona: MockPersona, now: number): PendingApproval[] {
  if (persona !== 'engineer') {
    return [];
  }

  return [
    {
      requestId: 'req-derate',
      specId: 'spec-derate-2',
      title: 'Derate factor should follow the panel, not the project',
      outcome:
        'When you change a panel on a quote, the derate factor changes with it instead of keeping the number someone typed at the start.',
      requesterName: 'Ayesha Rahman',
      projectName: 'Quote tool',
      estimatedCostUsd: 3.4,
      submittedAt: iso(-40 * MINUTE, now),
    },
  ];
}

/* -------------------------------------------------------------------------- */
/* Requests                                                                   */
/* -------------------------------------------------------------------------- */

/**
 * Eight requests covering all five artifact states and all eight artifact kinds, so every branch of
 * the §27.7 card is reachable by clicking rather than only by unit test.
 */
/** Pane 2 loads a page at a time (§12); this is the tail, which is what the detail payload carries. */
export const TRANSCRIPT_PAGE_SIZE = 500;

function tailPage(requestId: string, now: number): TranscriptPane {
  const all = transcriptFor(requestId, now);
  const from = Math.max(0, all.length - TRANSCRIPT_PAGE_SIZE);
  return {
    events: all.slice(from),
    nextCursor: from > 0 ? String(from) : null,
    totalCount: all.length,
  };
}

export function makeRequests(persona: MockPersona, now: number): RequestDetail[] {
  if (persona === 'new-requester') {
    return [];
  }

  const engineer = persona === 'engineer';

  const requests: RequestDetail[] = [
    /* --- 1. The hero: ready to try, hosted preview, five hours left ------- */
    {
      id: 'req-vertical',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: 'Remember the last selected vertical',
      status: 'preview_ready',
      createdAt: iso(-3 * HOUR, now),
      updatedAt: iso(-11 * MINUTE, now),
      lastMilestoneLabel: 'Ready to try',
      needsAttention: true,
      rawText:
        'every time i start a new quote it makes me pick solar again even though thats basically all we do. can it just remember what i picked last time',
      cancellable: false,
      spec: {
        id: 'spec-vertical-2',
        version: 2,
        title: 'Remember the last selected vertical',
        outcome:
          'When you start a new quote, the vertical you chose last time is already selected. You can still change it, and the very first quote you ever create still starts on Solar.',
        acceptanceCriteria: [
          { id: 'ac-1', text: 'Starting a new quote pre-selects the vertical you chose on your previous quote.' },
          { id: 'ac-2', text: 'Changing the vertical on a quote still works exactly as it does now.' },
          { id: 'ac-3', text: 'A person who has never created a quote still starts on Solar.' },
          { id: 'ac-4', text: 'The remembered choice is yours alone — it does not change what your colleagues see.' },
        ],
        glossary: [
          {
            term: 'vertical',
            definition:
              'The kind of installation a quote is for — solar, battery storage, or EV charging.',
          },
        ],
        approvedAt: iso(-2 * HOUR - 40 * MINUTE, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 'm1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-3 * HOUR, now),
            body: 'every time i start a new quote it makes me pick solar again even though thats basically all we do. can it just remember what i picked last time',
          },
          {
            id: 'm2',
            author: 'charter',
            kind: 'question',
            createdAt: iso(-3 * HOUR + 20_000, now),
            body: 'That makes sense. Two things I want to get right before anyone builds it.\n\nShould the quote remember *your* last choice, or the last choice anyone at Northbeam made?',
            choices: [
              { id: 'c1', label: 'Just mine' },
              { id: 'c2', label: 'Everyone shares one' },
            ],
          },
          {
            id: 'm3',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-3 * HOUR + 4 * MINUTE, now),
            body: 'just mine. dev does battery stuff sometimes and it would drive him mad',
          },
          {
            id: 'm4',
            author: 'charter',
            kind: 'question',
            createdAt: iso(-3 * HOUR + 4 * MINUTE + 15_000, now),
            body: 'Good. And on the very first quote a new starter creates, there is nothing to remember yet. What should they see?',
            choices: [
              { id: 'c3', label: 'Solar, like today' },
              { id: 'c4', label: 'Nothing selected' },
            ],
          },
          {
            id: 'm5',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-3 * HOUR + 7 * MINUTE, now),
            body: 'solar like today',
          },
          {
            id: 'm6',
            author: 'charter',
            kind: 'spec_proposed',
            createdAt: iso(-3 * HOUR + 7 * MINUTE + 12_000, now),
            body: "That is enough to build from. Here is what I have written down — read the checks at the bottom, because those are what you will be asked to confirm at the end.",
            specVersion: 2,
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-2 * HOUR - 38 * MINUTE, now),
        endedAt: iso(-11 * MINUTE, now),
        milestones: [
          {
            id: 'ms1',
            kind: 'status',
            label: 'You approved the plan',
            occurredAt: iso(-2 * HOUR - 40 * MINUTE, now),
            state: 'done',
          },
          {
            id: 'ms2',
            kind: 'understanding',
            label: 'Understanding the current setup',
            detail: 'Read how the quote wizard stores what you pick on each step.',
            annotationMd:
              'Your quote wizard keeps the selected vertical on the quote row itself, so there was nowhere to record a *preference* — that is the one new piece.',
            occurredAt: iso(-2 * HOUR - 36 * MINUTE, now),
            state: 'done',
            ...(engineer ? { eventSeq: HERO_MILESTONE_SEQS.understanding } : {}),
          },
          {
            id: 'ms3',
            kind: 'changing',
            label: 'Making the changes',
            detail: 'Added a per-person setting and used it when a new quote opens.',
            occurredAt: iso(-2 * HOUR - 19 * MINUTE, now),
            state: 'done',
            ...(engineer ? { eventSeq: HERO_MILESTONE_SEQS.changing } : {}),
          },
          {
            id: 'ms4',
            kind: 'checking',
            label: 'Checking it works',
            detail: 'Ran the existing tests plus three new ones for the cases you agreed.',
            occurredAt: iso(-38 * MINUTE, now),
            state: 'done',
            ...(engineer ? { eventSeq: HERO_MILESTONE_SEQS.checking } : {}),
          },
          {
            id: 'ms5',
            kind: 'assembling',
            label: 'Putting it together',
            detail: 'Built a copy of the quote tool with your change in it.',
            occurredAt: iso(-14 * MINUTE, now),
            state: 'done',
            ...(engineer ? { eventSeq: HERO_MILESTONE_SEQS.assembling } : {}),
          },
          {
            id: 'ms6',
            kind: 'outcome',
            label: 'Ready to try',
            occurredAt: iso(-11 * MINUTE, now),
            state: 'done',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-vertical',
          kind: 'hosted_preview',
          state: 'ready',
          audience: 'requester',
          label: 'Preview',
          primary: true,
          expiresAt: iso(5 * HOUR + 12 * MINUTE, now),
          instructionsMd:
            'This is a full copy of the quote tool with your change in it, loaded with test data. Nothing you do here touches real quotes.',
          payload: {
            url: 'https://pr-142.preview.northbeam.charter.app/quotes/new',
            displayUrl: 'pr-142.preview.northbeam…/quotes/new',
            reachability: 'reachable',
          },
          ...(engineer
            ? {
                details: {
                  changeRequestNumber: 142,
                  changeRequestUrl: 'https://github.com/northbeam/quote-tool/pull/142',
                  changeRequestTerm: 'pull request',
                  changeRequestTermShort: 'PR',
                  // Deliberately not the instance's own build commit above: two different SHAs on
                  // one page that happen to match is a fixture that hides a bug.
                  commitSha: '4d7b19e',
                  branch: 'charter/remember-vertical',
                  runner: 'detached · mac-mini-01',
                  durationMs: 12 * MINUTE,
                  costUsd: 1.42,
                  recapUrl: 'https://github.com/northbeam/quote-tool/pull/142#issuecomment-1',
                },
              }
            : {}),
        },
      ],

      /*
       * Panes 2 and 3, the recap and the post-hoc actions (§12, §14, §7.5).
       *
       * These four keys are the whole of §7.4 in the mock: the requester persona never reaches this
       * spread, so its payload has no `transcript` key at all — not a `transcript: undefined`, and
       * not a populated one the client is trusted to hide. That is what the real API must do too.
       */
      ...(engineer
        ? {
            transcript: tailPage('req-vertical', now),
            changes: { files: HERO_CHANGED_FILES },
            recap: heroRecap(now),
            sessionActions: heroSessionActions(),
          }
        : {}),
    },

    /* --- 2. Live: building now, artifact pending -------------------------- */
    {
      id: 'req-pdf',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: "Show the installer's name on the quote PDF",
      status: 'running',
      createdAt: iso(-26 * MINUTE, now),
      updatedAt: iso(-40_000, now),
      lastMilestoneLabel: 'Making the changes',
      needsAttention: false,
      rawText:
        "the pdf we send customers doesnt say who's installing it. people ring up asking. can we put the installer's name and phone on it",
      cancellable: true,
      spec: {
        id: 'spec-pdf-1',
        version: 1,
        title: "Show the installer's name on the quote PDF",
        outcome:
          "The quote PDF gains a short block near the bottom with the assigned installer's name and phone number. If no installer is assigned yet, the block is left out entirely rather than printed blank.",
        acceptanceCriteria: [
          { id: 'ac-p1', text: "A quote with an assigned installer shows that installer's name and phone on the PDF." },
          { id: 'ac-p2', text: 'A quote with no installer assigned produces a PDF with no empty installer block.' },
          { id: 'ac-p3', text: 'Existing quotes regenerate correctly — nothing already sent changes retrospectively.' },
        ],
        approvedAt: iso(-22 * MINUTE, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 'p1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-26 * MINUTE, now),
            body: "the pdf we send customers doesnt say who's installing it. people ring up asking. can we put the installer's name and phone on it",
          },
          {
            id: 'p2',
            author: 'charter',
            kind: 'spec_proposed',
            createdAt: iso(-24 * MINUTE, now),
            body: 'Straightforward enough. Here is what I will ask for.',
            specVersion: 1,
          },
        ],
      },
      thread: {
        live: true,
        startedAt: iso(-21 * MINUTE, now),
        milestones: [
          {
            id: 'pm1',
            kind: 'status',
            label: 'You approved the plan',
            occurredAt: iso(-22 * MINUTE, now),
            state: 'done',
          },
          {
            id: 'pm2',
            kind: 'understanding',
            label: 'Understanding the current setup',
            detail: 'Read how the quote PDF is laid out today.',
            occurredAt: iso(-21 * MINUTE, now),
            state: 'done',
          },
          {
            id: 'pm3',
            kind: 'changing',
            label: 'Making the changes',
            detail: 'Adding the installer block to the PDF template.',
            occurredAt: iso(-6 * MINUTE, now),
            state: 'active',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-pdf',
          kind: 'hosted_preview',
          state: 'pending',
          audience: 'requester',
          label: 'Preview',
          primary: true,
          payload: {
            url: '',
            displayUrl: '',
            reachability: 'unknown',
          },
        },
      ],
    },

    /* --- 3. Refining: the conversation, spec awaiting confirmation -------- */
    {
      id: 'req-copy',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: 'The Submit button should say what it does',
      status: 'refining',
      createdAt: iso(-9 * MINUTE, now),
      updatedAt: iso(-25_000, now),
      needsAttention: false,
      rawText:
        'the button at the end of a quote says Submit and nobody knows if that emails the customer or not',
      templateId: 'tpl-copy',
      cancellable: false,
      spec: {
        id: 'spec-copy-1',
        version: 1,
        title: 'The Submit button should say what it does',
        outcome:
          'The button at the end of the quote wizard reads "Send to installer" instead of "Submit", and the confirmation that appears afterwards says who it went to.',
        acceptanceCriteria: [
          { id: 'ac-c1', text: 'The final button in the quote wizard reads "Send to installer".' },
          { id: 'ac-c2', text: 'The message after pressing it names the installer it was sent to.' },
          { id: 'ac-c3', text: 'Nothing about who receives the quote changes — only the wording.' },
        ],
        openQuestions: [
          'If no installer is assigned, the button currently still says Submit. Leave that alone for now?',
        ],
      },
      refinement: {
        mode: 'plan',
        canReply: true,
        charterIsThinking: false,
        messages: [
          {
            id: 'c1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-9 * MINUTE, now),
            body: 'the button at the end of a quote says Submit and nobody knows if that emails the customer or not',
          },
          {
            id: 'c2',
            author: 'charter',
            kind: 'message',
            createdAt: iso(-9 * MINUTE + 18_000, now),
            body: 'Worth knowing before we change the label: pressing it does not email the customer. It sends the quote to the assigned installer, and the customer only gets it once the installer accepts.\n\nSo the honest label is about the installer, not the customer.',
          },
          {
            id: 'c3',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-6 * MINUTE, now),
            body: 'oh. then "send to installer" is what it should say',
          },
          {
            id: 'c4',
            author: 'charter',
            kind: 'spec_proposed',
            createdAt: iso(-5 * MINUTE, now),
            body: 'Agreed. One thing I could not settle on my own is in the open question below — answer it or leave it, either is fine.',
            specVersion: 1,
          },
        ],
      },
      thread: {
        live: false,
        milestones: [],
      },
      artifacts: [],
    },

    /* --- 4. Multiple artifacts: tabs, QR, capture, test report ------------ */
    {
      id: 'req-survey',
      projectId: 'proj-survey',
      projectName: 'Site survey app',
      title: 'Hold photos until the tablet is back on wifi',
      status: 'preview_ready',
      createdAt: iso(-1 * DAY, now),
      updatedAt: iso(-3 * HOUR, now),
      lastMilestoneLabel: 'Ready to try',
      needsAttention: true,
      rawText:
        'surveyors keep burning their data allowance uploading roof photos from site. can it wait until theyre back on wifi',
      cancellable: false,
      spec: {
        id: 'spec-survey-1',
        version: 1,
        title: 'Hold photos until the tablet is back on wifi',
        outcome:
          'Photos taken on site are kept on the tablet and upload automatically the next time it joins a wifi network. A surveyor who needs one to go now can still send it immediately.',
        acceptanceCriteria: [
          { id: 'ac-s1', text: 'Photos taken with no wifi stay on the tablet instead of uploading over cellular.' },
          { id: 'ac-s2', text: 'They upload on their own once the tablet joins wifi, without anyone opening the app.' },
          { id: 'ac-s3', text: 'A survey shows how many photos are still waiting to upload.' },
          { id: 'ac-s4', text: '"Send now" is available on a waiting photo and uploads it over cellular.' },
        ],
        approvedAt: iso(-23 * HOUR, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 's1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-1 * DAY, now),
            body: 'surveyors keep burning their data allowance uploading roof photos from site. can it wait until theyre back on wifi',
          },
          {
            id: 's2',
            author: 'charter',
            kind: 'spec_proposed',
            createdAt: iso(-1 * DAY + 6 * MINUTE, now),
            body: 'Here is what I have written down.',
            specVersion: 1,
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-23 * HOUR, now),
        endedAt: iso(-3 * HOUR, now),
        milestones: [
          {
            id: 'sm1',
            kind: 'understanding',
            label: 'Understanding the current setup',
            occurredAt: iso(-23 * HOUR, now),
            state: 'done',
          },
          {
            id: 'sm2',
            kind: 'changing',
            label: 'Making the changes',
            occurredAt: iso(-22 * HOUR, now),
            state: 'done',
          },
          {
            id: 'sm3',
            kind: 'checking',
            label: 'Checking it works',
            occurredAt: iso(-4 * HOUR, now),
            state: 'done',
          },
          {
            id: 'sm4',
            kind: 'assembling',
            label: 'Putting it together',
            detail: 'Built the app and sent it to TestFlight.',
            occurredAt: iso(-3 * HOUR, now),
            state: 'done',
          },
          {
            id: 'sm5',
            kind: 'outcome',
            label: 'Ready to try',
            occurredAt: iso(-3 * HOUR, now),
            state: 'done',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-survey-tf',
          kind: 'distribution_channel',
          state: 'ready',
          audience: 'requester',
          label: 'TestFlight',
          primary: true,
          expiresAt: iso(6 * DAY, now),
          instructionsMd:
            'Scan the code with the tablet you use on site. If TestFlight asks for an invite, tell us and we will send one.',
          payload: {
            provider: 'testflight',
            channelName: 'TestFlight',
            buildNumber: '42',
            openUrl: 'https://testflight.apple.com/join/nb-survey-42',
            inviteRequired: false,
          },
        },
        {
          id: 'art-survey-capture',
          kind: 'capture',
          state: 'ready',
          audience: 'requester',
          label: 'Screenshots',
          primary: false,
          instructionsMd:
            'Taken automatically on a simulated tablet, so you can see the change without installing anything.',
          payload: {
            items: [
              {
                id: 'cap-1',
                mediaType: 'image',
                url: 'data:image/svg+xml;utf8,' +
                  encodeURIComponent(
                    '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="500"><rect width="800" height="500" fill="#e7ecec"/><rect x="40" y="40" width="720" height="70" rx="10" fill="#ffffff"/><text x="70" y="85" font-family="system-ui" font-size="26" fill="#14171B">Survey · 14 Mill Lane</text><rect x="40" y="140" width="720" height="60" rx="10" fill="#d8e5e3"/><text x="70" y="178" font-family="system-ui" font-size="22" fill="#1E6B5E">3 photos waiting for wifi</text><rect x="40" y="230" width="340" height="220" rx="10" fill="#ffffff"/><rect x="420" y="230" width="340" height="220" rx="10" fill="#ffffff"/></svg>',
                  ),
                caption: 'The survey screen now shows how many photos are still waiting.',
                baselineUrl: 'data:image/svg+xml;utf8,' +
                  encodeURIComponent(
                    '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="500"><rect width="800" height="500" fill="#e7ecec"/><rect x="40" y="40" width="720" height="70" rx="10" fill="#ffffff"/><text x="70" y="85" font-family="system-ui" font-size="26" fill="#14171B">Survey · 14 Mill Lane</text><rect x="40" y="140" width="340" height="220" rx="10" fill="#ffffff"/><rect x="420" y="140" width="340" height="220" rx="10" fill="#ffffff"/></svg>',
                  ),
                width: 800,
                height: 500,
              },
              {
                id: 'cap-2',
                mediaType: 'image',
                url: 'data:image/svg+xml;utf8,' +
                  encodeURIComponent(
                    '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="500"><rect width="800" height="500" fill="#e7ecec"/><rect x="40" y="40" width="720" height="410" rx="10" fill="#ffffff"/><text x="70" y="110" font-family="system-ui" font-size="26" fill="#14171B">Photo · waiting for wifi</text><rect x="70" y="150" width="200" height="52" rx="8" fill="#1E6B5E"/><text x="98" y="184" font-family="system-ui" font-size="20" fill="#ffffff">Send now</text></svg>',
                  ),
                caption: '"Send now" appears on a photo that is still waiting.',
                width: 800,
                height: 500,
              },
            ],
          },
        },
        {
          id: 'art-survey-tests',
          kind: 'test_report',
          state: 'ready',
          audience: 'requester',
          label: 'Checks',
          primary: false,
          payload: {
            passed: 214,
            failed: 0,
            skipped: 3,
            durationMs: 96_000,
            failures: [],
          },
        },
      ],
    },

    /* --- 5. Expired: the number one source of confusion ------------------- */
    {
      id: 'req-export',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: 'Export the pipeline to a spreadsheet',
      status: 'merged',
      createdAt: iso(-6 * DAY, now),
      updatedAt: iso(-2 * DAY, now),
      lastMilestoneLabel: 'This is live',
      needsAttention: false,
      rawText:
        'i need the open quotes in a spreadsheet every monday for the pipeline meeting. right now i copy them by hand',
      cancellable: false,
      spec: {
        id: 'spec-export-1',
        version: 1,
        title: 'Export the pipeline to a spreadsheet',
        outcome:
          'The quote list gains an Export button that downloads everything currently shown as a spreadsheet, respecting whatever filter you have applied.',
        acceptanceCriteria: [
          { id: 'ac-e1', text: 'Export downloads a spreadsheet of the quotes currently listed.' },
          { id: 'ac-e2', text: 'Filtering the list first changes what ends up in the file.' },
          { id: 'ac-e3', text: 'The file opens in Excel and Google Sheets without a warning.' },
        ],
        approvedAt: iso(-6 * DAY, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 'e1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-6 * DAY, now),
            body: 'i need the open quotes in a spreadsheet every monday for the pipeline meeting. right now i copy them by hand',
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-6 * DAY, now),
        endedAt: iso(-6 * DAY + 18 * MINUTE, now),
        feedback: { verdict: 'works', submittedAt: iso(-5 * DAY, now) },
        milestones: [
          {
            id: 'em1',
            kind: 'changing',
            label: 'Making the changes',
            occurredAt: iso(-6 * DAY, now),
            state: 'done',
          },
          {
            id: 'em2',
            kind: 'outcome',
            label: 'Ready to try',
            occurredAt: iso(-6 * DAY + 18 * MINUTE, now),
            state: 'done',
          },
          {
            id: 'em3',
            kind: 'outcome',
            label: 'This is live',
            detail: 'An engineer reviewed it and it went out with the Tuesday release.',
            occurredAt: iso(-2 * DAY, now),
            state: 'done',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-export',
          kind: 'hosted_preview',
          state: 'expired',
          audience: 'requester',
          label: 'Preview',
          primary: true,
          expiresAt: iso(-4 * DAY, now),
          payload: {
            url: 'https://pr-138.preview.northbeam.charter.app/quotes',
            displayUrl: 'pr-138.preview.northbeam…/quotes',
            reachability: 'unreachable',
          },
        },
      ],
    },

    /* --- 6. Expiring: under an hour, amber countdown ---------------------- */
    {
      id: 'req-tariff',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: 'Try the new tariff calculator against last month',
      status: 'preview_ready',
      createdAt: iso(-2 * DAY, now),
      updatedAt: iso(-1 * DAY, now),
      lastMilestoneLabel: 'Ready to try',
      needsAttention: true,
      rawText:
        'can we check the new export tariff numbers against the quotes we did in march before we switch it on',
      cancellable: false,
      spec: {
        id: 'spec-tariff-1',
        version: 1,
        title: 'Try the new tariff calculator against last month',
        outcome:
          'A copy of the quote tool running the new export tariff rates, loaded with March’s quotes, so you can compare the payback figures side by side before anyone switches it on for real.',
        acceptanceCriteria: [
          { id: 'ac-t1', text: 'Every March quote can be opened and shows a payback figure.' },
          { id: 'ac-t2', text: 'The figures use the new export rates, not the old ones.' },
          { id: 'ac-t3', text: 'Nothing in the real quote tool changes while this is running.' },
        ],
        approvedAt: iso(-2 * DAY, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 't1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-2 * DAY, now),
            body: 'can we check the new export tariff numbers against the quotes we did in march before we switch it on',
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-2 * DAY, now),
        endedAt: iso(-1 * DAY, now),
        milestones: [
          {
            id: 'tm1',
            kind: 'assembling',
            label: 'Putting it together',
            occurredAt: iso(-1 * DAY, now),
            state: 'done',
          },
          {
            id: 'tm2',
            kind: 'outcome',
            label: 'Ready to try',
            occurredAt: iso(-1 * DAY, now),
            state: 'done',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-tariff-instance',
          kind: 'ephemeral_instance',
          state: 'expiring',
          audience: 'requester',
          label: 'Sandbox',
          primary: true,
          expiresAt: iso(38 * MINUTE, now),
          instructionsMd:
            'This sandbox shuts down on its own. If you need longer with it, use Rebuild once it goes.',
          payload: {
            protocol: 'https',
            connectString: 'https://tariff-sandbox.northbeam.charter.app',
            region: 'eu-west-1',
            note: 'Loaded with a copy of March, refreshed when the sandbox started.',
          },
        },
        {
          id: 'art-tariff-build',
          kind: 'build_artifact',
          state: 'expiring',
          audience: 'requester',
          label: 'Spreadsheet',
          primary: false,
          expiresAt: iso(38 * MINUTE, now),
          payload: {
            platform: 'other',
            filename: 'march-payback-comparison.xlsx',
            sizeBytes: 148_402,
            checksumAlgorithm: 'sha256',
            checksumShort: '9f2c11a4',
            downloadUrl: 'https://artifacts.northbeam.charter.app/march-payback-comparison.xlsx',
            installInstructionsMd:
              'Old rates and new rates side by side, one row per March quote. Open it in Excel.',
          },
        },
      ],
    },

    /* --- 7. Failure with dignity ------------------------------------------ */
    {
      id: 'req-derate',
      projectId: 'proj-quotes',
      projectName: 'Quote tool',
      title: 'Derate factor should follow the panel, not the project',
      status: 'failed',
      createdAt: iso(-4 * DAY, now),
      updatedAt: iso(-4 * DAY + 51 * MINUTE, now),
      lastMilestoneLabel: 'This turned out to be bigger than expected',
      needsAttention: false,
      rawText:
        'the derate is set once at the start and then it is wrong for the rest of the quote if you swap panels',
      cancellable: false,
      spec: {
        id: 'spec-derate-2',
        version: 2,
        title: 'Derate factor should follow the panel, not the project',
        outcome:
          'When you change a panel on a quote, the derate factor changes with it instead of keeping the number someone typed at the start.',
        acceptanceCriteria: [
          { id: 'ac-d1', text: 'Swapping the panel on a quote updates the derate factor to match the new panel.' },
          { id: 'ac-d2', text: 'A derate someone has typed in by hand is kept, and marked as manual.' },
          { id: 'ac-d3', text: 'Quotes already sent are not recalculated.' },
        ],
        glossary: [
          {
            term: 'derate',
            definition:
              'Reducing a rated output to account for real-world losses like heat or shading.',
          },
        ],
        approvedAt: iso(-4 * DAY, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 'd1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-4 * DAY, now),
            body: 'the derate is set once at the start and then it is wrong for the rest of the quote if you swap panels',
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-4 * DAY, now),
        endedAt: iso(-4 * DAY + 51 * MINUTE, now),
        failureSummary:
          'This turned out to be bigger than expected. The derate figure is used in more places than it looked from the outside, and changing it safely needs a decision only an engineer can make. Tomas has been told and will pick it up — you do not need to do anything.',
        milestones: [
          {
            id: 'dm1',
            kind: 'understanding',
            label: 'Understanding the current setup',
            occurredAt: iso(-4 * DAY, now),
            state: 'done',
          },
          {
            id: 'dm2',
            kind: 'changing',
            label: 'Making the changes',
            occurredAt: iso(-4 * DAY + 20 * MINUTE, now),
            state: 'done',
          },
          {
            id: 'dm3',
            kind: 'checking',
            label: 'Checking it works',
            occurredAt: iso(-4 * DAY + 44 * MINUTE, now),
            state: 'failed',
          },
          {
            id: 'dm4',
            kind: 'outcome',
            label: 'This turned out to be bigger than expected',
            occurredAt: iso(-4 * DAY + 51 * MINUTE, now),
            state: 'failed',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-derate',
          kind: 'hosted_preview',
          state: 'failed',
          audience: 'requester',
          label: 'Preview',
          primary: true,
          failureSummary:
            'There is nothing to try this time — the change did not get far enough to build a preview.',
          payload: { url: '', displayUrl: '', reachability: 'unknown' },
          ...(engineer
            ? {
                details: {
                  changeRequestNumber: 139,
                  changeRequestUrl: 'https://github.com/northbeam/quote-tool/pull/139',
                  changeRequestTerm: 'pull request',
                  changeRequestTermShort: 'PR',
                  commitSha: '7b1e004',
                  branch: 'charter/derate-follows-panel',
                  runner: 'github-actions · ubuntu-latest',
                  durationMs: 51 * MINUTE,
                  costUsd: 4.86,
                  recapUrl: 'https://github.com/northbeam/quote-tool/pull/139#issuecomment-9',
                },
              }
            : {}),
        },
      ],

      // The §7.5 auto-dispatch case: nobody vetted this spec, so the recap leads with that and
      // carries the spec in full. There is no `transcript` here — the recap and the four actions
      // stand on their own, which is the shape a session that failed early actually has.
      ...(engineer ? { recap: derateRecap(now), sessionActions: derateSessionActions() } : {}),
    },

    /* --- 8. Honestly engineer-verified (§27.4) ---------------------------- */
    {
      id: 'req-logger',
      projectId: 'proj-logger',
      projectName: 'Roof logger firmware',
      title: 'Report battery voltage with each reading',
      status: 'in_review',
      createdAt: iso(-2 * DAY, now),
      updatedAt: iso(-5 * HOUR, now),
      lastMilestoneLabel: 'An engineer is checking it',
      needsAttention: false,
      rawText:
        'we cant tell which loggers are about to go flat until they stop reporting. can it send the battery level too',
      cancellable: false,
      spec: {
        id: 'spec-logger-1',
        version: 1,
        title: 'Report battery voltage with each reading',
        outcome:
          'Each reading a logger sends now carries its battery voltage, so a logger that is running low can be spotted before it goes quiet.',
        acceptanceCriteria: [
          { id: 'ac-l1', text: 'Every reading includes a battery voltage.' },
          { id: 'ac-l2', text: 'Loggers already in the field keep working without being re-flashed.' },
          { id: 'ac-l3', text: 'Battery life is not measurably shortened by the extra reading.' },
        ],
        approvedAt: iso(-2 * DAY, now),
        approvedByName: 'Ayesha Rahman',
      },
      refinement: {
        mode: 'build',
        canReply: false,
        charterIsThinking: false,
        messages: [
          {
            id: 'l1',
            author: 'requester',
            kind: 'message',
            createdAt: iso(-2 * DAY, now),
            body: 'we cant tell which loggers are about to go flat until they stop reporting. can it send the battery level too',
          },
        ],
      },
      thread: {
        live: false,
        startedAt: iso(-2 * DAY, now),
        endedAt: iso(-6 * HOUR, now),
        milestones: [
          {
            id: 'lm1',
            kind: 'changing',
            label: 'Making the changes',
            occurredAt: iso(-2 * DAY, now),
            state: 'done',
          },
          {
            id: 'lm2',
            kind: 'checking',
            label: 'Checking it works',
            detail: 'Ran on a real logger on the bench for two hours.',
            occurredAt: iso(-6 * HOUR, now),
            state: 'done',
          },
          {
            id: 'lm3',
            kind: 'status',
            label: 'An engineer is checking it',
            detail: 'This kind of change is checked by an engineer rather than by you clicking it.',
            occurredAt: iso(-5 * HOUR, now),
            state: 'active',
          },
        ],
      },
      artifacts: [
        {
          id: 'art-logger-none',
          kind: 'none',
          state: 'ready',
          audience: 'requester',
          label: 'How this gets checked',
          primary: true,
          payload: {
            explanation:
              'Firmware changes cannot be checked by clicking a link — there is nothing to open. Tomas runs the new firmware on a logger on the bench and reads the results off it. You will be told when it is live.',
          },
        },
        {
          id: 'art-logger-hil',
          kind: 'hil_report',
          state: 'ready',
          audience: 'requester',
          label: 'Bench run',
          primary: false,
          payload: {
            deviceId: 'NB-LOG-0042',
            deviceLabel: 'Roof logger · bench unit 42',
            runDurationMs: 2 * HOUR,
            outcome: 'pass',
            traces: [
              {
                id: 'tr-1',
                label: 'Battery voltage over two hours',
                summary: 'Reported voltage tracked the bench meter to within 20 mV throughout.',
              },
              {
                id: 'tr-2',
                label: 'Current draw during a reading',
                summary: 'No measurable increase against the previous firmware.',
              },
            ],
          },
        },
      ],
    },
  ];

  return requests;
}
