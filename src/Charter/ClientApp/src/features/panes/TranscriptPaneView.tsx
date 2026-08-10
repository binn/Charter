import { useEffect, useRef } from 'react';
import type { TranscriptPane } from '@/api/types';
import { SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';

export interface TranscriptPaneViewProps {
  pane: TranscriptPane;
  /** Set when a milestone in pane 1 was clicked; scrolls the matching event into view (§12). */
  focusedSeq?: number;
  onSelectFile?: (path: string) => void;
}

/**
 * Pane 2 (§12): the raw event stream.
 *
 * This pane exists at all only because the API sent `transcript`, which it does only for viewers
 * with repo read access — transcripts leak file paths, environment variable names and error output
 * (§7.4). The component takes the data and draws it; it holds no opinion about who may see it.
 *
 * Virtualization and cursor pagination are still to do — a long session produces tens of thousands
 * of events and this renders all of them. Fine for the fixture sizes; not fine for a real session.
 */
export function TranscriptPaneView({ pane, focusedSeq, onSelectFile }: TranscriptPaneViewProps) {
  const focusedRef = useRef<HTMLLIElement>(null);

  useEffect(() => {
    if (focusedSeq !== undefined) {
      focusedRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
  }, [focusedSeq]);

  return (
    <div>
      <SectionLabel>Event stream</SectionLabel>
      <ol className="mt-3 space-y-0.5">
        {pane.events.map((event) => {
          const focused = event.seq === focusedSeq;
          return (
            <li
              className={cn(
                'text-small rounded-[0.375rem] px-2 py-1.5 font-mono transition-colors',
                focused ? 'bg-accent-soft text-accent-soft-ink' : 'text-ink-muted',
              )}
              key={event.seq}
              ref={focused ? focusedRef : null}
            >
              <span className="text-ink-subtle mr-2 tabular-nums">{event.seq}</span>
              <span className="text-ink">{event.type}</span>
              <span className="ml-2">{event.summary}</span>
              {event.path && onSelectFile ? (
                <button
                  className="text-accent ml-2 inline-flex items-center gap-1 underline decoration-dotted underline-offset-2"
                  onClick={() => {
                    onSelectFile(event.path as string);
                  }}
                  type="button"
                >
                  {event.path}
                  <Icon name="chevronRight" size={11} />
                </button>
              ) : null}
            </li>
          );
        })}
      </ol>
      {pane.nextCursor ? (
        <p className="text-tiny text-ink-subtle mt-3">Older events are paginated behind a cursor.</p>
      ) : null}
    </div>
  );
}
