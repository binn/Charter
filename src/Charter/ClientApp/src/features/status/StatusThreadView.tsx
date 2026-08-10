import type { Milestone, MilestoneKind, StatusThread } from '@/api/types';
import { Icon, type IconName } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { cn } from '@/lib/cn';
import { elapsedSince, formatDuration, formatRelative } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

const KIND_ICONS: Record<MilestoneKind, IconName> = {
  understanding: 'search',
  changing: 'wrench',
  checking: 'check',
  assembling: 'package',
  status: 'spark',
  question: 'message',
  outcome: 'arrowRight',
};

export interface StatusThreadViewProps {
  thread: StatusThread;
  /**
   * §12: clicking a milestone scrolls pane 2 to the events that produced it. Passed only when pane
   * 2 exists — which is to say, only when the API sent a transcript at all.
   */
  onSelectMilestone?: (milestone: Milestone) => void;
  selectedMilestoneId?: string;
}

/**
 * §11, pane 1. **One thread per request, forever** — several sessions, a rebuild, a "not quite"
 * and its follow-up all land in this one list, so nobody has to work out which of three cards is
 * live.
 *
 * What it shows are *translated milestones*, never the raw transcript: roughly four promoted event
 * types, already turned into plain language by the server. And it always shows something moving
 * while a session is live, because §11 is blunt about the alternative — "a 5–20 minute silent gap
 * reads as broken".
 *
 * The elapsed timer counts up from `startedAt`. There is no estimate here and no progress bar,
 * because §6 forbids an ETA: "one blown estimate costs more trust than ten honest slow ones".
 */
export function StatusThreadView({
  thread,
  onSelectMilestone,
  selectedMilestoneId,
}: StatusThreadViewProps) {
  const now = useNow(thread.live ? 1_000 : 60_000);

  return (
    <section aria-label="Progress">
      {thread.startedAt ? (
        <p className="text-small text-ink-muted mb-4 flex flex-wrap items-center gap-x-2 gap-y-1">
          {thread.live ? (
            <>
              <span aria-hidden="true" className="bg-accent animate-pulse-soft size-2 rounded-full" />
              <span className="text-ink font-medium">Working on it</span>
              <span className="text-ink-subtle">
                &middot; {formatDuration(elapsedSince(thread.startedAt, now))} so far
              </span>
            </>
          ) : thread.endedAt ? (
            <span className="text-ink-subtle">
              Took {formatDuration(Date.parse(thread.endedAt) - Date.parse(thread.startedAt))}
            </span>
          ) : null}
        </p>
      ) : null}

      <ol className="relative space-y-1">
        {thread.milestones.map((milestone, index) => {
          const isLast = index === thread.milestones.length - 1;
          const interactive = onSelectMilestone !== undefined && milestone.eventSeq !== undefined;

          const content = (
            <>
              <span className="relative flex flex-col items-center self-stretch">
                <span
                  className={cn(
                    'z-10 grid size-7 shrink-0 place-items-center rounded-full border',
                    milestone.state === 'failed'
                      ? 'border-danger-line bg-danger-soft text-danger'
                      : milestone.state === 'active'
                        ? 'border-accent-line bg-accent-soft text-accent-soft-ink'
                        : 'border-line bg-surface text-ink-subtle',
                  )}
                >
                  <Icon
                    className={milestone.state === 'active' ? 'animate-pulse-soft' : undefined}
                    name={
                      milestone.state === 'failed' ? 'alert' : KIND_ICONS[milestone.kind]
                    }
                    size={14}
                  />
                </span>
                {!isLast ? (
                  <span aria-hidden="true" className="bg-line -mt-1 w-px grow" />
                ) : null}
              </span>

              <span className="min-w-0 flex-1 pb-4">
                <span className="flex flex-wrap items-baseline gap-x-2">
                  <span
                    className={cn(
                      'font-medium',
                      milestone.state === 'failed' ? 'text-danger' : 'text-ink',
                    )}
                  >
                    {milestone.label}
                  </span>
                  <span className="text-tiny text-ink-subtle">
                    {formatRelative(milestone.occurredAt, now)}
                  </span>
                </span>

                {milestone.detail ? (
                  <span className="text-small text-ink-muted mt-0.5 block">{milestone.detail}</span>
                ) : null}

                {/* §13: one-sentence teaching annotation, grounded in what actually happened. */}
                {milestone.annotationMd ? (
                  <span className="border-accent-line bg-accent-soft/50 text-small text-accent-soft-ink mt-2 flex gap-2 rounded-control border px-3 py-2">
                    <Icon className="mt-0.5 shrink-0" name="spark" size={13} />
                    <Markdown>{milestone.annotationMd}</Markdown>
                  </span>
                ) : null}
              </span>
            </>
          );

          return (
            <li key={milestone.id}>
              {interactive ? (
                <button
                  aria-current={selectedMilestoneId === milestone.id}
                  className={cn(
                    'hover:bg-sunken flex w-full gap-3 rounded-control px-2 -mx-2 text-left transition-colors',
                    selectedMilestoneId === milestone.id && 'bg-sunken',
                  )}
                  onClick={() => {
                    onSelectMilestone(milestone);
                  }}
                  type="button"
                >
                  {content}
                </button>
              ) : (
                <div className="flex gap-3 px-2 -mx-2">{content}</div>
              )}
            </li>
          );
        })}
      </ol>

      {/* §11: failure has dignity. Budget exhausted, agent stuck and checks failing all arrive
          here as the same sentence. The real detail is in the engineer view. */}
      {thread.failureSummary ? (
        <div className="border-line bg-sunken rounded-card mt-2 border px-4 py-4">
          <p className="text-ink flex items-start gap-2.5">
            <Icon className="text-ink-subtle mt-0.5" name="alert" size={17} />
            <span className="text-small">{thread.failureSummary}</span>
          </p>
        </div>
      ) : null}
    </section>
  );
}
