import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type {
  ChangesPane,
  FileDiff,
  Milestone,
  RequestDetail,
  TranscriptEvent,
  TranscriptPane,
} from '@/api/types';
import { Card, SectionLabel } from '@/components/ui/Card';
import { StatusThreadView } from '@/features/status/StatusThreadView';
import { ThreePaneView } from '@/features/panes/ThreePaneView';
import { createTestApi, renderWithProviders } from '@/test/harness';

/**
 * Monaco is stubbed here deliberately.
 *
 * These tests are about §12's *linkage* — that activating one pane moves another — not about
 * Microsoft's editor. Loading several megabytes of it into jsdom to assert that a path appears
 * would be slow and would test the wrong thing. `panes.bundle.test.ts` covers the property that
 * actually matters about Monaco: that it is not statically reachable from the entry.
 */
vi.mock('@/features/panes/MonacoDiffPane', () => ({
  default: ({ diff, hunkIndex }: { diff: FileDiff; hunkIndex?: number }) => (
    <div data-testid="monaco-stub">
      {diff.path} at hunk {hunkIndex ?? 0}
    </div>
  ),
}));

const AT = '2026-06-07T11:00:00.000Z';

function milestone(id: string, label: string, eventSeq?: number): Milestone {
  return {
    id,
    kind: 'changing',
    label,
    occurredAt: AT,
    state: 'done',
    ...(eventSeq === undefined ? {} : { eventSeq }),
  };
}

function event(seq: number, partial: Partial<TranscriptEvent> = {}): TranscriptEvent {
  return {
    seq,
    kind: 'tool_use',
    type: 'tool_execution_start',
    summary: `read file-${seq}.cs`,
    createdAt: AT,
    milestoneId: 'ms-changing',
    level: 'info',
    ...partial,
  };
}

/** Short enough that the whole list lands inside the virtualizer's overscan under jsdom. */
const EVENTS: TranscriptEvent[] = [
  event(1, { milestoneId: 'ms-understanding' }),
  event(2, { milestoneId: 'ms-understanding' }),
  event(3),
  event(4, {
    kind: 'file_write',
    summary: 'Wrote the migration',
    path: 'src/Data/Migrations/AddPreference.cs',
    hunkIndex: 1,
  }),
  event(5),
  event(6, { kind: 'diagnostic', level: 'error', summary: 'a test failed' }),
];

const TRANSCRIPT: TranscriptPane = { events: EVENTS, nextCursor: null, totalCount: EVENTS.length };

const CHANGES: ChangesPane = {
  files: [
    {
      path: 'src/Data/Migrations/AddPreference.cs',
      additions: 34,
      deletions: 0,
      risk: 'high',
      riskReasons: ['Database migration'],
    },
    { path: 'tests/DefaultsTests.cs', additions: 40, deletions: 0, risk: 'low' },
  ],
};

const DIFF: FileDiff = {
  path: 'src/Data/Migrations/AddPreference.cs',
  language: 'csharp',
  originalText: '',
  modifiedText: 'public class AddPreference {}\n',
  hunks: [
    { id: 'h0', header: '@@ -0,0 +1,2 @@', originalStartLine: 0, modifiedStartLine: 1 },
    { id: 'h1', header: '@@ -1,1 +1,1 @@', originalStartLine: 1, modifiedStartLine: 1 },
  ],
  binary: false,
  truncated: false,
};

function makeRequest(overrides: Partial<RequestDetail> = {}): RequestDetail {
  return {
    id: 'req-test',
    projectId: 'proj-1',
    projectName: 'Quote tool',
    title: 'Remember the last selected vertical',
    status: 'preview_ready',
    createdAt: AT,
    updatedAt: AT,
    needsAttention: false,
    rawText: 'remember what i picked',
    cancellable: false,
    refinement: { mode: 'build', canReply: false, charterIsThinking: false, messages: [] },
    thread: {
      live: false,
      startedAt: AT,
      milestones: [
        milestone('ms-understanding', 'Understanding the current setup', 1),
        milestone('ms-changing', 'Making the changes', 3),
        milestone('ms-outcome', 'Ready to try'),
      ],
    },
    artifacts: [],
    ...overrides,
  };
}

function renderPanes(request: RequestDetail, persona: 'engineer' | 'requester') {
  const api = createTestApi(
    {
      getTranscript: () => Promise.resolve(TRANSCRIPT),
      getFileDiff: () => Promise.resolve(DIFF),
    },
    persona,
  );

  return renderWithProviders(
    <ThreePaneView request={request}>
      <Card className="p-4">
        <SectionLabel>Progress</SectionLabel>
        <StatusThreadView thread={request.thread} />
      </Card>
    </ThreePaneView>,
    api,
  );
}

describe('ThreePaneView — pane availability is a permission (§7.4, §12)', () => {
  it('gives a requester no mode picker at all, because the payload carries no panes', async () => {
    // The requester's `RequestDetail` has no `transcript` and no `changes` keys — the API omits
    // them. This is the shape the real server must send, not a client-side filter.
    renderPanes(makeRequest(), 'requester');

    expect(await screen.findByText('Making the changes')).toBeInTheDocument();

    expect(screen.queryByRole('radiogroup', { name: 'How much detail to show' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Detailed' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Developer' })).toBeNull();
    expect(screen.queryByRole('listbox', { name: 'Agent event stream' })).toBeNull();
  });

  it('leaves the milestones as plain text when there is no pane to link into', async () => {
    renderPanes(makeRequest(), 'requester');

    await screen.findByText('Making the changes');
    // Not a button: with no pane 2 there is nothing to jump to, and a control that does nothing is
    // worse than no control.
    expect(screen.queryByRole('button', { name: /Making the changes/ })).toBeNull();
  });

  it('offers Detailed only when a transcript was sent, and Developer only with changes', async () => {
    renderPanes(makeRequest({ transcript: TRANSCRIPT }), 'engineer');

    expect(await screen.findByRole('radio', { name: 'Detailed' })).toBeInTheDocument();
    // No `changes` in this payload, so no Developer mode — even though the viewer is an engineer
    // with repo read. Availability follows the data, never the role.
    expect(screen.queryByRole('radio', { name: 'Developer' })).toBeNull();
  });

  it('offers all three when the payload carries both panes', async () => {
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    expect(await screen.findByRole('radio', { name: 'Simple' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Detailed' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Developer' })).toBeInTheDocument();
  });
});

describe('ThreePaneView — the panes are linked (§12)', () => {
  it('marks the run of events a milestone produced when that milestone is clicked', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    const listbox = await screen.findByRole('listbox', { name: 'Agent event stream' });
    // Nothing is selected before the first interaction.
    expect(listbox).not.toHaveAttribute('aria-activedescendant');

    await user.click(screen.getByRole('button', { name: /Understanding the current setup/ }));

    // The milestone points at event 1, so that option becomes the active descendant — which is how
    // a virtualized listbox reports its selection.
    await waitFor(() => {
      const active = listbox.getAttribute('aria-activedescendant');
      expect(active).toBeTruthy();
      expect(document.getElementById(active as string)).toHaveTextContent('read file-1.cs');
    });
  });

  it('scrolls pane 2 to a different place when a different milestone is clicked', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    const listbox = await screen.findByRole('listbox', { name: 'Agent event stream' });

    await user.click(screen.getByRole('button', { name: /Making the changes/ }));

    await waitFor(() => {
      const active = listbox.getAttribute('aria-activedescendant');
      expect(document.getElementById(active as string)).toHaveTextContent('read file-3.cs');
    });
  });

  it('opens pane 3 at the exact hunk when a file-write event is clicked', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    const writeEvent = await screen.findByText('Wrote the migration');
    await user.click(writeEvent);

    // Event 4 carries `hunkIndex: 1`, so pane 3 must open at hunk 1 and not at the top of the file.
    const stub = await screen.findByTestId('monaco-stub');
    expect(stub).toHaveTextContent('src/Data/Migrations/AddPreference.cs at hunk 1');
  });

  it('does not open pane 3 for an event that wrote no file', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    await user.click(await screen.findByText('read file-3.cs'));

    expect(screen.queryByTestId('monaco-stub')).toBeNull();
    expect(
      screen.getByText(/click a file change in the Detailed pane/i),
    ).toBeInTheDocument();
  });

  it('drives the same linkage from the keyboard, with one tab stop into the stream', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    const listbox = await screen.findByRole('listbox', { name: 'Agent event stream' });
    listbox.focus();

    // Arrow keys move the active option without activating it — manual activation, so arrowing
    // through a session does not fire a diff fetch per keystroke.
    await user.keyboard('{Home}');
    await waitFor(() => {
      expect(listbox.getAttribute('aria-activedescendant')).toBeTruthy();
    });
    expect(screen.queryByTestId('monaco-stub')).toBeNull();

    await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}');
    await waitFor(() => {
      const active = listbox.getAttribute('aria-activedescendant');
      expect(document.getElementById(active as string)).toHaveTextContent('Wrote the migration');
    });

    await user.keyboard('{Enter}');
    expect(await screen.findByTestId('monaco-stub')).toHaveTextContent('at hunk 1');
  });

  it('lets pane 3 be driven from its own file list too', async () => {
    const user = userEvent.setup();
    renderPanes(makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }), 'engineer');

    await user.click(await screen.findByRole('button', { name: /tests\/DefaultsTests\.cs/ }));

    expect(await screen.findByTestId('monaco-stub')).toBeInTheDocument();
  });

  it('announces the jump, so the linkage is not visual-only', async () => {
    const user = userEvent.setup();
    const { container } = renderPanes(
      makeRequest({ transcript: TRANSCRIPT, changes: CHANGES }),
      'engineer',
    );

    await user.click(await screen.findByRole('button', { name: /Making the changes/ }));

    const live = container.querySelector('[aria-live="polite"].sr-only');
    await waitFor(() => {
      expect(live).toHaveTextContent('Detailed view moved to the events for: Making the changes');
    });
  });
});

describe('TranscriptPaneView — the stream is virtualized (§12)', () => {
  it('renders a small fraction of a long session, and says how much of it it holds', async () => {
    const many: TranscriptEvent[] = Array.from({ length: 12_480 }, (_, index) =>
      event(index + 1),
    );
    const tail = many.slice(-500);

    renderPanes(
      makeRequest({
        transcript: { events: tail, nextCursor: '11980', totalCount: many.length },
      }),
      'engineer',
    );

    await screen.findByRole('listbox', { name: 'Agent event stream' });

    expect(screen.getByText('500 of 12,480 events')).toBeInTheDocument();

    // The load-bearing assertion: 500 events are in state, but nowhere near 500 rows are in the
    // DOM. Without virtualization this is 500 nodes here and 12,480 after paging back.
    const rendered = screen.getAllByRole('option');
    expect(rendered.length).toBeLessThan(50);
  });

  it('offers to page backwards only while there are older events (cursor pagination)', async () => {
    renderPanes(
      makeRequest({ transcript: { events: EVENTS, nextCursor: '500', totalCount: 1_000 } }),
      'engineer',
    );

    expect(await screen.findByRole('button', { name: /Load earlier events/ })).toBeInTheDocument();
  });

  it('hides the pager once the beginning of the session has been reached', async () => {
    renderPanes(makeRequest({ transcript: TRANSCRIPT }), 'engineer');

    await screen.findByRole('listbox', { name: 'Agent event stream' });
    expect(screen.queryByRole('button', { name: /Load earlier events/ })).toBeNull();
  });

  it('labels an error event with a word as well as a colour', async () => {
    renderPanes(makeRequest({ transcript: TRANSCRIPT }), 'engineer');

    await screen.findByText('a test failed');
    expect(screen.getByText('error')).toBeInTheDocument();
  });
});
