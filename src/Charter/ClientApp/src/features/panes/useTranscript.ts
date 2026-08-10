import { useCallback, useMemo, useState } from 'react';
import { useApi } from '@/api/api-context';
import type { Id, TranscriptEvent, TranscriptPane } from '@/api/types';

export interface TranscriptState {
  /** The window currently on screen, ascending by `seq`. Never the whole session. */
  events: TranscriptEvent[];
  totalCount: number;
  /** Non-null while there are older events to page back to. */
  nextCursor: string | null;
  loadingOlder: boolean;
  /** True while `jumpTo` is fetching a window that is not loaded. */
  jumping: boolean;
  error: Error | null;
  loadOlder: () => void;
  /**
   * Make `seq` available to render. A no-op when it is already loaded; otherwise fetches a window
   * centred on it.
   */
  jumpTo: (seq: number) => void;
}

/** What the hook is holding on top of the page the request payload already carried. */
type Window =
  | { kind: 'extended'; events: TranscriptEvent[]; nextCursor: string | null; totalCount: number }
  | { kind: 'jumped'; events: TranscriptEvent[]; nextCursor: string | null; totalCount: number };

/**
 * Pane 2's data: cursor-paginated, paging **backwards** from the live tail.
 *
 * Backwards, because the interesting end of a session log is the end. The detail payload already
 * carries the tail, so the pane paints immediately and only fetches when someone scrolls up or
 * follows a link from pane 1.
 *
 * `jumpTo` is what makes §12's linkage survive contact with a real session. A milestone can point at
 * event 12 of 12,480; paging backwards twenty-five times to reach it is not a feature. When the
 * target is outside the loaded window the hook asks the server for one centred on it and *replaces*
 * what it holds, rather than stitching two disjoint ranges into a list with an invisible gap in the
 * middle.
 *
 * The view is **derived, never synchronised**. `initial` changes as the live session appends to the
 * tail, and the obvious implementation — an effect copying it into state — is both a cascading
 * render and a source of drift. Instead the hook stores only what it has added on top, and computes
 * the rest. A window the user deliberately jumped to stays put; one they merely scrolled back
 * through still picks up new events at the end.
 */
export function useTranscript(requestId: Id, initial: TranscriptPane): TranscriptState {
  const api = useApi();

  const [window, setWindow] = useState<Window | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [jumping, setJumping] = useState(false);
  const [error, setError] = useState<Error | null>(null);

  // Navigating to another request must not show the previous session's stream for a frame. This is
  // React's documented "adjust state when a prop changes" pattern: set during render, not in an
  // effect, so the stale window never reaches the screen.
  const [loadedRequest, setLoadedRequest] = useState(requestId);
  if (loadedRequest !== requestId) {
    setLoadedRequest(requestId);
    setWindow(null);
    setError(null);
  }

  const view = useMemo<TranscriptPane>(() => {
    if (window === null) {
      return initial;
    }
    if (window.kind === 'jumped') {
      return window;
    }
    // Scrolled back, but still anchored to the tail — so fold in anything the session has emitted
    // since. `seq` is the identity, which makes this idempotent however often the payload refreshes.
    const highest = window.events[window.events.length - 1]?.seq ?? 0;
    const fresh = initial.events.filter((event) => event.seq > highest);
    return fresh.length === 0
      ? window
      : {
          events: [...window.events, ...fresh],
          nextCursor: window.nextCursor,
          totalCount: Math.max(window.totalCount, initial.totalCount),
        };
  }, [window, initial]);

  const loadOlder = useCallback(() => {
    if (view.nextCursor === null || loadingOlder) {
      return;
    }
    const cursor = view.nextCursor;
    setLoadingOlder(true);
    api
      .getTranscript(requestId, { cursor })
      .then((page) => {
        setWindow((current) => {
          const base = current ?? { kind: 'extended' as const, ...initial };
          const lowest = base.events[0]?.seq ?? Number.POSITIVE_INFINITY;
          return {
            kind: base.kind,
            events: [...page.events.filter((event) => event.seq < lowest), ...base.events],
            nextCursor: page.nextCursor,
            totalCount: page.totalCount,
          };
        });
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause : new Error('Could not load older events'));
      })
      .finally(() => {
        setLoadingOlder(false);
      });
  }, [api, requestId, view.nextCursor, loadingOlder, initial]);

  const jumpTo = useCallback(
    (seq: number) => {
      if (view.events.some((event) => event.seq === seq)) {
        return;
      }
      setJumping(true);
      api
        .getTranscript(requestId, { aroundSeq: seq })
        .then((page) => {
          setWindow({ kind: 'jumped', ...page });
        })
        .catch((cause: unknown) => {
          setError(
            cause instanceof Error ? cause : new Error('Could not load that part of the log'),
          );
        })
        .finally(() => {
          setJumping(false);
        });
    },
    [api, requestId, view.events],
  );

  return {
    events: view.events,
    totalCount: view.totalCount,
    nextCursor: view.nextCursor,
    loadingOlder,
    jumping,
    error,
    loadOlder,
    jumpTo,
  };
}
