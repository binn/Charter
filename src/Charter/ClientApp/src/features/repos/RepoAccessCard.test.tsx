import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/api/client';
import type { RepoAccess } from '@/api/types';
import { RepoAccessCard } from '@/features/repos/RepoAccessCard';
import { createTestApi, renderWithProviders } from '@/test/harness';

/**
 * §7.3, guardrail 1. The endpoints existed with nothing calling them, so what these hold down is
 * that the screen tells the truth about the default rather than rendering an empty table: deny by
 * default means the absence of a row *is* the refusal, and a repository can be fully granted and
 * still invisible because §9's smoke test has not passed.
 */

function renderCard(access: RepoAccess, extra: Parameters<typeof createTestApi>[0] = {}) {
  const api = createTestApi({
    getRepoAccess: () => Promise.resolve(access),
    listMembers: () => Promise.resolve([]),
    ...extra,
  });

  renderWithProviders(<RepoAccessCard repoId="repo-1" repoName="northbeam/quote-tool" />, api);
  return api;
}

describe('repo access (§7.3)', () => {
  it('says nobody can file when there is no grant, rather than showing an empty table', async () => {
    renderCard({ grants: [], requesterVisible: true });

    expect(await screen.findByText('Nobody can')).toBeInTheDocument();
    expect(screen.getByText(/the absence of a row/)).toBeInTheDocument();
  });

  it('says a granted repository is still invisible until the smoke test passes', async () => {
    renderCard({
      grants: [{ role: 'requester', canRequest: true }],
      requesterVisible: false,
    });

    expect(await screen.findByText(/its smoke test has not passed/)).toBeInTheDocument();
  });

  it('names the person on a grant rather than showing an id', async () => {
    renderCard({
      grants: [
        {
          memberId: 'member-1',
          memberName: 'Priya Raman',
          memberEmail: 'priya@northbeam.example',
          canRequest: true,
        },
      ],
      requesterVisible: true,
    });

    expect(await screen.findByText('Priya Raman')).toBeInTheDocument();
    expect(screen.getByText('priya@northbeam.example')).toBeInTheDocument();
    expect(screen.queryByText('member-1')).not.toBeInTheDocument();
  });

  it('withholds a role grant rather than deleting it', async () => {
    const setRepoAccess = vi.fn().mockResolvedValue({
      grants: [{ role: 'requester', canRequest: false }],
      requesterVisible: true,
    });

    renderCard({ grants: [{ role: 'requester', canRequest: true }], requesterVisible: true }, {
      setRepoAccess,
    });

    const rows = await screen.findAllByRole('button', { name: 'Withhold' });
    await userEvent.setup().click(rows[0]!);

    await waitFor(() => {
      expect(setRepoAccess).toHaveBeenCalledWith('repo-1', {
        role: 'requester',
        canRequest: false,
      });
    });
  });

  it('shows the server sentence when a grant is refused', async () => {
    renderCard({ grants: [], requesterVisible: true }, {
      setRepoAccess: () =>
        Promise.reject(new ApiError(403, 'Only an administrator can decide who may file against a repository.')),
    });

    const grant = await screen.findAllByRole('button', { name: 'Grant' });
    await userEvent.setup().click(grant[0]!);

    expect(
      await screen.findByText(/Only an administrator can decide who may file/),
    ).toBeInTheDocument();
  });

  it('offers no person picker when the viewer may not read the member list', async () => {
    // An engineer configuring a repository is refused `GET /api/members`. The screen renders what it
    // was given rather than what the viewer's role suggests it might have been.
    renderCard({ grants: [], requesterVisible: true }, {
      listMembers: () => Promise.reject(new ApiError(403, 'Members belong to administrators.')),
    });

    expect(await screen.findByText('Nobody can')).toBeInTheDocument();
    expect(screen.queryByText('Add somebody')).not.toBeInTheDocument();
  });

  it('offers the ungranted members to an administrator', async () => {
    const setRepoAccess = vi.fn().mockResolvedValue({ grants: [], requesterVisible: true });

    renderCard({ grants: [], requesterVisible: true }, {
      listMembers: () =>
        Promise.resolve([
          {
            id: 'member-1',
            displayName: 'Priya Raman',
            email: 'priya@northbeam.example',
            roles: ['requester' as const],
            canCreateRepo: false,
            joinedAt: '2026-05-01T09:00:00.000Z',
            isYou: false,
          },
        ]),
      setRepoAccess,
    });

    await userEvent.setup().click(await screen.findByRole('button', { name: /Priya Raman/ }));

    await waitFor(() => {
      expect(setRepoAccess).toHaveBeenCalledWith('repo-1', {
        memberId: 'member-1',
        canRequest: true,
      });
    });
  });
});
