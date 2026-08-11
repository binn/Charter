import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import type { RequestDetail } from '@/api/types';
import { ApprovalsPage } from '@/pages/ApprovalsPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { ProjectsPage } from '@/pages/ProjectsPage';
import { RepositoriesPage } from '@/pages/RepositoriesPage';
import { RequestListPage } from '@/pages/RequestListPage';
import { RunnersPage } from '@/pages/RunnersPage';
import { ThreePaneView } from '@/features/panes/ThreePaneView';
import { createTestApi, renderWithProviders } from '@/test/harness';

/**
 * §30.5: "Every list view has a designed empty state that tells the user the single next action. No
 * blank tables, no lonely spinner. **This is the entire product on day one** and is routinely left
 * until last."
 *
 * So this file is a checklist of every list surface in the app. A new list view without an entry
 * here is the omission the section is warning about, and the day-one instance is made entirely of
 * these screens.
 */

const AT = '2026-06-07T11:00:00.000Z';

function emptyRequest(overrides: Partial<RequestDetail> = {}): RequestDetail {
  return {
    id: 'req-1',
    projectId: 'proj-1',
    projectName: 'Quote tool',
    title: 'A request',
    status: 'queued',
    createdAt: AT,
    updatedAt: AT,
    needsAttention: false,
    rawText: 'something',
    cancellable: false,
    refinement: { mode: 'build', canReply: false, charterIsThinking: false, messages: [] },
    thread: { live: true, startedAt: AT, milestones: [] },
    artifacts: [],
    ...overrides,
  };
}

describe('every list view has a designed empty state (§30.5)', () => {
  it('requests — names the one action, in the requester’s language', async () => {
    renderWithProviders(<RequestListPage />, createTestApi({ listRequests: () => Promise.resolve([]) }));

    expect(await screen.findByText('Nothing here yet')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Describe what you want changed' }),
    ).toBeInTheDocument();
  });

  it('projects — explains why it is empty rather than looking broken', async () => {
    renderWithProviders(<ProjectsPage />, createTestApi({ listProjects: () => Promise.resolve([]) }));

    expect(await screen.findByText('Nothing shared with you yet')).toBeInTheDocument();
    expect(screen.getByText(/Ask them to add you/)).toBeInTheDocument();
  });

  it('repositories — says what connecting one actually does (§9)', async () => {
    renderWithProviders(<RepositoriesPage />, createTestApi({ listRepos: () => Promise.resolve([]) }));

    expect(await screen.findByText('No repositories connected')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Connect it/ })).toBeInTheDocument();
  });

  it('approvals — says empty is the healthy state, not a failure', async () => {
    renderWithProviders(
      <ApprovalsPage />,
      createTestApi({ listPendingApprovals: () => Promise.resolve([]) }),
    );

    expect(await screen.findByText('Nothing waiting')).toBeInTheDocument();
    expect(screen.getByText(/budgets do the governing instead/)).toBeInTheDocument();
  });

  it('runners — names the next action and when you would even want one', async () => {
    renderWithProviders(
      <RunnersPage />,
      createTestApi({ listRunners: () => Promise.resolve({ agents: [], waiting: [] }) }),
    );

    expect(await screen.findByText('No agents registered')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Generate a pairing token/ })).toBeInTheDocument();
  });

  it('pane 2 — an unstarted session says so instead of showing an empty box', async () => {
    renderWithProviders(
      <ThreePaneView
        request={emptyRequest({ transcript: { events: [], nextCursor: null, totalCount: 0 } })}
      >
        <p>Pane one</p>
      </ThreePaneView>,
      createTestApi(),
    );

    expect(await screen.findByText('No events yet')).toBeInTheDocument();
    expect(screen.getByText(/every tool call the agent makes appears here/)).toBeInTheDocument();
  });

  it('pane 3 — no files changed yet, and what will appear when there are', async () => {
    renderWithProviders(
      <ThreePaneView
        request={emptyRequest({
          transcript: { events: [], nextCursor: null, totalCount: 0 },
          changes: { files: [] },
        })}
      >
        <p>Pane one</p>
      </ThreePaneView>,
      createTestApi(),
    );

    expect(await screen.findByText('No file changes yet')).toBeInTheDocument();
    expect(screen.getByText(/riskiest first/)).toBeInTheDocument();
  });

  it('a dead link — explains rather than showing a bare 404', async () => {
    renderWithProviders(<NotFoundPage />, createTestApi());

    expect(await screen.findByText('Nothing here')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Back to your requests/ })).toBeInTheDocument();
  });
});
