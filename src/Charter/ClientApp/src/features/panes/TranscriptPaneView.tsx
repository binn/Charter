import { useVirtualizer } from '@tanstack/react-virtual';
import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import type { Id, TranscriptEvent, TranscriptEventKind, TranscriptPane } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { SectionLabel } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon, type IconName } from '@/components/ui/Icon';
import { usePaneSelection } from '@/features/panes/pane-selection';
import { useTranscript } from '@/features/panes/useTranscript';
import { cn } from '@/lib/cn';

const KIND_ICONS: Record<TranscriptEventKind, IconName> = {
  tool_use: 'wrench',
  file_write: 'file',
  command: 'terminal',
  message: 'message',
  diagnostic: 'alert',
  lifecycle: 'spark',
};

/** Fixed row height. Uniform rows are what let 12,000 events scroll without measuring any of them. */
const ROW_HEIGHT = 26;

export interface TranscriptPaneViewProps {
  requestId: Id;
  pane: TranscriptPane;
  /** The id the pane's heading carries, so pane 1's milestones can point `aria-controls` at it. */
  paneId: string;
}

/**
 * Pane 2 (§12): the raw event stream, virtualized and cursor-paginated.
 *
 * This pane exists at all only because the API sent `transcript`, which it does only for viewers
 * with repo read access — transcripts leak file paths, environment variable names and error output
 * (§7.4). The component takes the data and draws it; it holds no opinion about who may see it, and
 * there is no role check anywhere below.
 *
 * **It is a listbox, not a log.** That is the accessibility consequence of §12's linkage: the events
 * are selectable things that drive another pane, so they need selection semantics, arrow-key
 * movement and a single tab stop. Virtualization rules out roving `tabindex` — the element you
 * wanted to focus may not be in the DOM — so the active option is tracked with
 * `aria-activedescendant`, which is exactly the case that attribute exists for.
 *
 * Activation is manual: arrowing through events moves the highlight but does not open pane 3.
 * Enter does. Automatic activation would fire a diff fetch per keystroke.
 */
export function TranscriptPaneView({ requestId, pane, paneId }: TranscriptPaneViewProps) {
  const linkage = usePaneSelection();
  const transcript = useTranscript(requestId, pane);
  const { events, jumpTo } = transcript;

  const scrollRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const baseId = useId();
  const [activeSeq, setActiveSeq] = useState<number | undefined>(undefined);

  /*
   * `useVirtualizer` returns functions the React Compiler cannot safely memoise, so it emits a
   * "Compilation Skipped" warning for this component and leaves it unmemoised. That is the correct
   * outcome and not something to silence: the virtualizer's whole job is to return a *different*
   * window on every scroll, and a memoised one would show a stale list.
   */
  const virtualizer = useVirtualizer({
    count: events.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 14,
  });

  const nonce = linkage?.selection.nonce ?? 0;
  const targetSeq = linkage?.selection.eventSeq;
  const focusPane = linkage?.selection.focusPane ?? null;
  const activeMilestoneId = linkage?.selection.milestoneId;

  /*
   * The pane-1 → pane-2 half of the linkage.
   *
   * Two passes, deliberately. If the target is not in the loaded window this asks the hook for one
   * centred on it and stops; `events` then changes identity, the effect runs again, and the second
   * pass finds the row and scrolls to it. Trying to do both in one pass means either awaiting inside
   * an effect or scrolling to an index that does not exist yet.
   */
  useEffect(() => {
    if (targetSeq === undefined) {
      return;
    }
    const index = events.findIndex((event) => event.seq === targetSeq);
    if (index === -1) {
      jumpTo(targetSeq);
      return;
    }
    setActiveSeq(targetSeq);
    virtualizer.scrollToIndex(index, { align: 'center' });
  }, [nonce, targetSeq, events, jumpTo, virtualizer]);

  useEffect(() => {
    if (focusPane === 2) {
      listRef.current?.focus();
    }
  }, [nonce, focusPane]);

  const moveActive = useCallback(
    (delta: number | 'start' | 'end') => {
      if (events.length === 0) {
        return;
      }
      const from = events.findIndex((event) => event.seq === activeSeq);
      const current = from === -1 ? events.length - 1 : from;
      const next =
        delta === 'start'
          ? 0
          : delta === 'end'
            ? events.length - 1
            : Math.min(events.length - 1, Math.max(0, current + delta));
      const target = events[next];
      if (target) {
        setActiveSeq(target.seq);
        virtualizer.scrollToIndex(next, { align: 'auto' });
      }
    },
    [events, activeSeq, virtualizer],
  );

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        moveActive(1);
        break;
      case 'ArrowUp':
        event.preventDefault();
        moveActive(-1);
        break;
      case 'PageDown':
        event.preventDefault();
        moveActive(20);
        break;
      case 'PageUp':
        event.preventDefault();
        moveActive(-20);
        break;
      case 'Home':
        event.preventDefault();
        moveActive('start');
        break;
      case 'End':
        event.preventDefault();
        moveActive('end');
        break;
      case 'Enter':
      case ' ': {
        const selected = events.find((candidate) => candidate.seq === activeSeq);
        if (selected) {
          event.preventDefault();
          linkage?.selectEvent(selected, { fromKeyboard: true });
        }
        break;
      }
      default:
        break;
    }
  };

  const headingId = `${paneId}-heading`;

  if (transcript.totalCount === 0) {
    return (
      <section aria-labelledby={headingId} className="flex h-full flex-col">
        <SectionLabel id={headingId}>Detailed</SectionLabel>
        <EmptyState
          className="mt-4 border-0"
          description="Nothing has run yet. Once this request is dispatched, every tool call the agent makes appears here as it happens."
          icon="list"
          title="No events yet"
        />
      </section>
    );
  }

  return (
    <section aria-labelledby={headingId} className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <SectionLabel id={headingId}>Detailed</SectionLabel>
        <p className="text-tiny text-ink-subtle tabular-nums">
          {events.length.toLocaleString()} of {transcript.totalCount.toLocaleString()} events
        </p>
      </div>

      <p className="text-tiny text-ink-subtle mt-1">
        Everything the agent did, as it reported it. Enter opens a file change in Developer.
      </p>

      {transcript.nextCursor === null ? null : (
        <div className="mt-2">
          <Button
            disabled={transcript.loadingOlder}
            onClick={transcript.loadOlder}
            size="sm"
            variant="secondary"
          >
            <Icon name="chevronDown" size={13} className="rotate-180" />
            {transcript.loadingOlder ? 'Loading…' : 'Load earlier events'}
          </Button>
        </div>
      )}

      {transcript.error ? (
        <p className="text-small text-danger mt-2 flex items-center gap-1.5">
          <Icon name="alert" size={14} />
          {transcript.error.message}
        </p>
      ) : null}

      <div
        className="border-line bg-sunken rounded-control mt-3 min-h-0 flex-1 overflow-auto border"
        ref={scrollRef}
      >
        <div
          aria-activedescendant={activeSeq === undefined ? undefined : `${baseId}-${activeSeq}`}
          aria-label="Agent event stream"
          className="relative w-full focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-focus"
          onKeyDown={onKeyDown}
          ref={listRef}
          role="listbox"
          style={{ height: `${virtualizer.getTotalSize()}px` }}
          tabIndex={0}
        >
          {virtualizer.getVirtualItems().map((row) => {
            const event = events[row.index];
            if (!event) {
              return null;
            }
            return (
              <TranscriptRow
                event={event}
                id={`${baseId}-${event.seq}`}
                isActive={event.seq === activeSeq}
                isInSelectedMilestone={
                  activeMilestoneId !== undefined && event.milestoneId === activeMilestoneId
                }
                key={event.seq}
                offset={row.start}
                onActivate={(fromKeyboard) => {
                  setActiveSeq(event.seq);
                  linkage?.selectEvent(event, { fromKeyboard });
                }}
              />
            );
          })}
        </div>
      </div>

      {transcript.jumping ? (
        <p className="text-tiny text-ink-subtle mt-2" role="status">
          Fetching that part of the log…
        </p>
      ) : null}
    </section>
  );
}

interface TranscriptRowProps {
  event: TranscriptEvent;
  id: string;
  isActive: boolean;
  /** Marks the whole run of events a selected pane-1 milestone produced, not just its first. */
  isInSelectedMilestone: boolean;
  offset: number;
  onActivate: (fromKeyboard: boolean) => void;
}

function TranscriptRow({
  event,
  id,
  isActive,
  isInSelectedMilestone,
  offset,
  onActivate,
}: TranscriptRowProps) {
  const opensPane3 = event.path !== undefined;

  return (
    <div
      aria-selected={isActive}
      className={cn(
        'text-tiny absolute top-0 left-0 flex w-full items-center gap-2 px-2 font-mono',
        'border-l-2 transition-colors',
        isInSelectedMilestone ? 'border-l-accent bg-accent-soft/40' : 'border-l-transparent',
        isActive && 'bg-accent-soft text-accent-soft-ink',
        opensPane3 && 'cursor-pointer',
      )}
      id={id}
      onClick={(domEvent) => {
        // `detail === 0` is how a browser reports Enter or Space on an element rather than a
        // pointer press. It is the difference between "move my focus, I am on the keyboard" and
        // "leave my focus alone, I clicked".
        onActivate(domEvent.detail === 0);
      }}
      role="option"
      style={{ height: `${ROW_HEIGHT}px`, transform: `translateY(${offset}px)` }}
    >
      <span className="text-ink-subtle w-14 shrink-0 text-right tabular-nums">{event.seq}</span>

      <Icon
        className={cn(
          'shrink-0',
          event.level === 'error'
            ? 'text-danger'
            : event.level === 'warning'
              ? 'text-warn'
              : 'text-ink-subtle',
        )}
        name={event.level === 'error' || event.level === 'warning' ? 'alert' : KIND_ICONS[event.kind]}
        size={12}
      />

      {/* Never colour alone (§27.7, generalised): the level gets a word as well as a hue. */}
      {event.level === 'error' || event.level === 'warning' ? (
        <span
          className={cn(
            'text-micro shrink-0 font-semibold uppercase',
            event.level === 'error' ? 'text-danger' : 'text-warn',
          )}
        >
          {event.level}
        </span>
      ) : null}

      <span className="text-ink-subtle shrink-0">{event.type}</span>
      <span className="text-ink min-w-0 flex-1 truncate">{event.summary}</span>

      {opensPane3 ? (
        <span className="text-accent flex shrink-0 items-center gap-1">
          <Icon name="diff" size={11} />
          <span className="text-micro">open</span>
        </span>
      ) : null}
    </div>
  );
}
