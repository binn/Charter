import { useCallback, useMemo, useState, type ReactNode } from 'react';
import {
  EMPTY_SELECTION,
  PaneSelectionContext,
  type PaneSelection,
  type PaneSelectionValue,
} from '@/features/panes/pane-selection';

/**
 * Holds the §12 shared selection and announces every cross-pane jump.
 *
 * The live region is not decoration. The whole feature is "clicking here moves something over
 * there", and a sighted user gets that for free from the scroll. Without an announcement, a screen
 * reader user activating a milestone gets silence and no evidence anything happened.
 */
export function PaneSelectionProvider({ children }: { children: ReactNode }) {
  const [selection, setSelection] = useState<PaneSelection>(EMPTY_SELECTION);
  const [announcement, setAnnouncement] = useState('');

  const selectMilestone = useCallback<PaneSelectionValue['selectMilestone']>(
    (milestone, options) => {
      // Bound before the updater closes over it: `exactOptionalPropertyTypes` will not accept a
      // `number | undefined` in a `eventSeq?: number` slot, and the narrowing above does not
      // survive into the callback.
      const eventSeq = milestone.eventSeq;
      if (eventSeq === undefined) {
        return;
      }
      setSelection((current) => ({
        milestoneId: milestone.id,
        eventSeq,
        // A milestone selects a point in the stream, not a file. Whatever pane 3 was showing stays,
        // because closing it would throw away the reader's place for no reason.
        ...(current.filePath === undefined ? {} : { filePath: current.filePath }),
        ...(current.hunkIndex === undefined ? {} : { hunkIndex: current.hunkIndex }),
        nonce: current.nonce + 1,
        focusPane: options?.fromKeyboard ? 2 : null,
      }));
      setAnnouncement(`Detailed view moved to the events for: ${milestone.label}`);
    },
    [],
  );

  const selectEvent = useCallback<PaneSelectionValue['selectEvent']>((event, options) => {
    setSelection((current) => {
      const opensPane3 = event.path !== undefined;
      return {
        ...(current.milestoneId === undefined ? {} : { milestoneId: current.milestoneId }),
        eventSeq: event.seq,
        ...(event.path === undefined ? {} : { filePath: event.path }),
        ...(event.hunkIndex === undefined ? {} : { hunkIndex: event.hunkIndex }),
        nonce: current.nonce + 1,
        focusPane: options?.fromKeyboard && opensPane3 ? 3 : null,
      };
    });
    setAnnouncement(
      event.path === undefined
        ? `Selected event ${event.seq}`
        : `Developer view opened at ${event.path}`,
    );
  }, []);

  const selectFile = useCallback<PaneSelectionValue['selectFile']>((path, options) => {
    setSelection((current) => ({
      ...(current.milestoneId === undefined ? {} : { milestoneId: current.milestoneId }),
      ...(current.eventSeq === undefined ? {} : { eventSeq: current.eventSeq }),
      filePath: path,
      ...(options?.hunkIndex === undefined ? {} : { hunkIndex: options.hunkIndex }),
      nonce: current.nonce + 1,
      focusPane: options?.fromKeyboard ? 3 : null,
    }));
    setAnnouncement(`Showing changes to ${path}`);
  }, []);

  const value = useMemo<PaneSelectionValue>(
    () => ({ selection, selectMilestone, selectEvent, selectFile }),
    [selection, selectMilestone, selectEvent, selectFile],
  );

  return (
    <PaneSelectionContext value={value}>
      {children}
      <span aria-live="polite" className="sr-only">
        {announcement}
      </span>
    </PaneSelectionContext>
  );
}
