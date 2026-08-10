import { createContext, use } from 'react';
import type { Id, Milestone, TranscriptEvent } from '@/api/types';

/**
 * The shared selection behind §12's linked panes.
 *
 * > "Panes must be linked or it's three apps in a trenchcoat. Clicking a milestone in pane 1
 * > scrolls pane 2 to the events that produced it. Clicking a file-write event in pane 2 opens
 * > pane 3 at that hunk. **Selection is shared state.**"
 *
 * So it lives here rather than inside any one pane. Pane 1 is composed by the page — the requester's
 * pane 1 is the whole product — which means the linkage cannot be props threaded down from
 * `ThreePaneView`; it has to be context that all three panes and the page can reach.
 *
 * Two details that are easy to get wrong:
 *
 * - **`nonce` rather than value equality.** Panes react to the nonce, so clicking the same
 *   milestone twice scrolls back to it. Watching `eventSeq` alone would make the second click do
 *   nothing, which reads as a broken control.
 * - **Focus moves only when the keyboard asked for it.** Yanking focus into pane 2 on a mouse click
 *   would strand a mouse user's arrow keys in a list they did not choose to enter; leaving it put
 *   for a keyboard user would make the link unusable, because they would have to tab through pane 1
 *   to reach what they just selected. `origin` carries that intent, set from `event.detail === 0`,
 *   which is how a browser reports Enter or Space on a button.
 */

export interface PaneSelection {
  /** Pane 1 → pane 2. */
  milestoneId?: Id;
  /** The event a milestone was promoted from, or the event selected directly in pane 2. */
  eventSeq?: number;
  /** Pane 2 → pane 3. */
  filePath?: string;
  hunkIndex?: number;
  /** Incremented on every deliberate navigation, including a repeat of the same target. */
  nonce: number;
  /** The pane that should take focus next, or `null` when the pointer drove the change. */
  focusPane: 2 | 3 | null;
}

export interface PaneSelectionValue {
  selection: PaneSelection;
  /** Pane 1: a milestone was activated. Scrolls pane 2 to the events that produced it. */
  selectMilestone: (milestone: Milestone, options?: { fromKeyboard?: boolean }) => void;
  /** Pane 2: an event was activated. Opens pane 3 at its hunk when it is a file write. */
  selectEvent: (event: TranscriptEvent, options?: { fromKeyboard?: boolean }) => void;
  /** Pane 3: a file was picked from the changed-file list. */
  selectFile: (path: string, options?: { hunkIndex?: number; fromKeyboard?: boolean }) => void;
}

export const EMPTY_SELECTION: PaneSelection = { nonce: 0, focusPane: null };

/**
 * Stable DOM ids for panes 2 and 3.
 *
 * Constants rather than `useId`, because pane 1's milestone buttons need to point `aria-controls` at
 * pane 2 and pane 1 is composed by the page, outside `ThreePaneView`. A generated id would have to
 * be threaded through the very context this file exists to avoid threading things through. One
 * three-pane view exists per page, so a fixed id cannot collide.
 */
export const PANE_2_ID = 'charter-pane-detailed';
export const PANE_3_ID = 'charter-pane-developer';

export const PaneSelectionContext = createContext<PaneSelectionValue | null>(null);

/**
 * Returns `null` outside a provider rather than throwing.
 *
 * A requester's page has no linked panes at all, so pane 1 renders with no provider above it. That
 * is the normal case, not a mistake worth crashing over — `StatusThreadView` simply renders its
 * milestones as text instead of buttons.
 */
export function usePaneSelection(): PaneSelectionValue | null {
  return use(PaneSelectionContext);
}
