import { useCallback, useState } from 'react';
import { Link } from 'react-router';
import { useApi } from '@/api/api-context';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { AgentCard } from '@/features/runners/AgentCard';
import { RegisterAgentPanel } from '@/features/runners/RegisterAgentPanel';
import { SettingsNav } from '@/pages/SettingsPage';
import { useAsync } from '@/hooks/useAsync';
import { useNow } from '@/hooks/useNow';
import { cn } from '@/lib/cn';

/**
 * Settings → Runners (§33.3).
 *
 * Two jobs on one page, and the second is the one that earns it. Registering an agent is a
 * five-minute task done once. Working out why a session is sitting in a queue is a thing engineers
 * do repeatedly and currently by guessing, so the waiting list is not a footnote here: picking a
 * waiting session re-renders every agent's capability set **in that session's terms**, turning a
 * wall of version strings into a per-requirement answer.
 *
 * The verdict shown is always the server's (`eligibleAgentIds`). The per-requirement breakdown
 * beside it is the reasoning, not a second opinion.
 */
export function RunnersPage() {
  const api = useApi();
  const now = useNow(15_000);
  const load = useCallback((signal: AbortSignal) => api.listRunners(signal), [api]);
  const state = useAsync(load);
  const [selectedRequestId, setSelectedRequestId] = useState<string | null>(null);

  const onRevoke = useCallback(
    async (agentId: string) => {
      await api.revokeAgent(agentId);
      state.reload();
    },
    [api, state],
  );

  return (
    <>
      <PageHeader
        description="Machines you control that Charter can send work to. They dial out to Charter; Charter never connects to them."
        title="Runners"
      />

      <SettingsNav />

      {state.status === 'loading' ? (
        <div className="mt-6 space-y-3">
          <Skeleton className="h-40 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : state.status === 'error' ? (
        <Card className="mt-6 px-4 py-5">
          <p className="text-ink">Charter could not load your runners just now.</p>
          <Button className="mt-3" onClick={state.reload}>
            Try again
          </Button>
        </Card>
      ) : (
        <div className="mt-6 space-y-4">
          <RegisterAgentPanel onRegistered={state.reload} />

          {state.data.waiting.length > 0 ? (
            <Card className="px-4 py-5 sm:px-5">
              <SectionLabel>Waiting for a runner</SectionLabel>
              <p className="text-small text-ink-muted mt-1.5">
                These sessions are queued, not failed. Pick one to see which agents can take it and
                which cannot.
              </p>
              <ul className="mt-3 space-y-2">
                {state.data.waiting.map((demand) => {
                  const selected = demand.requestId === selectedRequestId;
                  const stuck = demand.eligibleAgentIds.length === 0;
                  return (
                    <li key={demand.requestId}>
                      <button
                        aria-pressed={selected}
                        className={cn(
                          'w-full rounded-control border px-3.5 py-3 text-left transition-colors',
                          selected
                            ? 'border-accent-line bg-accent-soft'
                            : 'border-line hover:border-line-strong',
                        )}
                        onClick={() => {
                          setSelectedRequestId(selected ? null : demand.requestId);
                        }}
                        type="button"
                      >
                        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
                          <Icon
                            className={stuck ? 'text-danger' : 'text-ink-subtle'}
                            name={stuck ? 'alert' : 'hourglass'}
                            size={14}
                          />
                          <span className="text-ink font-medium">{demand.title}</span>
                          <span className="text-tiny text-ink-subtle ml-auto">
                            {stuck
                              ? 'nothing can run this'
                              : `${demand.eligibleAgentIds.length} agent${
                                  demand.eligibleAgentIds.length === 1 ? '' : 's'
                                } can run it`}
                          </span>
                        </span>
                        <span className="text-tiny text-ink-muted mt-1 block font-mono">
                          needs {demand.requires.join(', ')}
                        </span>
                        {demand.queuedReason ? (
                          <span className="text-small text-ink-muted mt-1.5 block">
                            {demand.queuedReason}
                          </span>
                        ) : null}
                      </button>
                    </li>
                  );
                })}
              </ul>
            </Card>
          ) : null}

          {state.data.agents.length === 0 ? (
            /* §30.5. One next action, named — and the reason to bother, in one line. */
            <EmptyState
              description="Charter can run work on GitHub Actions without one. Register an agent when a project needs macOS, a signing identity, a licensed toolchain, or a device plugged into a machine you own."
              icon="server"
              secondary={
                <Link className="text-accent underline underline-offset-4" to="/settings">
                  Back to settings
                </Link>
              }
              title="No agents registered"
            />
          ) : (
            <ul className="space-y-3">
              {state.data.agents.map((agent) => {
                const selected = state.data.waiting.find(
                  (demand) => demand.requestId === selectedRequestId,
                );
                return (
                  <li key={agent.id}>
                    <AgentCard
                      agent={agent}
                      now={now}
                      onRevoke={onRevoke}
                      {...(selected
                        ? {
                            requirements: selected.requires,
                            eligible: selected.eligibleAgentIds.includes(agent.id),
                          }
                        : {})}
                    />
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
    </>
  );
}
