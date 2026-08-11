import type { SmokeTestCheckpoint, SmokeTestOutcome } from '@/api/types';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon, type IconName } from '@/components/ui/Icon';
import { StatusPill, type Tone } from '@/components/ui/StatusPill';
import { cn } from '@/lib/cn';

/**
 * §9 step 4, and the reason the whole wizard exists: "Charter files a canned trivial request and
 * runs the entire loop: agent runs → checks pass → PR opens → preview deploys → URL binds back.
 * Nothing else validates all six integration points at once."
 *
 * So this shows the run **happening**. An engineer decides whether to trust this tool by watching it
 * work once (§30.3); a green tick that appears after two silent minutes proves the same fact and
 * persuades nobody. Each checkpoint carries what it actually did, so a failure points at the
 * integration that broke rather than at "smoke test failed".
 *
 * There is no progress bar and no estimate — §6 forbids an ETA, and a step is `running` because the
 * one before it finished, not because a timer says so.
 */

/** The six integration points, in the order the loop exercises them. */
const FALLBACK: { id: SmokeTestCheckpoint['id']; label: string }[] = [
  { id: 'request_filed', label: 'Filed a trivial request' },
  { id: 'agent_ran', label: 'The agent ran' },
  { id: 'checks_passed', label: 'Your checks passed' },
  { id: 'pull_request', label: 'A pull request opened' },
  { id: 'preview_deployed', label: 'A preview deployed' },
  { id: 'url_bound', label: 'The preview URL bound back' },
];

const STATE_ICON: Record<SmokeTestCheckpoint['state'], IconName> = {
  pending: 'clock',
  running: 'hourglass',
  passed: 'check',
  failed: 'cross',
  skipped: 'clock',
};

const STATE_TONE: Record<SmokeTestCheckpoint['state'], string> = {
  pending: 'border-line text-ink-subtle',
  running: 'border-accent-line bg-accent-soft text-accent-soft-ink',
  passed: 'border-ok-line bg-ok-soft text-ok',
  failed: 'border-danger-line bg-danger-soft text-danger',
  skipped: 'border-line text-ink-subtle',
};

/**
 * What the client can prove from an outcome that carries no checkpoints — an older control plane, or
 * a run recorded before this shape existed. It marks what it cannot know as unknown rather than
 * inventing ticks.
 */
function reconstruct(outcome: SmokeTestOutcome): SmokeTestCheckpoint[] {
  const proven: Record<string, boolean> = {
    request_filed: true,
    agent_ran: outcome.passed || outcome.pullRequestNumber !== undefined,
    checks_passed: outcome.passed,
    pull_request: outcome.pullRequestNumber !== undefined,
    preview_deployed: outcome.previewBound,
    url_bound: outcome.previewBound,
  };

  return FALLBACK.map((step) => ({
    id: step.id,
    label: step.label,
    state: proven[step.id] === true ? 'passed' : outcome.passed ? 'skipped' : 'failed',
  }));
}

export function SmokeTestRun({
  outcome,
  running,
}: {
  /** Null before the first run: confirming the scope is what queues it. */
  outcome: SmokeTestOutcome | null;
  /** True while the repository is in the smoke-test state and the page is polling. */
  running: boolean;
}) {
  const checkpoints = outcome?.checkpoints ?? (outcome ? reconstruct(outcome) : []);
  const failed = outcome !== null && !outcome.passed && !running;

  const pill: { tone: Tone; icon: IconName; label: string } = failed
    ? { tone: 'bad', icon: 'alert', label: 'Did not finish' }
    : outcome?.passed === true
      ? { tone: 'good', icon: 'check', label: 'All six passed' }
      : running
        ? { tone: 'active', icon: 'hourglass', label: 'Running now' }
        : { tone: 'neutral', icon: 'clock', label: 'Not started' };

  return (
    <Card className="px-4 py-5 sm:px-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <SectionLabel>The smoke test</SectionLabel>
        <StatusPill icon={pill.icon} tone={pill.tone}>
          {pill.label}
        </StatusPill>
      </div>

      <p className="text-small text-ink-muted mt-1.5">
        Charter files one trivial request and runs the whole loop against this repository. It is the
        only thing that exercises all six integration points at once, and it is what makes the
        repository visible to requesters — until it passes, nobody can file against it.
      </p>

      {checkpoints.length === 0 ? (
        <p className="text-small text-ink-muted mt-4">
          Nothing has run yet. Confirming the scope above queues it.
        </p>
      ) : (
        <ol
          aria-live="polite"
          className="mt-4 space-y-1.5"
          // Announced as it advances: someone watching this with a screen reader should hear the
          // run progress, which is the entire point of showing it live.
        >
          {checkpoints.map((checkpoint) => (
            <li
              className={cn(
                'rounded-control flex items-start gap-3 border px-3 py-2.5',
                STATE_TONE[checkpoint.state],
              )}
              key={checkpoint.id}
            >
              <Icon
                className={cn('mt-0.5', checkpoint.state === 'running' && 'animate-pulse-soft')}
                name={STATE_ICON[checkpoint.state]}
                size={15}
              />
              <span className="min-w-0 flex-1">
                <span className="text-ink block font-medium text-small">{checkpoint.label}</span>
                {checkpoint.detail ? (
                  <span className="text-tiny text-ink-muted block">{checkpoint.detail}</span>
                ) : null}
              </span>
              {/* Never colour alone (§27.7): the state is a word as well as a hue and a glyph. */}
              <span className="text-tiny shrink-0 capitalize">
                {checkpoint.state === 'running' ? 'running' : checkpoint.state}
              </span>
            </li>
          ))}
        </ol>
      )}

      {outcome?.warnings?.map((warning) => (
        <p className="text-small text-warn mt-3 flex items-start gap-2" key={warning}>
          <Icon className="mt-0.5" name="alert" size={15} />
          <span>{warning}</span>
        </p>
      ))}

      {failed ? (
        <p className="text-small text-ink-muted mt-3">
          Fix whatever the failing step names and confirm the scope again — that re-queues the run.
          Nothing about this repository is visible to requesters in the meantime.
        </p>
      ) : null}
    </Card>
  );
}
