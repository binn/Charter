import type { AuditEntry, Member, RepoAccessGrant } from '@/api/types';

/**
 * §7.1's administrator column: members, roles, repo access and the audit log.
 *
 * The fixture is built so the three things that are easy to get wrong are visible without a backend:
 *
 * - **Repo access is deny by default** (§7.3). One repository has grants and one has none, so the
 *   screen has to render "requestable by nobody" as a real state rather than an empty table.
 * - **Roles are additive** (§7.1). One member holds three, which is what stops the screen being
 *   built as a single-select.
 * - **Privilege escalation is audited**. The seeded log contains role grants, scope grants and the
 *   moment onboarding made a repository requestable — the three questions an admin arrives with.
 */

export const MOCK_MEMBER_IDS = {
  ada: 'member-ada',
  priya: 'member-priya',
  tom: 'member-tom',
} as const;

export function makeMembers(now: number): Member[] {
  return [
    {
      id: MOCK_MEMBER_IDS.ada,
      displayName: 'Ada Okafor',
      email: 'ada@northbeam.example',
      roles: ['admin', 'engineer', 'approver'],
      canCreateRepo: true,
      joinedAt: new Date(now - 120 * 24 * 60 * 60_000).toISOString(),
      isYou: true,
    },
    {
      id: MOCK_MEMBER_IDS.priya,
      displayName: 'Priya Raman',
      email: 'priya@northbeam.example',
      roles: ['requester'],
      canCreateRepo: false,
      joinedAt: new Date(now - 40 * 24 * 60 * 60_000).toISOString(),
      isYou: false,
    },
    {
      id: MOCK_MEMBER_IDS.tom,
      displayName: 'Tom Iwu',
      email: 'tom@northbeam.example',
      roles: ['requester', 'approver'],
      canCreateRepo: false,
      joinedAt: new Date(now - 12 * 24 * 60 * 60_000).toISOString(),
      isYou: false,
    },
  ];
}

/** The grants an onboarded repository has. Everything else is refused by the absence of a row. */
export function makeAccessGrants(): RepoAccessGrant[] {
  return [
    {
      memberId: MOCK_MEMBER_IDS.ada,
      memberName: 'Ada Okafor',
      memberEmail: 'ada@northbeam.example',
      canRequest: true,
    },
    { role: 'requester', canRequest: true },
  ];
}

export function makeAuditLog(now: number): AuditEntry[] {
  const at = (minutes: number) => new Date(now - minutes * 60_000).toISOString();

  return [
    {
      id: 'audit-6',
      at: at(35),
      action: 'repo.scope.granted',
      summary: 'Ada Okafor let somebody file requests against a repository.',
      actorName: 'Ada Okafor',
      actorEmail: 'ada@northbeam.example',
      targetType: 'repo',
      targetId: 'repo-quote-tool',
      details: { role: 'requester' },
    },
    {
      id: 'audit-5',
      at: at(180),
      action: 'member.role.granted',
      summary: 'Ada Okafor made tom@northbeam.example an approver.',
      actorName: 'Ada Okafor',
      actorEmail: 'ada@northbeam.example',
      targetType: 'Member',
      targetId: MOCK_MEMBER_IDS.tom,
      details: { role: 'approver', member_email: 'tom@northbeam.example' },
    },
    {
      id: 'audit-4',
      at: at(2 * 60 * 24),
      action: 'repo.ready',
      summary: 'The smoke test passed; the repository became requestable.',
      targetType: 'Repo',
      targetId: 'repo-quote-tool',
      details: { pull_request: '118', preview_bound: 'True' },
    },
    {
      id: 'audit-3',
      at: at(2 * 60 * 24 + 40),
      action: 'repo.scope.confirmed',
      summary: 'Ada Okafor confirmed the scope and queued the smoke test.',
      actorName: 'Ada Okafor',
      actorEmail: 'ada@northbeam.example',
      targetType: 'Repo',
      targetId: 'repo-quote-tool',
    },
    {
      id: 'audit-2',
      at: at(3 * 60 * 24),
      action: 'member.invited',
      summary: 'Ada Okafor invited priya@northbeam.example to the organisation.',
      actorName: 'Ada Okafor',
      actorEmail: 'ada@northbeam.example',
      targetType: 'Invitation',
      details: { email: 'priya@northbeam.example' },
    },
    {
      id: 'audit-1',
      at: at(9 * 60 * 24),
      action: 'setup.completed',
      summary: 'Ada Okafor claimed this instance and became its first administrator.',
      actorName: 'Ada Okafor',
      actorEmail: 'ada@northbeam.example',
      targetType: 'Organization',
    },
  ];
}
