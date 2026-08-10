import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { SessionActions } from '@/api/types';
import { PostHocActions } from '@/features/session/PostHocActions';
import { createTestApi, renderWithProviders } from '@/test/harness';

const BRANCH = 'charter/remember-vertical';

function actions(overrides: Partial<SessionActions> = {}): SessionActions {
  return {
    canApprove: true,
    canSteer: true,
    canRevise: true,
    canTakeOver: true,
    branch: BRANCH,
    ...overrides,
  };
}

function renderActions(
  session: SessionActions,
  extra: Parameters<typeof createTestApi>[0] = {},
) {
  const api = createTestApi(extra);
  renderWithProviders(
    <PostHocActions actions={session} onDone={() => {}} requestId="req-1" specTitle="A spec" />,
    api,
  );
  return api;
}

describe('post-hoc session actions (§7.5)', () => {
  it('offers all four as peers, not one primary and a menu', async () => {
    renderActions(actions());

    expect(await screen.findByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Steer' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Revise and rebuild' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Take over' })).toBeInTheDocument();
  });

  it('omits an action the viewer may not take rather than disabling it', async () => {
    renderActions(actions({ canApprove: false }));

    await screen.findByRole('button', { name: 'Steer' });
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
  });

  it('names the branch, and says Charter has no merge button', async () => {
    renderActions(actions());

    expect(await screen.findByText(BRANCH)).toBeInTheDocument();
    expect(screen.getByText(/Charter has no merge button/)).toBeInTheDocument();
  });

  it('steers the existing session on the same branch', async () => {
    const user = userEvent.setup();
    const steerSession = vi.fn(() => Promise.resolve());
    renderActions(actions(), { steerSession });

    await user.click(await screen.findByRole('button', { name: 'Steer' }));
    expect(screen.getByText(/same branch, in the same thread/)).toBeInTheDocument();

    await user.type(
      screen.getByLabelText('What should it do differently?'),
      'Use the employee id instead.',
    );
    await user.click(screen.getByRole('button', { name: 'Send instruction' }));

    await waitFor(() => {
      expect(steerSession).toHaveBeenCalledWith('req-1', 'Use the employee id instead.');
    });
  });

  it('will not send an empty steering instruction', async () => {
    const user = userEvent.setup();
    renderActions(actions());

    await user.click(await screen.findByRole('button', { name: 'Steer' }));
    expect(screen.getByRole('button', { name: 'Send instruction' })).toBeDisabled();
  });

  it('forks the spec on revise and rebuild', async () => {
    const user = userEvent.setup();
    const reviseSession = vi.fn(() => Promise.resolve());
    renderActions(actions(), { reviseSession });

    await user.click(await screen.findByRole('button', { name: 'Revise and rebuild' }));
    const field = screen.getByLabelText('The revised specification');
    await user.clear(field);
    await user.type(field, 'Key it on the employee record.');
    await user.click(screen.getByRole('button', { name: 'Rebuild from this' }));

    await waitFor(() => {
      expect(reviseSession).toHaveBeenCalledWith('req-1', 'Key it on the employee record.');
    });
  });
});

describe('Take over is the consequential one (§7.5)', () => {
  it('spells out that agent writes to the branch stop, before anything happens', async () => {
    const user = userEvent.setup();
    const takeOverSession = vi.fn(() => Promise.resolve());
    renderActions(actions(), { takeOverSession });

    await user.click(await screen.findByRole('button', { name: 'Take over' }));

    expect(screen.getByText('Take over this branch')).toBeInTheDocument();
    expect(screen.getByText(/Charter stops writing to/)).toBeInTheDocument();
    expect(screen.getByText(/Steer and Revise will no longer be offered/)).toBeInTheDocument();
    expect(screen.getByText(/is stopped and its cost settled/)).toBeInTheDocument();
    // Nothing has been called merely by opening the confirmation.
    expect(takeOverSession).not.toHaveBeenCalled();
  });

  it('cannot be triggered without typing the branch name exactly', async () => {
    const user = userEvent.setup();
    const takeOverSession = vi.fn(() => Promise.resolve());
    renderActions(actions(), { takeOverSession });

    await user.click(await screen.findByRole('button', { name: 'Take over' }));

    const confirm = screen.getByRole('button', { name: 'Stop agent writes and take over' });
    expect(confirm).toBeDisabled();

    await user.type(screen.getByLabelText(/Confirm by typing the branch name/), 'charter/wrong');
    expect(confirm).toBeDisabled();
    expect(takeOverSession).not.toHaveBeenCalled();
  });

  it('goes through once the branch name matches', async () => {
    const user = userEvent.setup();
    const takeOverSession = vi.fn(() => Promise.resolve());
    renderActions(actions(), { takeOverSession });

    await user.click(await screen.findByRole('button', { name: 'Take over' }));
    await user.type(screen.getByLabelText(/Confirm by typing the branch name/), BRANCH);

    const confirm = screen.getByRole('button', { name: 'Stop agent writes and take over' });
    await waitFor(() => {
      expect(confirm).toBeEnabled();
    });
    await user.click(confirm);

    await waitFor(() => {
      expect(takeOverSession).toHaveBeenCalledWith('req-1');
    });
  });

  it('can be abandoned with Escape, since the honest answer may be "let me go and look"', async () => {
    const user = userEvent.setup();
    renderActions(actions());

    await user.click(await screen.findByRole('button', { name: 'Take over' }));
    expect(screen.getByText('Take over this branch')).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByText('Take over this branch')).toBeNull();
    });
    expect(screen.getByRole('button', { name: 'Take over' })).toBeInTheDocument();
  });

  it('withdraws steering entirely once a session has been handed off', async () => {
    renderActions(
      actions({
        canApprove: false,
        canSteer: false,
        canRevise: false,
        canTakeOver: false,
        handedOff: { at: new Date().toISOString(), byName: 'Tomas Beck' },
      }),
    );

    expect(await screen.findByText('This session was taken over')).toBeInTheDocument();
    expect(screen.getByText(/is not writing to that branch any more/)).toBeInTheDocument();
    // Not disabled — gone. A hand-off is not a mode you can toggle back off.
    expect(screen.queryByRole('button', { name: 'Steer' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Take over' })).toBeNull();
  });
});
