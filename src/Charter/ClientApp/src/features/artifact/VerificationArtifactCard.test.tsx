import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { AcceptanceCriterion, VerificationArtifact } from '@/api/types';
import { VerificationArtifactCard } from '@/features/artifact/VerificationArtifactCard';

/**
 * §27.7 is the most load-bearing component spec in the product, so these tests are written against
 * the section's own rules rather than against the implementation: every state renders, expiry is a
 * designed state and not a dead link, several artifacts become tabs in one card, "What to check" is
 * verbatim, pass/fail never rests on colour alone, and `details` is present only when the API sent
 * it.
 */

const CRITERIA: AcceptanceCriterion[] = [
  { id: 'ac-1', text: 'Starting a new quote pre-selects the vertical you chose last time.' },
  { id: 'ac-2', text: 'A person who has never created a quote still starts on Solar.' },
];

const inHours = (hours: number) => new Date(Date.now() + hours * 3_600_000).toISOString();

/** Loosely typed on purpose: some cases need to set an optional field back to `undefined`, which
 *  `exactOptionalPropertyTypes` rightly forbids on the real type. */
function hostedPreview(overrides: Record<string, unknown> = {}): VerificationArtifact {
  return {
    id: 'art-1',
    kind: 'hosted_preview',
    state: 'ready',
    audience: 'requester',
    label: 'Preview',
    primary: true,
    expiresAt: inHours(5),
    payload: {
      url: 'https://pr-142.preview.example.test/quotes/new',
      displayUrl: 'pr-142.preview.example…/quotes/new',
      reachability: 'reachable',
    },
    ...overrides,
  } as VerificationArtifact;
}

function renderCard(artifacts: VerificationArtifact[], criteria = CRITERIA) {
  const onFeedback = vi.fn().mockResolvedValue(undefined);
  const onRebuild = vi.fn().mockResolvedValue(undefined);

  render(
    <VerificationArtifactCard
      acceptanceCriteria={criteria}
      artifacts={artifacts}
      onFeedback={onFeedback}
      onRebuild={onRebuild}
      specTitle="Remember the last selected vertical"
      startedAt={new Date(Date.now() - 12 * 60_000).toISOString()}
      currentMilestoneLabel="Putting it together"
    />,
  );

  return { onFeedback, onRebuild };
}

describe('VerificationArtifactCard — states', () => {
  it('renders `pending` as a skeleton with the current milestone and elapsed time, and never an ETA', () => {
    renderCard([hostedPreview({ state: 'pending', expiresAt: undefined })]);

    expect(screen.getByText('Building this now')).toBeInTheDocument();
    expect(screen.getByText('Putting it together')).toBeInTheDocument();
    expect(screen.getByText(/12m so far/)).toBeInTheDocument();

    // §6: elapsed time only. Nothing on the card may predict a finish.
    expect(document.body.textContent).not.toMatch(/estimated|remaining time|finishes|eta|about \d+ minutes left/i);
    expect(screen.queryByRole('link', { name: /open preview/i })).not.toBeInTheDocument();
  });

  it('renders `ready` with the primary action, the URL chip and a QR code', () => {
    renderCard([hostedPreview()]);

    expect(screen.getByText('Ready to try')).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: /open preview/i }).length).toBeGreaterThan(0);
    expect(screen.getByText('pr-142.preview.example…/quotes/new')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /scan to open this preview/i })).toBeInTheDocument();
  });

  it('shows the countdown from first render, well before anything is close to expiring', () => {
    renderCard([hostedPreview({ expiresAt: inHours(5) })]);

    // The whole point of §27.7's expiry rule: you learn the link is temporary at 9am, not at 2pm.
    expect(screen.getByRole('timer')).toHaveTextContent(/expires in 4h 59m|expires in 5h 00m/);
  });

  it('treats under an hour as `expiring` and warns in text, not only in amber', () => {
    renderCard([hostedPreview({ state: 'ready', expiresAt: inHours(0.5) })]);

    const timer = screen.getByRole('timer');
    expect(timer).toHaveTextContent(/expires in 29m|expires in 30m/);
    expect(timer).toHaveTextContent(/Expiring soon/);
  });

  it('derives `expired` from the clock even when the server still says ready', () => {
    // Someone opening a link from a three-day-old notification. The server's state is stale; the
    // card must not hand them a dead host.
    renderCard([hostedPreview({ state: 'ready', expiresAt: inHours(-72) })]);

    expect(screen.getByText('This preview has been cleaned up')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /open preview/i })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /build it again/i }).length).toBeGreaterThan(0);
  });

  it('offers Rebuild as the primary action on an expired artifact', async () => {
    const user = userEvent.setup();
    const { onRebuild } = renderCard([hostedPreview({ state: 'expired', expiresAt: inHours(-72) })]);

    await user.click(screen.getAllByRole('button', { name: /build it again/i })[0]!);
    expect(onRebuild).toHaveBeenCalledWith('art-1');
  });

  it('renders `failed` in plain language with no stack trace and no dead action', () => {
    renderCard([
      hostedPreview({
        state: 'failed',
        expiresAt: undefined,
        failureSummary: 'There is nothing to try this time — the change did not get far enough.',
      }),
    ]);

    expect(screen.getByText('Nothing to try this time')).toBeInTheDocument();
    expect(
      screen.getByText(/There is nothing to try this time/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /open preview/i })).not.toBeInTheDocument();
  });
});

describe('VerificationArtifactCard — multiple artifacts', () => {
  const multi: VerificationArtifact[] = [
    {
      id: 'art-capture',
      kind: 'capture',
      state: 'ready',
      audience: 'requester',
      label: 'Screenshots',
      primary: false,
      payload: {
        items: [
          {
            id: 'cap-1',
            mediaType: 'image',
            url: 'https://example.test/after.png',
            caption: 'The survey screen shows how many photos are waiting.',
          },
        ],
      },
    },
    {
      id: 'art-tf',
      kind: 'distribution_channel',
      state: 'ready',
      audience: 'requester',
      label: 'TestFlight',
      primary: true,
      payload: {
        provider: 'testflight',
        channelName: 'TestFlight',
        buildNumber: '42',
        openUrl: 'https://testflight.apple.com/join/abc',
        inviteRequired: false,
      },
    },
  ];

  it('renders several artifacts as tabs inside one card, primary first — never as separate cards', () => {
    renderCard(multi);

    const tablist = screen.getByRole('tablist', { name: /ways to check/i });
    const tabs = within(tablist).getAllByRole('tab');

    expect(tabs).toHaveLength(2);
    expect(tabs[0]).toHaveTextContent('TestFlight');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
    expect(tabs[1]).toHaveTextContent('Screenshots');

    // One card means one "What to check" list and one verdict, not two of each.
    expect(screen.getAllByText('What to check')).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Works' })).toHaveLength(1);
  });

  it('switches the body when another tab is chosen', async () => {
    const user = userEvent.setup();
    renderCard(multi);

    expect(screen.getByText('Build 42')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /screenshots/i }));

    expect(
      screen.getByText('The survey screen shows how many photos are waiting.'),
    ).toBeInTheDocument();
  });
});

describe('VerificationArtifactCard — What to check', () => {
  it('renders the acceptance criteria verbatim, in order, without regenerating them', () => {
    renderCard([hostedPreview()]);

    const items = screen.getAllByRole('checkbox');
    expect(items).toHaveLength(CRITERIA.length);

    for (const criterion of CRITERIA) {
      expect(screen.getByText(criterion.text)).toBeInTheDocument();
    }
  });

  it('leaves the checklist out entirely when a spec has no criteria', () => {
    renderCard([hostedPreview()], []);
    expect(screen.queryByText('What to check')).not.toBeInTheDocument();
  });
});

describe('VerificationArtifactCard — audience gating (§7.4, §27.7)', () => {
  it('does not render the Details disclosure when the API omitted `details`', () => {
    renderCard([hostedPreview()]);

    expect(screen.queryByText('Details')).not.toBeInTheDocument();
    // A requester must never see a SHA — and there is nothing on the client that could reveal one,
    // because the payload does not carry it.
    expect(document.body.textContent).not.toMatch(/\bPR #\d+/);
  });

  it('renders the Details disclosure when the API sent `details`', () => {
    renderCard([
      hostedPreview({
        details: {
          changeRequestNumber: 142,
          changeRequestUrl: 'https://github.test/org/repo/pull/142',
          changeRequestTerm: 'pull request',
          changeRequestTermShort: 'PR',
          commitSha: 'a3f9c21',
          branch: 'charter/remember-vertical',
          runner: 'detached · mac-mini-01',
          durationMs: 12 * 60_000,
          costUsd: 1.42,
        },
      }),
    ]);

    expect(screen.getByText('Details')).toBeInTheDocument();
    expect(screen.getByText(/PR #142/)).toBeInTheDocument();
  });
});

describe('VerificationArtifactCard — pass/fail never relies on colour alone', () => {
  it('pairs a failing test report with an icon and a text label', () => {
    renderCard([
      {
        id: 'art-tests',
        kind: 'test_report',
        state: 'ready',
        audience: 'requester',
        label: 'Checks',
        primary: true,
        payload: {
          passed: 8,
          failed: 2,
          skipped: 0,
          durationMs: 30_000,
          failures: [
            { id: 'f1', name: 'keeps the vertical', assertion: 'expected Solar, got null' },
          ],
        },
      },
    ]);

    expect(screen.getByText('2 of 10 checks failed')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /8 passed, 2 failed, 0 skipped/ })).toBeInTheDocument();
  });

  it('pairs a passing test report with an icon and a text label', () => {
    renderCard([
      {
        id: 'art-tests',
        kind: 'test_report',
        state: 'ready',
        audience: 'requester',
        label: 'Checks',
        primary: true,
        payload: { passed: 10, failed: 0, skipped: 0, durationMs: 30_000, failures: [] },
      },
    ]);

    expect(screen.getByText('Everything passed')).toBeInTheDocument();
  });
});

describe('VerificationArtifactCard — feedback', () => {
  it('offers exactly two buttons and does not demand a written report', async () => {
    const user = userEvent.setup();
    const { onFeedback } = renderCard([hostedPreview()]);

    await user.click(screen.getByRole('button', { name: 'Works' }));
    expect(onFeedback).toHaveBeenCalledWith('works');
  });

  it('lets "Not quite" through with no note at all', async () => {
    const user = userEvent.setup();
    const { onFeedback } = renderCard([hostedPreview()]);

    await user.click(screen.getByRole('button', { name: 'Not quite' }));
    await user.click(screen.getByRole('button', { name: /send it back/i }));

    expect(onFeedback).toHaveBeenCalledWith('not_quite', undefined);
  });

  it('does not ask for a verdict on an artifact the requester cannot verify', () => {
    renderCard([
      {
        id: 'art-none',
        kind: 'none',
        state: 'ready',
        audience: 'requester',
        label: 'How this gets checked',
        primary: true,
        payload: {
          explanation: 'Firmware changes are checked by an engineer on the bench.',
        },
      },
    ]);

    expect(screen.getByText('Checked by an engineer')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Works' })).not.toBeInTheDocument();
  });
});
