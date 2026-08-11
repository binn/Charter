import { useCallback } from 'react';
import { useApi } from '@/api/api-context';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { SettingsNav } from '@/pages/SettingsPage';
import { useAsync } from '@/hooks/useAsync';
import { formatDateTime } from '@/lib/format';

/**
 * §7.3, guardrail 5: **every agent action attributable to a named human.**
 *
 * The log was being written from day one and read by nobody, which makes it a compliance ornament
 * rather than a guardrail. The questions it exists to answer are ordinary and specific — who made
 * this repository requestable, who made them an administrator, what happened at 03:00 — so entries
 * lead with a sentence rather than a dotted verb, and carry the verb beside it for whoever is
 * grepping the container logs for the same fact.
 *
 * An entry with **no actor** renders as Charter itself and is not hidden. §7.3 says the agent never
 * acts on its own initiative; the few entries with nobody's name on them are exactly the ones an
 * operator should be able to find.
 */
export function AuditPage() {
  const api = useApi();
  const load = useCallback((signal: AbortSignal) => api.getAuditLog(signal), [api]);
  const state = useAsync(load);

  return (
    <>
      <PageHeader
        description="What has happened on this instance, and who did it. Append-only — nothing here can be edited or removed from inside Charter."
        title="Audit log"
      />

      <SettingsNav />

      <div className="mt-6 max-w-3xl space-y-4">
        {state.status === 'loading' ? (
          <Skeleton className="h-64 w-full" />
        ) : state.status === 'error' ? (
          <Card className="px-4 py-5">
            <p className="text-ink">
              Charter could not load the audit log. It belongs to administrators — if you are not
              one, the server refuses it outright rather than handing over a filtered copy.
            </p>
            <Button className="mt-3" onClick={state.reload}>
              Try again
            </Button>
          </Card>
        ) : state.data.entries.length === 0 ? (
          <EmptyState
            description="Nothing has happened yet. Connecting a repository, inviting somebody or changing a role all land here, naming whoever did it."
            icon="list"
            title="Nothing recorded yet"
          />
        ) : (
          <>
            <ol className="space-y-2">
              {state.data.entries.map((entry) => (
                <li className="border-line bg-surface rounded-card border px-4 py-3" key={entry.id}>
                  <p className="text-ink text-small">{entry.summary}</p>
                  <div className="text-tiny text-ink-subtle mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
                    <span>{formatDateTime(entry.at)}</span>
                    <code className="font-mono">{entry.action}</code>
                    {entry.actorEmail === undefined ? (
                      <span className="flex items-center gap-1">
                        <Icon name="spark" size={12} />
                        Charter itself, with nobody's name on it
                      </span>
                    ) : (
                      <span>{entry.actorEmail}</span>
                    )}
                  </div>
                  {entry.details ? (
                    <dl className="text-tiny text-ink-subtle mt-1.5 flex flex-wrap gap-x-4 gap-y-0.5">
                      {Object.entries(entry.details).map(([key, value]) => (
                        <div className="flex gap-1.5" key={key}>
                          <dt className="font-mono">{key}</dt>
                          <dd className="text-ink-muted font-mono">{value}</dd>
                        </div>
                      ))}
                    </dl>
                  ) : null}
                </li>
              ))}
            </ol>

            {state.data.hasMore ? (
              <p className="text-small text-ink-muted">
                Older entries exist beyond this page. Nothing is ever deleted — the log is
                append-only, and reading further back is a database query for now.
              </p>
            ) : null}
          </>
        )}
      </div>
    </>
  );
}
