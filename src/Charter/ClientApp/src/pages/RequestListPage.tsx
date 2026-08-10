import { useCallback } from 'react';
import { Link, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import type { RequestSummary } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { SetupChecklist } from '@/features/setup/SetupChecklist';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { useAsync } from '@/hooks/useAsync';
import { useNow } from '@/hooks/useNow';
import { cn } from '@/lib/cn';
import { formatRelative } from '@/lib/format';
import { requesterStatus } from '@/lib/status';

export function RequestListPage() {
  const api = useApi();
  const navigate = useNavigate();
  const load = useCallback((signal: AbortSignal) => api.listRequests(signal), [api]);
  const state = useAsync(load);
  const now = useNow(30_000);

  return (
    <>
      <PageHeader
        actions={
          <Button
            onClick={() => {
              void navigate('/requests/new');
            }}
            variant="primary"
          >
            <Icon name="plus" size={16} />
            New request
          </Button>
        }
        description="Everything you have asked for, and where each one has got to."
        title="Requests"
      />

      {/*
       * §30.2. The requests list is where an admin lands, so the setup checklist lives here rather
       * than on a dashboard route that would exist only to hold it. It renders itself away entirely
       * when the API sends no checklist — which is everyone who is not an admin.
       */}
      <SetupChecklist />

      {state.status === 'loading' ? (
        <ul className="space-y-2">
          {[0, 1, 2].map((row) => (
            <li key={row}>
              <Skeleton className="h-20 w-full" />
            </li>
          ))}
        </ul>
      ) : state.status === 'error' ? (
        <Card className="px-4 py-5">
          <p className="text-ink">Charter could not load your requests just now.</p>
          <Button className="mt-3" onClick={state.reload}>
            Try again
          </Button>
        </Card>
      ) : state.data.length === 0 ? (
        /* §30.5. The single next action, named. This is the entire product on day one. */
        <EmptyState
          action={
            <Button
              onClick={() => {
                void navigate('/requests/new');
              }}
              size="lg"
              variant="primary"
            >
              Describe what you want changed
            </Button>
          }
          description="Ask for something in your own words — a label that says the wrong thing, a field that should remember what you picked. Charter works out the details with you before anything gets built."
          icon="message"
          secondary={
            <Link className="text-accent underline underline-offset-4" to="/projects">
              See what you can file against
            </Link>
          }
          title="Nothing here yet"
        />
      ) : (
        <ul className="space-y-2">
          {state.data.map((request) => (
            <li key={request.id}>
              <RequestRow now={now} request={request} />
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function RequestRow({ request, now }: { request: RequestSummary; now: number }) {
  const status = requesterStatus(request.status);

  return (
    <Link
      className={cn(
        'border-line bg-surface hover:border-line-strong block rounded-card border px-4 py-3.5 transition-colors',
        request.needsAttention && 'border-accent-line',
      )}
      to={`/requests/${request.id}`}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <StatusPill
          pulse={status.tone === 'active'}
          tone={status.tone}
          {...(status.tone === 'good'
            ? { icon: 'check' as const }
            : status.tone === 'bad'
              ? { icon: 'alert' as const }
              : {})}
        >
          {status.label}
        </StatusPill>
        <span className="text-tiny text-ink-subtle ml-auto">
          {formatRelative(request.updatedAt, now)}
        </span>
      </div>

      <p className="text-ink mt-2 font-medium">{request.title}</p>

      <p className="text-small text-ink-muted mt-0.5 flex flex-wrap items-center gap-x-2">
        <span>{request.projectName}</span>
        {request.lastMilestoneLabel ? (
          <>
            <span aria-hidden="true" className="text-ink-subtle">
              &middot;
            </span>
            <span>{request.lastMilestoneLabel}</span>
          </>
        ) : null}
        {request.awaitingApprovalFrom ? (
          <>
            <span aria-hidden="true" className="text-ink-subtle">
              &middot;
            </span>
            <span>waiting on {request.awaitingApprovalFrom}</span>
          </>
        ) : null}
      </p>
    </Link>
  );
}
