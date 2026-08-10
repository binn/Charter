import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { SetupChecklist as SetupChecklistData } from '@/api/types';
import { makeSetupChecklist } from '@/api/mock/fixtures-setup';
import { SetupChecklist } from '@/features/setup/SetupChecklist';
import { createTestApi, renderWithProviders } from '@/test/harness';

function renderChecklist(
  checklist: SetupChecklistData | null,
  extra: Parameters<typeof createTestApi>[0] = {},
) {
  const api = createTestApi({ getSetupChecklist: () => Promise.resolve(checklist), ...extra });
  renderWithProviders(<SetupChecklist />, api);
  return api;
}

function allDone(): SetupChecklistData {
  const checklist = makeSetupChecklist();
  return { tasks: checklist.tasks.map((task) => ({ ...task, done: true })) };
}

describe('admin setup checklist (§30.2)', () => {
  it('renders nothing at all when the API sent no checklist', async () => {
    const { container } = renderWithProviders(
      <SetupChecklist />,
      createTestApi({ getSetupChecklist: () => Promise.resolve(null) }),
    );

    // A requester's dashboard has no checklist, not an empty one.
    await waitFor(() => {
      expect(container.querySelector('[role="progressbar"]')).toBeNull();
    });
    expect(screen.queryByText('Setting up Charter')).toBeNull();
  });

  it('is a persistent checklist rather than a modal wizard', async () => {
    renderChecklist(makeSetupChecklist());

    await screen.findByText('Setting up Charter');
    // Nothing traps the user: no dialog, and every step is reachable in any order.
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByText(/Leave and come back whenever you like/)).toBeInTheDocument();
  });

  it('reports progress as text and as a real progressbar, not just a coloured strip', async () => {
    renderChecklist(makeSetupChecklist());

    expect(await screen.findByText('4 of 7 done')).toBeInTheDocument();

    const meter = screen.getByRole('progressbar', { name: 'Setup progress' });
    expect(meter).toHaveAttribute('aria-valuenow', '4');
    expect(meter).toHaveAttribute('aria-valuemax', '7');
    expect(meter).toHaveAttribute('aria-valuetext', '4 of 7 steps done');
  });

  it('shows what each finished step actually configured, not just a tick', async () => {
    renderChecklist(makeSetupChecklist());

    expect(await screen.findByText('Northbeam Solar')).toBeInTheDocument();
    expect(screen.getByText('Anthropic · subscription OAuth')).toBeInTheDocument();
    expect(screen.getByText('1 repository · quote-tool')).toBeInTheDocument();
  });

  it('keeps every outstanding step actionable, explaining order rather than enforcing it', async () => {
    renderChecklist(makeSetupChecklist());

    // `notification_channels` is blocked by `invite_people`, but it is still a link.
    const blocked = await screen.findByText('Choose notification channels');
    const row = blocked.closest('a');
    expect(row).not.toBeNull();
    expect(screen.getByText(/but you can do it now if you prefer/)).toBeInTheDocument();
  });

  it('marks a step that leaves Charter, because that is why a wizard would have trapped them', async () => {
    const checklist = makeSetupChecklist();
    const tasks = checklist.tasks.map((task) =>
      task.id === 'connect_github' ? { ...task, done: false } : task,
    );
    renderChecklist({ tasks });

    expect(await screen.findByText('leaves Charter')).toBeInTheDocument();
    const link = screen.getByText('Connect GitHub').closest('a');
    expect(link).toHaveAttribute('target', '_blank');
  });

  it('is dismissible only once everything is done', async () => {
    renderChecklist(makeSetupChecklist());
    await screen.findByText('Setting up Charter');
    expect(screen.queryByRole('button', { name: /Hide this checklist/ })).toBeNull();
  });

  it('dismisses server-side, and stays gone (§30.2 — no browser storage)', async () => {
    const user = userEvent.setup();
    const dismissSetupChecklist = vi.fn(() =>
      Promise.resolve({ tasks: allDone().tasks, dismissedAt: new Date().toISOString() }),
    );
    renderChecklist(allDone(), { dismissSetupChecklist });

    expect(await screen.findByText('7 of 7 done')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Hide this checklist/ }));

    await waitFor(() => {
      expect(screen.queryByText('Setting up Charter')).toBeNull();
    });
    // The dismissal is a server call, not a localStorage write.
    expect(dismissSetupChecklist).toHaveBeenCalled();
  });

  it('stays hidden on a later visit once the server says it was dismissed', async () => {
    renderChecklist({ tasks: allDone().tasks, dismissedAt: '2026-06-01T00:00:00.000Z' });

    await waitFor(() => {
      expect(screen.queryByText('Setting up Charter')).toBeNull();
    });
  });
});
