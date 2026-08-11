import { useCallback, useState } from 'react';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { Member, Role } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { FormAlert } from '@/features/auth/AuthPage';
import { SettingsNav } from '@/pages/SettingsPage';
import { useAsync } from '@/hooks/useAsync';
import { formatDateTime } from '@/lib/format';

/**
 * §7.1's administrator column: **members and roles**.
 *
 * Roles are additive — "a member may hold several" — so this is four independent switches per
 * person and never a single-select. Each one names what that role actually lets somebody see, in the
 * spec's own terms, because "make Priya an engineer" is a decision about who reads transcripts and
 * diffs (§7.4) and it should not be made from the word alone.
 *
 * Every change here writes `member.role.granted` or `member.role.revoked` to the audit log. That is
 * the whole reason this screen exists rather than a database seat: privilege escalation is the one
 * thing an audit log must never miss, and the verbs had no writer at all before it.
 */

const ROLES: { role: Role; label: string; sees: string }[] = [
  {
    role: 'requester',
    label: 'Requester',
    sees: 'Files requests and watches them. Never sees a repository name, a branch or a diff.',
  },
  {
    role: 'approver',
    label: 'Approver',
    sees: 'Gates spend on refined specs. Not code quality — that is branch protection, not Charter.',
  },
  {
    role: 'engineer',
    label: 'Engineer',
    sees: 'Sessions, transcripts, diffs and steering. Configures repositories and scopes.',
  },
  {
    role: 'admin',
    label: 'Administrator',
    sees: 'Members, roles, budgets, repository connections, model selection and this audit log.',
  },
];

export function MembersPage() {
  const api = useApi();
  const load = useCallback((signal: AbortSignal) => api.listMembers(signal), [api]);
  const state = useAsync(load);

  const [busy, setBusy] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const toggle = (member: Member, role: Role, granted: boolean) => {
    const key = `${member.id}:${role}`;
    setBusy(key);
    setFailure(null);

    api
      .setMemberRole(member.id, { role, granted })
      .then(() => {
        state.reload();
      })
      .catch((error: unknown) => {
        setFailure(
          error instanceof ApiError
            ? error.message
            : 'Charter could not reach the server just now. Try again in a moment.',
        );
      })
      .finally(() => {
        setBusy(null);
      });
  };

  return (
    <>
      <PageHeader
        description="Who is in this organisation, and what each of them can see. Roles add up — somebody can be both an approver and an engineer."
        title="Members"
      />

      <SettingsNav />

      <div className="mt-6 max-w-3xl space-y-4">
        {failure ? <FormAlert>{failure}</FormAlert> : null}

        {state.status === 'loading' ? (
          <Skeleton className="h-48 w-full" />
        ) : state.status === 'error' ? (
          <Card className="px-4 py-5">
            <p className="text-ink">
              Charter could not load the members. Members and roles belong to administrators — if you
              are not one, this page stays empty rather than showing you a filtered version of it.
            </p>
            <Button className="mt-3" onClick={state.reload}>
              Try again
            </Button>
          </Card>
        ) : state.data.length === 0 ? (
          <EmptyState
            description="An organisation with one member is a personal instance, and that is a perfectly good way to run Charter. Invite somebody when you want a second pair of eyes on what the agent proposes."
            icon="user"
            title="Nobody else here yet"
          />
        ) : (
          <ul className="space-y-3">
            {state.data.map((member) => (
              <li key={member.id}>
                <Card className="px-4 py-4 sm:px-5">
                  <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                    <div>
                      <p className="text-ink font-medium">
                        {member.displayName}
                        {member.isYou ? (
                          <span className="text-tiny text-ink-subtle ml-2">that is you</span>
                        ) : null}
                      </p>
                      <p className="text-small text-ink-muted">{member.email}</p>
                    </div>
                    <p className="text-tiny text-ink-subtle">
                      Joined {formatDateTime(member.joinedAt)}
                    </p>
                  </div>

                  <ul className="mt-3 space-y-1">
                    {ROLES.map((entry) => {
                      const held = member.roles.includes(entry.role);
                      const key = `${member.id}:${entry.role}`;

                      return (
                        <li
                          className="border-line rounded-control flex items-start gap-3 border px-3 py-2.5"
                          key={entry.role}
                        >
                          <span className="min-w-0 flex-1">
                            <span className="text-ink text-small flex items-center gap-2 font-medium">
                              {entry.label}
                              {held ? <StatusPill tone="good">Held</StatusPill> : null}
                            </span>
                            <span className="text-tiny text-ink-muted block">{entry.sees}</span>
                          </span>
                          <Button
                            disabled={busy !== null}
                            onClick={() => {
                              toggle(member, entry.role, !held);
                            }}
                            size="sm"
                            variant={held ? 'secondary' : 'primary'}
                          >
                            {busy === key ? 'Saving…' : held ? 'Remove' : 'Give'}
                          </Button>
                        </li>
                      );
                    })}
                  </ul>

                  {member.canCreateRepo ? (
                    <p className="text-tiny text-ink-subtle mt-3">
                      Also allowed to create new repositories — a capability rather than a role
                      (§26.10), granted deliberately.
                    </p>
                  ) : null}
                </Card>
              </li>
            ))}
          </ul>
        )}

        <p className="text-small text-ink-muted">
          Every change on this page is written to the audit log, naming you. Two things Charter will
          refuse: leaving somebody with no role at all, and removing the last administrator on this
          instance.
        </p>
      </div>
    </>
  );
}
