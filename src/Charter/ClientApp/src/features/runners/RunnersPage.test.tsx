import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { RunnersView } from '@/api/types';
import { makeRunners } from '@/api/mock/fixtures-runners';
import { satisfiedBy } from '@/features/runners/capability-matching';
import { RunnersPage } from '@/pages/RunnersPage';
import { createTestApi, renderWithProviders } from '@/test/harness';

const NOW = Date.parse('2026-06-07T12:00:00.000Z');

function renderRunners(view: RunnersView, extra: Parameters<typeof createTestApi>[0] = {}) {
  const api = createTestApi({ listRunners: () => Promise.resolve(view), ...extra });
  renderWithProviders(<RunnersPage />, api);
  return api;
}

describe('capability matching (§27.3, §32.2)', () => {
  const capabilities = makeRunners(NOW).agents[1]!.capabilities;

  it('treats a probed patch version as satisfying a requirement written to the minor', () => {
    // §32.2 probes `xcodebuild -version` and stores what it found — 16.2. A session asks for 16.
    expect(satisfiedBy('xcode:16', capabilities)?.id).toBe('xcode:16.2');
  });

  it('matches a bare capability by id', () => {
    expect(satisfiedBy('macos', capabilities)?.id).toBe('macos');
  });

  it('does not invent a match that is not there', () => {
    expect(satisfiedBy('windows', capabilities)).toBeNull();
    expect(satisfiedBy('xcode:26', capabilities)).toBeNull();
  });
});

describe('Settings → Runners (§33.3)', () => {
  it('lists each agent with its mode, version, concurrency and online state', async () => {
    renderRunners(makeRunners(NOW));

    expect(await screen.findByText('mac-mini-01')).toBeInTheDocument();

    const card = screen.getByText('mac-mini-01').closest('div[class*="rounded-card"]');
    expect(card).not.toBeNull();
    const scope = within(card as HTMLElement);

    expect(scope.getByText('native')).toBeInTheDocument();
    expect(scope.getByText(/agent 0\.4\.1/)).toBeInTheDocument();
    expect(scope.getByText('1 running of 2 allowed')).toBeInTheDocument();
    expect(scope.getByText('online')).toBeInTheDocument();
    // Capabilities are rendered as the matchable identifiers a session is checked against.
    expect(scope.getByText('xcode:16.2')).toBeInTheDocument();
  });

  it('states the online/offline status in words, never by colour alone', async () => {
    renderRunners(makeRunners(NOW));

    expect(await screen.findByText('offline')).toBeInTheDocument();
  });

  it('says plainly when an agent is refusing work over a protocol mismatch (§33.6)', async () => {
    renderRunners(makeRunners(NOW));

    expect(await screen.findByText('Not claiming work.')).toBeInTheDocument();
    expect(screen.getByText(/running agent 0\.3\.9/)).toBeInTheDocument();
  });

  it('explains a routing decision per requirement when a waiting session is picked', async () => {
    const user = userEvent.setup();
    renderRunners(makeRunners(NOW));

    await user.click(
      await screen.findByRole('button', { name: /Hold photos until the tablet is back on wifi/ }),
    );

    // The Mac can run it, and the capability set now says which requirement each capability met.
    const mac = screen.getByText('mac-mini-01').closest('div[class*="rounded-card"]') as HTMLElement;
    expect(within(mac).getByText('met by xcode:16.2')).toBeInTheDocument();
    expect(
      within(mac).getByText('Charter will let this agent claim that session.'),
    ).toBeInTheDocument();

    // The Linux box cannot, and says which requirement is missing rather than just going quiet.
    const linux = screen.getByText('build-01').closest('div[class*="rounded-card"]') as HTMLElement;
    expect(within(linux).getAllByText('not advertised by this agent').length).toBeGreaterThan(0);
    expect(
      within(linux).getByText('Charter will not offer that session to this agent.'),
    ).toBeInTheDocument();
  });

  it('surfaces the server’s explanation when nothing can run a session at all', async () => {
    renderRunners(makeRunners(NOW));

    expect(await screen.findByText('nothing can run this')).toBeInTheDocument();
    expect(
      screen.getByText(/bench-pi has the toolchain and the STM32 attached/),
    ).toBeInTheDocument();
  });

  it('shows the exact pairing command and warns the token is single use (§33.3)', async () => {
    const user = userEvent.setup();
    renderRunners(makeRunners(NOW), {
      createPairingToken: () =>
        Promise.resolve({
          token: 'cpt_abc123',
          command: 'charter-agent --server https://charter.example.com --token cpt_abc123',
          expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
        }),
    });

    await user.click(await screen.findByRole('button', { name: /Generate a pairing token/ }));

    expect(
      await screen.findByText(
        'charter-agent --server https://charter.example.com --token cpt_abc123',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText(/Single use, expires in/)).toBeInTheDocument();
    expect(screen.getByText(/cannot be retrieved again/)).toBeInTheDocument();
  });

  it('warns that revoking kills the jobs running right now, and counts them', async () => {
    const user = userEvent.setup();
    renderRunners(makeRunners(NOW));

    const busy = (await screen.findByText('build-01')).closest(
      'div[class*="rounded-card"]',
    ) as HTMLElement;

    await user.click(within(busy).getByRole('button', { name: 'Revoke' }));

    // build-01 has two jobs in flight; the warning must say so in words, not hint at it.
    expect(await screen.findByText('The 2 jobs running on it right now are killed.')).toBeInTheDocument();
    expect(screen.getByText(/Its credential is invalidated immediately/)).toBeInTheDocument();
  });

  it('refuses to revoke until the agent name is typed exactly', async () => {
    const user = userEvent.setup();
    const revokeAgent = vi.fn(() => Promise.resolve());
    renderRunners(makeRunners(NOW), { revokeAgent });

    const card = (await screen.findByText('build-01')).closest(
      'div[class*="rounded-card"]',
    ) as HTMLElement;
    await user.click(within(card).getByRole('button', { name: 'Revoke' }));

    const confirm = await screen.findByRole('button', { name: 'Revoke this agent now' });
    expect(confirm).toBeDisabled();

    const field = screen.getByLabelText(/Confirm by typing the agent name/);
    await user.type(field, 'build-0');
    expect(confirm).toBeDisabled();

    await user.type(field, '1');
    await waitFor(() => {
      expect(confirm).toBeEnabled();
    });

    await user.click(confirm);
    await waitFor(() => {
      expect(revokeAgent).toHaveBeenCalledWith('agent-linux-01');
    });
  });

  it('offers a designed empty state naming the one next action (§30.5)', async () => {
    renderRunners({ agents: [], waiting: [] });

    expect(await screen.findByText('No agents registered')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /Generate a pairing token/ }),
    ).toBeInTheDocument();
    // And it says why you might not need one at all, rather than implying the product is broken.
    expect(screen.getByText(/without one/)).toBeInTheDocument();
  });
});
