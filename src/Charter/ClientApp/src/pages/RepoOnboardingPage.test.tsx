import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { ApiProvider } from '@/api/ApiProvider';
import type { CharterApi } from '@/api/client';
import type { RepoOnboarding } from '@/api/types';
import { ViewerProvider } from '@/app/ViewerProvider';
import { RepoOnboardingPage } from '@/pages/RepoOnboardingPage';
import { createTestApi } from '@/test/harness';

/**
 * §9 end to end, as one page.
 *
 * The section's own summary is the test plan: connect, recon, confirm scope, **watch the smoke
 * test**, primer, merge gate — and "a repo is invisible to requesters until the smoke test passes",
 * which is a fact the page has to keep saying while it is not yet true.
 */

const AT = '2026-06-07T12:00:00.000Z';

function onboarding(overrides: Partial<RepoOnboarding> = {}): RepoOnboarding {
  return {
    repo: {
      id: 'repo-1',
      fullName: 'northbeam/quote-tool',
      baseBranch: 'main',
      status: 'pending',
      requesterVisible: false,
      hasPrimer: false,
      connectedAt: AT,
      updatedAt: AT,
    },
    steps: [
      { id: 'connect', label: 'Connect the repository', done: true, current: false },
      { id: 'recon', label: 'Let Charter read it', done: false, current: true },
      { id: 'confirm_scope', label: 'Confirm the scope config', done: false, current: false },
      { id: 'smoke_test', label: 'Watch the smoke test', done: false, current: false },
      { id: 'primer', label: 'Publish the primer', done: false, current: false },
      { id: 'merge_gate', label: 'Check the merge gate', done: false, current: false },
    ],
    ...overrides,
  };
}

function renderPage(api: CharterApi) {
  return render(
    <ApiProvider api={api}>
      <MemoryRouter initialEntries={['/settings/repositories/repo-1']}>
        <ViewerProvider>
          <Routes>
            <Route element={<RepoOnboardingPage />} path="/settings/repositories/:id" />
          </Routes>
        </ViewerProvider>
      </MemoryRouter>
    </ApiProvider>,
  );
}

describe('repo onboarding (§9)', () => {
  it('says readiness is earned by the smoke test, not set by hand', async () => {
    renderPage(createTestApi({ getRepoOnboarding: () => Promise.resolve(onboarding()) }));

    expect(await screen.findByRole('heading', { name: 'northbeam/quote-tool' })).toBeInTheDocument();
    expect(screen.getByText(/until its smoke test passes/)).toBeInTheDocument();
    expect(screen.getByText(/no way to set it by hand/)).toBeInTheDocument();
  });

  it('offers recon first, and says it writes nothing', async () => {
    const startRecon = vi.fn().mockResolvedValue({
      status: 'configuring',
      explanation: 'Recon read the repository without writing to it.',
      warnings: [],
    });

    renderPage(
      createTestApi({ getRepoOnboarding: () => Promise.resolve(onboarding()), startRecon }),
    );

    expect(await screen.findByText(/It writes nothing/)).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Start recon' }));

    await waitFor(() => {
      expect(startRecon).toHaveBeenCalledWith('repo-1');
    });
  });

  it('confirms the scope, which is what queues the smoke test', async () => {
    const confirmScope = vi.fn().mockResolvedValue({
      status: 'smoke_test',
      explanation: 'The scope config is open as a pull request.',
      warnings: [],
      pullRequestNumber: 127,
    });

    renderPage(
      createTestApi({
        confirmScope,
        getRepoOnboarding: () =>
          Promise.resolve(
            onboarding({
              repo: {
                id: 'repo-1',
                fullName: 'northbeam/quote-tool',
                baseBranch: 'main',
                status: 'configuring',
                requesterVisible: false,
                hasPrimer: false,
                connectedAt: AT,
                updatedAt: AT,
              },
              proposedScope: {
                detectedStack: ['React 19'],
                commands: [],
                entries: [
                  { path: 'src/app/', kind: 'directory', allowed: true },
                  { path: 'db/migrations/', kind: 'directory', allowed: false, locked: true },
                ],
              },
            }),
          ),
      }),
    );

    await userEvent.setup().click(await screen.findByRole('button', { name: /Confirm this scope/ }));

    await waitFor(() => {
      expect(confirmScope).toHaveBeenCalledWith('repo-1', {
        allow: ['src/app/'],
        deny: ['db/migrations/'],
      });
    });
  });

  it('shows the smoke test running rather than reporting a boolean', async () => {
    renderPage(
      createTestApi({
        getRepoOnboarding: () =>
          Promise.resolve(
            onboarding({
              repo: {
                id: 'repo-1',
                fullName: 'northbeam/quote-tool',
                baseBranch: 'main',
                status: 'smoke_test',
                requesterVisible: false,
                hasPrimer: false,
                connectedAt: AT,
                updatedAt: AT,
              },
              lastSmokeTest: {
                passed: false,
                at: AT,
                previewBound: false,
                checkpoints: [
                  { id: 'request_filed', label: 'Filed a trivial request', state: 'passed' },
                  { id: 'agent_ran', label: 'The agent ran', state: 'running' },
                  { id: 'checks_passed', label: 'Your checks passed', state: 'pending' },
                  { id: 'pull_request', label: 'A pull request opened', state: 'pending' },
                  { id: 'preview_deployed', label: 'A preview deployed', state: 'pending' },
                  { id: 'url_bound', label: 'The preview URL bound back', state: 'pending' },
                ],
              },
            }),
          ),
      }),
    );

    expect(await screen.findByText('Running now')).toBeInTheDocument();
    expect(screen.getByText('The agent ran')).toBeInTheDocument();
    expect(screen.getByText('The preview URL bound back')).toBeInTheDocument();
  });

  it('will not publish an empty primer, and says why the button is off', async () => {
    renderPage(
      createTestApi({
        getRepoOnboarding: () => Promise.resolve(onboarding()),
      }),
    );

    expect(await screen.findByRole('button', { name: /Publish the primer/ })).toBeDisabled();
    expect(screen.getByText(/There is nothing to publish yet/)).toBeInTheDocument();
  });
});
