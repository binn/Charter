import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from '@/App';
import { __resetMockState } from '@/api/mock/mockApi';

/**
 * The engineer surface, end to end through the real mock layer.
 *
 * Everything else tests components against hand-built payloads. This one runs the actual
 * `mockApi` — the module that stands in for the server and therefore the module that decides what
 * each persona may see. It is the only place the whole chain is exercised at once: persona →
 * payload shape → which panes exist → linkage across all three.
 *
 * The 12,480-event fixture is real here, so this also demonstrates the case the linkage was built
 * for: a milestone pointing at event 12 while the loaded window starts at 11,981.
 */
vi.mock('@/features/panes/MonacoDiffPane', () => ({
  default: ({ diff }: { diff: { path: string } }) => (
    <div data-testid="monaco-stub">{diff.path}</div>
  ),
}));

function visit(path: string) {
  window.history.replaceState({}, '', path);
  __resetMockState();
}

describe('an engineer opening a finished session', () => {
  beforeEach(() => {
    visit('/requests/req-vertical?mock=engineer');
  });

  it('lands in Developer, because that is the server-held preference for their role (§12)', async () => {
    render(<App />);

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Remember the last selected vertical' }),
    ).toBeInTheDocument();

    const developer = screen.getByRole('radio', { name: 'Developer' });
    expect(developer).toHaveAttribute('aria-checked', 'true');
  });

  it('shows the tail of a twelve-thousand event session without rendering it', async () => {
    render(<App />);

    expect(await screen.findByText('500 of 12,480 events')).toBeInTheDocument();
    expect(screen.getAllByRole('option').length).toBeLessThan(80);
  });

  it('jumps back thousands of events when an early milestone is clicked', async () => {
    const user = userEvent.setup();
    render(<App />);

    const listbox = await screen.findByRole('listbox', { name: 'Agent event stream' });

    await user.click(
      await screen.findByRole('button', { name: /Understanding the current setup/ }),
    );

    // Event 12 is nowhere near the loaded window, so the pane must fetch a window centred on it
    // rather than paging back twenty-five times.
    await waitFor(
      () => {
        const active = listbox.getAttribute('aria-activedescendant');
        expect(active).toBeTruthy();
        expect(document.getElementById(active as string)).toBeInTheDocument();
      },
      { timeout: 3_000 },
    );
  });

  it('renders the §14 recap with its risk-ranked files and no verdict', async () => {
    render(<App />);

    expect(await screen.findByText('Recap for the reviewer')).toBeInTheDocument();
    expect(screen.getByText('Where it deviated, or decided for itself')).toBeInTheDocument();
    expect(screen.getByText(/orientation aid/)).toBeInTheDocument();
  });

  it('offers the four post-hoc actions (§7.5)', async () => {
    render(<App />);

    expect(await screen.findByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Steer' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Revise and rebuild' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Take over' })).toBeInTheDocument();
  });

  it('shows the engineer-only artifact details the requester payload omits', async () => {
    render(<App />);

    // A commit SHA is the canonical thing §27.7 says requesters never see.
    expect((await screen.findAllByText(/4d7b19e/)).length).toBeGreaterThan(0);
  });
});

describe('a requester opening the same request', () => {
  beforeEach(() => {
    visit('/requests/req-vertical?mock=requester');
  });

  it('gets no panes, no recap, no session actions and no SHA', async () => {
    render(<App />);

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Remember the last selected vertical' }),
    ).toBeInTheDocument();

    expect(screen.queryByRole('radio', { name: 'Detailed' })).toBeNull();
    expect(screen.queryByRole('radio', { name: 'Developer' })).toBeNull();
    expect(screen.queryByRole('listbox', { name: 'Agent event stream' })).toBeNull();
    expect(screen.queryByText('Recap for the reviewer')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Take over' })).toBeNull();
    expect(screen.queryByText(/4d7b19e/)).toBeNull();
  });

  it('still gets the whole of pane 1 — the product, not a degraded version of it', async () => {
    render(<App />);

    // "Ready to try" appears both as the status pill and as the final milestone — one thread,
    // one story, which is exactly §11's intent.
    expect((await screen.findAllByText('Ready to try')).length).toBeGreaterThan(0);
    expect(screen.getByText('Understanding the current setup')).toBeInTheDocument();
  });
});
