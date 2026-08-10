import { useCallback } from 'react';
import { Link } from 'react-router';
import { useApi } from '@/api/api-context';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { useAsync } from '@/hooks/useAsync';
import { useNow } from '@/hooks/useNow';
import { formatRelative } from '@/lib/format';

/**
 * The spend gate (§7.5), and only the spend gate.
 *
 * "Is this worth burning tokens and quota on?" — never "is this code fit to ship". The merge gate
 * lives in branch protection and CODEOWNERS, outside Charter entirely, and is not represented in
 * the data model, so there is nothing on this page that could be mistaken for one.
 *
 * The empty state matters more than the list: §7.5 expects small teams to run with auto-dispatch
 * and budgets doing the governing, so "nothing waiting" is the normal, healthy state here, not a
 * sign that something is broken.
 */
export function ApprovalsPage() {
  const api = useApi();
  const load = useCallback((signal: AbortSignal) => api.listPendingApprovals(signal), [api]);
  const state = useAsync(load);
  const now = useNow(30_000);

  return (
    <>
      <PageHeader
        description="Requests waiting on someone to say they are worth building. This is about cost, not code."
        title="Approvals"
      />

      {state.status === 'loading' ? (
        <Skeleton className="h-32 w-full" />
      ) : state.status === 'error' ? (
        <Card className="px-4 py-5">
          <p className="text-ink">Charter could not load the approval queue just now.</p>
          <Button className="mt-3" onClick={state.reload}>
            Try again
          </Button>
        </Card>
      ) : state.data.length === 0 ? (
        <EmptyState
          description="Nothing is waiting on you. If your team has auto-dispatch on, most requests will never appear here — budgets do the governing instead."
          icon="check"
          secondary={
            <Link className="text-accent underline underline-offset-4" to="/requests">
              See all requests
            </Link>
          }
          title="Nothing waiting"
        />
      ) : (
        <ul className="space-y-3">
          {state.data.map((approval) => (
            <li key={approval.specId}>
              <Card className="px-4 py-4 sm:px-5">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <h2 className="text-ink font-medium">{approval.title}</h2>
                  <span className="text-small text-ink-muted font-mono">
                    ~${approval.estimatedCostUsd.toFixed(2)}
                  </span>
                </div>
                <p className="text-small text-ink-muted mt-1">{approval.outcome}</p>
                <p className="text-tiny text-ink-subtle mt-2">
                  {approval.requesterName} &middot; {approval.projectName} &middot;{' '}
                  {formatRelative(approval.submittedAt, now)}
                </p>
                <div className="mt-3">
                  <Link
                    className="text-small text-accent underline underline-offset-4"
                    to={`/requests/${approval.requestId}`}
                  >
                    Read the full plan
                  </Link>
                </div>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
