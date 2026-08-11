import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/api/client';
import type { Member } from '@/api/types';
import { makeAuditLog, makeMembers } from '@/api/mock/fixtures-admin';
import { AuditPage } from '@/pages/AuditPage';
import { MembersPage } from '@/pages/MembersPage';
import { createTestApi, renderWithProviders } from '@/test/harness';

/**
 * §7.1's administrator column, and §7.3's fifth guardrail.
 *
 * Roles are additive, so the screen has to be four switches and never a single-select; and a role
 * change has to *say* it is audited, because that is the reason it is a screen rather than a
 * database seat.
 */

const NOW = Date.parse('2026-06-07T12:00:00.000Z');

function renderMembers(members: Member[], extra: Parameters<typeof createTestApi>[0] = {}) {
  const api = createTestApi({ listMembers: () => Promise.resolve(members), ...extra });
  renderWithProviders(<MembersPage />, api);
  return api;
}

describe('Settings → Members (§7.1)', () => {
  it('shows every role as its own switch, because roles add up', async () => {
    renderMembers(makeMembers(NOW));

    expect(await screen.findByText('Ada Okafor')).toBeInTheDocument();

    const card = screen.getByText('Ada Okafor').closest('div[class*="rounded-card"]');
    const scope = within(card as HTMLElement);

    // Three held, one not: a single-select could not represent this member at all.
    expect(scope.getAllByText('Held')).toHaveLength(3);
    expect(scope.getByText('that is you')).toBeInTheDocument();
  });

  it('says in words what each role lets somebody see', async () => {
    renderMembers(makeMembers(NOW));

    expect(
      await screen.findAllByText(/Never sees a repository name, a branch or a diff/),
    ).not.toHaveLength(0);
    expect(screen.getAllByText(/Sessions, transcripts, diffs and steering/)).not.toHaveLength(0);
  });

  it('grants one role at a time, which is what the audit log records', async () => {
    const setMemberRole = vi.fn().mockResolvedValue(makeMembers(NOW)[1]);

    renderMembers(makeMembers(NOW), { setMemberRole });

    const card = (await screen.findByText('Priya Raman')).closest('div[class*="rounded-card"]');
    const scope = within(card as HTMLElement);

    await userEvent.setup().click(scope.getAllByRole('button', { name: 'Give' })[0]!);

    await waitFor(() => {
      expect(setMemberRole).toHaveBeenCalledWith('member-priya', {
        role: 'approver',
        granted: true,
      });
    });
  });

  it('shows the server sentence when removing the last administrator is refused', async () => {
    renderMembers(makeMembers(NOW), {
      setMemberRole: () =>
        Promise.reject(
          new ApiError(409, 'That is the last administrator on this instance. Make somebody else an administrator first.'),
        ),
    });

    const card = (await screen.findByText('Ada Okafor')).closest('div[class*="rounded-card"]');
    const scope = within(card as HTMLElement);

    await userEvent.setup().click(scope.getAllByRole('button', { name: 'Remove' })[0]!);

    // Read from the alert rather than by text: the page also *states* this rule in its footer, and
    // matching that would let the test pass without the refusal ever being shown.
    expect(await screen.findByRole('alert')).toHaveTextContent(/last administrator on this instance/);
  });

  it('says the page is refused rather than showing a filtered version of it', async () => {
    renderMembers([], {
      listMembers: () => Promise.reject(new ApiError(403, 'Members belong to administrators.')),
    });

    expect(await screen.findByText(/belong to administrators/)).toBeInTheDocument();
  });
});

describe('Settings → Audit log (§7.3, guardrail 5)', () => {
  function renderAudit(extra: Parameters<typeof createTestApi>[0] = {}) {
    const api = createTestApi({
      getAuditLog: () => Promise.resolve({ entries: makeAuditLog(NOW), hasMore: false }),
      ...extra,
    });

    renderWithProviders(<AuditPage />, api);
    return api;
  }

  it('leads with the sentence and keeps the dotted verb beside it', async () => {
    renderAudit();

    expect(
      await screen.findByText('Ada Okafor made tom@northbeam.example an approver.'),
    ).toBeInTheDocument();
    expect(screen.getByText('member.role.granted')).toBeInTheDocument();
  });

  it('shows an entry nobody is named for as Charter itself rather than hiding it', async () => {
    renderAudit();

    expect(
      await screen.findByText('The smoke test passed; the repository became requestable.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/Charter itself, with nobody's name on it/)).toBeInTheDocument();
  });

  it('names its own empty state rather than showing a blank table', async () => {
    renderAudit({ getAuditLog: () => Promise.resolve({ entries: [], hasMore: false }) });

    expect(await screen.findByText('Nothing recorded yet')).toBeInTheDocument();
  });
});
