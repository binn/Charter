import type { AgentStatus, RunnerAgent } from '@/api/types';
import { Card, SectionLabel } from '@/components/ui/Card';
import { ConfirmDestructive } from '@/components/ui/ConfirmDestructive';
import { Icon, type IconName } from '@/components/ui/Icon';
import { StatusPill, type Tone } from '@/components/ui/StatusPill';
import { CapabilitySet } from '@/features/runners/CapabilitySet';
import { cn } from '@/lib/cn';
import { formatRelative } from '@/lib/format';

/** Pass/fail never by colour alone (§27.7): every status carries a glyph and a word. */
const STATUS: Record<AgentStatus, { tone: Tone; icon: IconName; label: string }> = {
  online: { tone: 'good', icon: 'check', label: 'online' },
  offline: { tone: 'bad', icon: 'cross', label: 'offline' },
  draining: { tone: 'warn', icon: 'hourglass', label: 'draining' },
  revoked: { tone: 'neutral', icon: 'cross', label: 'revoked' },
};

export interface AgentCardProps {
  agent: RunnerAgent;
  now: number;
  /** Set when a waiting session is selected, so the capability set answers that session's question. */
  requirements?: string[];
  /** The server's verdict for the selected session. `undefined` when none is selected. */
  eligible?: boolean;
  onRevoke: (agentId: string) => Promise<void>;
}

export function AgentCard({ agent, now, requirements, eligible, onRevoke }: AgentCardProps) {
  const status = STATUS[agent.status];
  const busy = agent.concurrency.inFlight > 0;

  return (
    <Card
      className={cn(
        'px-4 py-4 sm:px-5',
        eligible === true && 'border-ok-line',
        eligible === false && 'opacity-70',
      )}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h3 className="text-ink font-medium">{agent.name}</h3>
        <StatusPill icon={status.icon} tone={status.tone}>
          {status.label}
        </StatusPill>

        {/* §33.2: `native` is not a lesser mode, it is the only option for macOS and USB devices —
            but it is weaker isolation, and the docs say so, so the label does too. */}
        <StatusPill icon={agent.mode === 'docker' ? 'package' : 'server'} tone="neutral">
          {agent.mode === 'docker' ? 'docker' : 'native'}
        </StatusPill>

        <span className="text-tiny text-ink-subtle ml-auto font-mono">
          agent {agent.version} · {agent.os} · {agent.arch}
        </span>
      </div>

      {/* §33.6: a protocol mismatch refuses work outright. Saying so here is the difference between
          a clear message now and three sessions that mysteriously never start. */}
      {!agent.protocolCompatible ? (
        <p className="border-warn-line bg-warn-soft rounded-control text-small text-ink mt-3 flex items-start gap-2 border px-3 py-2">
          <Icon className="text-warn mt-0.5 shrink-0" name="alert" size={14} />
          <span>
            <span className="font-medium">Not claiming work.</span>{' '}
            {agent.protocolNote ??
              'Its protocol version does not match this Charter. Upgrade the agent.'}
          </span>
        </p>
      ) : null}

      <dl className="text-tiny mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        <dt className="text-ink-subtle">Jobs</dt>
        <dd className="text-ink">
          {agent.concurrency.inFlight} running of {agent.concurrency.limit} allowed
        </dd>
        <dt className="text-ink-subtle">Heartbeat</dt>
        <dd className="text-ink">
          {agent.lastHeartbeatAt === undefined
            ? 'never'
            : formatRelative(agent.lastHeartbeatAt, now)}
          {agent.status === 'offline' ? (
            <span className="text-ink-muted">
              {' '}
              — its jobs were re-queued once the lease timed out
            </span>
          ) : null}
        </dd>
        <dt className="text-ink-subtle">Registered</dt>
        <dd className="text-ink">{formatRelative(agent.registeredAt, now)}</dd>
      </dl>

      <div className="mt-4">
        <SectionLabel className="mb-2">
          {requirements === undefined ? 'Probed capabilities' : 'Against this session'}
        </SectionLabel>
        <CapabilitySet
          capabilities={agent.capabilities}
          {...(requirements === undefined ? {} : { requirements })}
        />
      </div>

      {eligible !== undefined ? (
        <p
          className={cn(
            'text-small mt-3 flex items-center gap-1.5 font-medium',
            eligible ? 'text-ok' : 'text-ink-muted',
          )}
        >
          <Icon name={eligible ? 'check' : 'cross'} size={14} />
          {eligible
            ? 'Charter will let this agent claim that session.'
            : 'Charter will not offer that session to this agent.'}
        </p>
      ) : null}

      <div className="border-line mt-4 border-t pt-3">
        <ConfirmDestructive
          confirmLabel="Revoke this agent now"
          confirmPhrase={agent.name}
          consequences={[
            busy ? (
              <>
                <span className="font-medium">
                  {agent.concurrency.inFlight === 1
                    ? 'The job running on it right now is killed.'
                    : `The ${agent.concurrency.inFlight} jobs running on it right now are killed.`}
                </span>{' '}
                Their work is lost and their cost is settled where it stands.
              </>
            ) : (
              'Any job it picks up between now and confirming is killed.'
            ),
            'Its credential is invalidated immediately. It cannot reconnect.',
            'Sessions that could only run here will queue until another agent can take them.',
            'To bring it back you generate a new pairing token and register it again.',
          ]}
          onConfirm={() => onRevoke(agent.id)}
          phraseLabel="agent name"
          title={`Revoke ${agent.name}`}
          triggerIcon="stop"
          triggerLabel="Revoke"
        />
      </div>
    </Card>
  );
}
