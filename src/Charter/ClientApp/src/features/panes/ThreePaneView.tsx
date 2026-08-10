import { useState, type ReactNode } from 'react';
import type { PanePreference, RequestDetail } from '@/api/types';
import { useViewer } from '@/app/viewer-context';
import { Card } from '@/components/ui/Card';
import { SegmentedControl, type Segment } from '@/components/ui/SegmentedControl';
import { ChangesPaneView } from '@/features/panes/ChangesPaneView';
import { TranscriptPaneView } from '@/features/panes/TranscriptPaneView';
import { useIsDesktop } from '@/hooks/useMediaQuery';

export interface ThreePaneViewProps {
  request: RequestDetail;
  /** Pane 1. Composed by the page, because the requester's pane 1 is the whole product. */
  children: ReactNode;
  /** Set when a milestone is clicked, so pane 2 can scroll to the events that produced it. */
  focusedEventSeq?: number;
}

/**
 * §12, progressive disclosure. Named for the user: **Simple / Detailed / Developer**.
 *
 * Two rules from that section shape this component:
 *
 * - **"Pane 2 and 3 availability is a permission, not a preference."** So the available modes are
 *   derived from whether `request.transcript` and `request.changes` are present in the payload —
 *   the API omits them for anyone without repo read (§7.4). A requester does not get a disabled
 *   Developer tab; they get no tab strip at all, because for them there is one pane and choosing
 *   between one thing is not a choice.
 * - **"Panes must be linked or it's three apps in a trenchcoat."** Selection is shared state:
 *   clicking a milestone scrolls pane 2, clicking a file-write event opens pane 3 at that file.
 *
 * The mode is a user preference held server-side, so it follows the person between devices.
 * Mobile shows exactly one pane at a time, as §12 requires.
 */
export function ThreePaneView({ request, children, focusedEventSeq }: ThreePaneViewProps) {
  const { viewer, updatePreferences } = useViewer();
  const isDesktop = useIsDesktop();
  const [selectedPath, setSelectedPath] = useState<string | undefined>(undefined);

  const hasTranscript = request.transcript !== undefined;
  const hasChanges = request.changes !== undefined;

  if (!hasTranscript && !hasChanges) {
    return <>{children}</>;
  }

  const segments: Segment<PanePreference>[] = [
    { value: 'simple', label: 'Simple', hint: 'Plain-English progress' },
    ...(hasTranscript
      ? [{ value: 'detailed' as const, label: 'Detailed', hint: 'Everything the agent did' }]
      : []),
    ...(hasChanges
      ? [{ value: 'developer' as const, label: 'Developer', hint: 'Files the agent changed' }]
      : []),
  ];

  const requested = viewer.preferences.pane;
  const mode = segments.some((segment) => segment.value === requested) ? requested : 'simple';

  const onModeChange = (next: PanePreference) => {
    void updatePreferences({ pane: next });
  };

  // Desktop stacks the panes side by side and keeps pane 1 visible, which is the only reason §12
  // puts them side by side at all. Mobile shows exactly one.
  const showPane1 = isDesktop || mode === 'simple';
  const showPane2 = hasTranscript && (isDesktop ? mode !== 'simple' : mode === 'detailed');
  const showPane3 = hasChanges && mode === 'developer';

  return (
    <div className="space-y-4">
      <SegmentedControl
        label="How much detail to show"
        onChange={onModeChange}
        segments={segments}
        value={mode}
      />

      <div
        className={
          mode === 'simple'
            ? ''
            : mode === 'detailed'
              ? 'lg:grid lg:grid-cols-2 lg:gap-4'
              : 'lg:grid lg:grid-cols-3 lg:gap-4'
        }
      >
        {showPane1 ? <div className="min-w-0">{children}</div> : null}

        {showPane2 && request.transcript ? (
          <Card className="min-w-0 overflow-x-auto p-4">
            <TranscriptPaneView
              onSelectFile={setSelectedPath}
              pane={request.transcript}
              {...(focusedEventSeq === undefined ? {} : { focusedSeq: focusedEventSeq })}
            />
          </Card>
        ) : null}

        {showPane3 && request.changes ? (
          <Card className="min-w-0 overflow-x-auto p-4">
            <ChangesPaneView
              onSelectFile={setSelectedPath}
              pane={request.changes}
              {...(selectedPath === undefined ? {} : { selectedPath })}
            />
          </Card>
        ) : null}
      </div>
    </div>
  );
}
