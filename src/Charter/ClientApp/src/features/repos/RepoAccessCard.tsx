import { useCallback, useState } from 'react';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { Member, Role } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { FormAlert } from '@/features/auth/AuthPage';
import { useAsync } from '@/hooks/useAsync';

/**
 * §7.3, guardrail 1: **who may file against this repository, deny by default.**
 *
 * The endpoints existed with nothing calling them, which is how every onboarded repository ended up
 * requestable by nobody with no way to see why. Three things this screen has to get right:
 *
 * - **The absence of a row is the refusal.** There is no "denied" row for everybody who has no
 *   grant, because that would be a list of the whole organisation and would read as a policy rather
 *   than as the default. So the empty state says the default out loud instead.
 * - **Readiness and access are independent.** A repository can have grants and still be invisible
 *   because its smoke test has not passed (§9), and an admin who has just granted access needs to be
 *   told that rather than left wondering.
 * - **A withholding row beats a granting one.** Turning a person off does not delete their grant, it
 *   writes a refusal — which is what the server does, and what makes "why can this person not file?"
 *   answerable.
 */

const ROLES: { role: Role; label: string; hint: string }[] = [
  { role: 'requester', label: 'Requesters', hint: 'Everybody whose job here is asking for changes' },
  { role: 'approver', label: 'Approvers', hint: 'The people who gate spend' },
  { role: 'engineer', label: 'Engineers', hint: 'The people who read the diffs' },
  { role: 'admin', label: 'Administrators', hint: 'Members, roles, budgets, connections' },
];

export function RepoAccessCard({ repoId, repoName }: { repoId: string; repoName: string }) {
  const api = useApi();
  const loadAccess = useCallback((signal: AbortSignal) => api.getRepoAccess(repoId, signal), [api, repoId]);
  const access = useAsync(loadAccess);

  // Administrators get a person picker; an engineer configuring a repository is refused
  // `GET /api/members` and gets the role grants only. The refusal is the server's, and the screen
  // renders what it was given rather than what the viewer's role suggests it might have been.
  const loadMembers = useCallback(
    (signal: AbortSignal) => api.listMembers(signal).catch((): Member[] => []),
    [api],
  );
  const members = useAsync(loadMembers);

  const [busy, setBusy] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const set = (key: string, body: { memberId?: string; role?: Role; canRequest: boolean }) => {
    setBusy(key);
    setFailure(null);

    api
      .setRepoAccess(repoId, body)
      .then(() => {
        access.reload();
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

  if (access.status === 'loading') {
    return <Skeleton className="h-40 w-full" />;
  }

  if (access.status === 'error') {
    return (
      <Card className="px-4 py-5 sm:px-5">
        <SectionLabel>Who can file against this</SectionLabel>
        <p className="text-small text-ink-muted mt-1.5">
          Charter could not load the access list. Granting access is an administrator job — if you
          are not one, this is what that refusal looks like.
        </p>
      </Card>
    );
  }

  const grants = access.data.grants;
  const people = grants.filter((grant) => grant.memberId !== undefined);
  const byRole = new Map(grants.filter((grant) => grant.role !== undefined).map((grant) => [grant.role, grant]));
  const anyone = grants.some((grant) => grant.canRequest);
  const roster = members.status === 'ready' ? members.data : [];
  const ungranted = roster.filter((member) => !people.some((grant) => grant.memberId === member.id));

  return (
    <Card className="px-4 py-5 sm:px-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <SectionLabel>Who can file against this</SectionLabel>
        <StatusPill
          icon={anyone ? 'check' : 'key'}
          tone={anyone ? 'good' : 'neutral'}
        >
          {anyone ? 'Some people can' : 'Nobody can'}
        </StatusPill>
      </div>

      <p className="text-small text-ink-muted mt-1.5">
        A newly connected repository is requestable by nobody, and stays that way until somebody is
        named here. Grants are to a person or to a whole role; withholding beats granting, so turning
        one person off overrides the role grant that would otherwise cover them.
      </p>

      {!access.data.requesterVisible ? (
        <p className="text-small text-warn mt-3 flex items-start gap-2">
          <Icon className="mt-0.5" name="alert" size={15} />
          <span>
            Nobody sees {repoName} yet whatever this list says — its smoke test has not passed, and
            that is what makes a repository visible to requesters (§9).
          </span>
        </p>
      ) : null}

      {failure ? (
        <div className="mt-3">
          <FormAlert>{failure}</FormAlert>
        </div>
      ) : null}

      <h3 className="text-small text-ink mt-5 font-medium">By role</h3>
      <ul className="mt-2 space-y-1">
        {ROLES.map((entry) => {
          const grant = byRole.get(entry.role);
          const on = grant?.canRequest === true;
          const key = `role:${entry.role}`;

          return (
            <li
              className="border-line rounded-control flex items-start gap-3 border px-3 py-2.5"
              key={entry.role}
            >
              <span className="min-w-0 flex-1">
                <span className="text-ink text-small block font-medium">{entry.label}</span>
                <span className="text-tiny text-ink-muted block">{entry.hint}</span>
              </span>
              <Button
                disabled={busy !== null}
                onClick={() => {
                  set(key, { role: entry.role, canRequest: !on });
                }}
                size="sm"
                variant={on ? 'secondary' : 'primary'}
              >
                {busy === key ? 'Saving…' : on ? 'Withhold' : 'Grant'}
              </Button>
            </li>
          );
        })}
      </ul>

      <h3 className="text-small text-ink mt-5 font-medium">By person</h3>

      {people.length === 0 ? (
        <p className="text-small text-ink-muted mt-2">
          Nobody is named individually. That is not an omission — deny by default means the absence
          of a row <em>is</em> the refusal.
        </p>
      ) : (
        <ul className="mt-2 space-y-1">
          {people.map((grant) => (
            <li
              className="border-line rounded-control flex items-start gap-3 border px-3 py-2.5"
              key={grant.memberId}
            >
              <span className="min-w-0 flex-1">
                <span className="text-ink text-small block font-medium">
                  {grant.memberName ?? 'A member'}
                </span>
                {grant.memberEmail ? (
                  <span className="text-tiny text-ink-muted block">{grant.memberEmail}</span>
                ) : null}
              </span>
              <span className="text-tiny text-ink-subtle shrink-0 self-center">
                {grant.canRequest ? 'Can file' : 'Withheld'}
              </span>
              <Button
                disabled={busy !== null}
                onClick={() => {
                  set(`member:${grant.memberId ?? ''}`, {
                    memberId: grant.memberId ?? '',
                    canRequest: !grant.canRequest,
                  });
                }}
                size="sm"
                variant={grant.canRequest ? 'secondary' : 'primary'}
              >
                {busy === `member:${grant.memberId ?? ''}`
                  ? 'Saving…'
                  : grant.canRequest
                    ? 'Withhold'
                    : 'Grant'}
              </Button>
            </li>
          ))}
        </ul>
      )}

      {ungranted.length > 0 ? (
        <div className="mt-4">
          <h3 className="text-small text-ink font-medium">Add somebody</h3>
          <ul className="mt-2 flex flex-wrap gap-2">
            {ungranted.map((member) => (
              <li key={member.id}>
                <Button
                  disabled={busy !== null}
                  onClick={() => {
                    set(`member:${member.id}`, { memberId: member.id, canRequest: true });
                  }}
                  size="sm"
                >
                  <Icon name="plus" size={13} />
                  {member.displayName}
                </Button>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </Card>
  );
}
