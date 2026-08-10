import type { ReactNode } from 'react';
import type { PanePreference, RequestDetail } from '@/api/types';
import { useViewer } from '@/app/viewer-context';
import { Card } from '@/components/ui/Card';
import { SegmentedControl, type Segment } from '@/components/ui/SegmentedControl';
import { DiffPaneView } from '@/features/panes/DiffPaneView';
import { PaneSelectionProvider } from '@/features/panes/PaneSelectionProvider';
import { PANE_2_ID, PANE_3_ID, usePaneSelection } from '@/features/panes/pane-selection';
import { TranscriptPaneView } from '@/features/panes/TranscriptPaneView';
import { useIsDesktop } from '@/hooks/useMediaQuery';
import { cn } from '@/lib/cn';

export interface ThreePaneViewProps {
  request: RequestDetail;
  /** Pane 1. Composed by the page, because the requester's pane 1 is the whole product. */
  children: ReactNode;
}

/**
 * §12, progressive disclosure. Named for the user: **Simple / Detailed / Developer**.
 *
 * Three rules from that section shape this component.
 *
 * **"Pane 2 and 3 availability is a permission, not a preference."** The available modes are derived
 * from whether `request.transcript` and `request.changes` are present in the payload — the API omits
 * them for anyone without repo read (§7.4). There is no role check here, and there is nothing to
 * hide, because there is nothing to hide *with*: the data is not in the response. A requester does
 * not get a disabled Developer tab, they get no tab strip at all, because choosing between one thing
 * is not a choice.
 *
 * **"Panes must be linked or it's three apps in a trenchcoat."** Selection is shared state, held by
 * `PaneSelectionProvider` so that pane 1 — composed by the page above this component — can take part
 * in it. That linkage is the entire reason these sit side by side rather than in tabs, and it is
 * what makes the view teach: you watch "Making the changes" line up with a run of tool calls, and
 * one of those tool calls line up with a hunk.
 *
 * **Defaults by role, then persisted per user.** The default arrives already decided — the server
 * seeds `preferences.pane` from the viewer's roles (requester → Simple, engineer → Developer) — and
 * every change writes back through `PATCH /api/me/preferences`. Nothing is kept in the browser.
 */
export function ThreePaneView({ request, children }: ThreePaneViewProps) {
  const { viewer, updatePreferences } = useViewer();
  const isDesktop = useIsDesktop();

  const transcript = request.transcript;
  const changes = request.changes;

  // Absence *is* the permission check (§7.4). Not `viewer.capabilities.canReadRepos`, which would
  // be the client re-deriving an answer the server already gave by omission.
  const hasTranscript = transcript !== undefined;
  const hasChanges = changes !== undefined;

  if (!hasTranscript && !hasChanges) {
    return <>{children}</>;
  }

  const segments: Segment<PanePreference>[] = [
    { value: 'simple', label: 'Simple', hint: 'Plain-English progress' },
    ...(hasTranscript
      ? [{ value: 'detailed' as const, label: 'Detailed', hint: 'Everything the agent did' }]
      : []),
    ...(hasChanges
      ? [{ value: 'developer' as const, label: 'Developer', hint: 'The code that changed' }]
      : []),
  ];

  /*
   * Resolve the stored preference against what this request actually offers.
   *
   * The preference means "how much detail I want", so when the exact mode is unavailable the right
   * answer is the most detailed one that *is* — not Simple. An engineer who has chosen Developer and
   * opens a session that produced a transcript but no file changes wants Detailed; dropping them to
   * Simple would look like the preference had been forgotten.
   */
  const ORDER: PanePreference[] = ['simple', 'detailed', 'developer'];
  const requested = viewer.preferences.pane;
  const available = ORDER.filter((value) => segments.some((segment) => segment.value === value));
  const ceiling = ORDER.indexOf(requested);
  const mode =
    available.filter((value) => ORDER.indexOf(value) <= ceiling).pop() ?? available[0] ?? 'simple';

  const onModeChange = (next: PanePreference) => {
    void updatePreferences({ pane: next });
  };

  /*
   * Desktop keeps every pane up to and including the chosen one, because §12's linkage needs at
   * least two visible to mean anything. Mobile shows exactly one, as §12 requires — there is no
   * useful two-pane layout on a phone, and a linked jump there is a pane swap instead of a scroll.
   */
  const showPane1 = isDesktop || mode === 'simple';
  const showPane2 = hasTranscript && (isDesktop ? mode !== 'simple' : mode === 'detailed');
  const showPane3 = hasChanges && mode === 'developer';

  const columns =
    mode === 'simple'
      ? ''
      : mode === 'detailed'
        ? 'lg:grid lg:grid-cols-[minmax(0,7fr)_minmax(0,6fr)] lg:gap-4 lg:items-start'
        : 'lg:grid lg:grid-cols-[minmax(0,5fr)_minmax(0,5fr)_minmax(0,7fr)] lg:gap-4 lg:items-start';

  // Panes 2 and 3 scroll internally against the viewport rather than growing the page. A pane that
  // grows cannot be "scrolled to", and scrolling to something is the whole feature.
  const paneFrame = 'min-w-0 p-3 lg:h-[calc(100dvh-11rem)] lg:sticky lg:top-20';

  return (
    <PaneSelectionProvider>
      <div className="space-y-4">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <SegmentedControl
            label="How much detail to show"
            onChange={onModeChange}
            segments={segments}
            value={mode}
          />
          {mode !== 'simple' && isDesktop ? <LinkageHint /> : null}
        </div>

        <div className={columns}>
          {showPane1 ? (
            <div className={cn('min-w-0', mode !== 'simple' && 'lg:max-h-[calc(100dvh-11rem)] lg:overflow-y-auto lg:pr-1')}>
              {children}
            </div>
          ) : null}

          {showPane2 && transcript ? (
            <Card className={paneFrame} id={PANE_2_ID}>
              <TranscriptPaneView pane={transcript} paneId={PANE_2_ID} requestId={request.id} />
            </Card>
          ) : null}

          {showPane3 && changes ? (
            <Card className={paneFrame} id={PANE_3_ID}>
              <DiffPaneView pane={changes} paneId={PANE_3_ID} requestId={request.id} />
            </Card>
          ) : null}
        </div>
      </div>
    </PaneSelectionProvider>
  );
}

/**
 * Says out loud what the linkage does, once, the first time someone opens a multi-pane mode.
 *
 * §12 wants these modes to teach. A user who never discovers that milestones are clickable never
 * gets the lesson, and a feature nobody finds is a feature nobody has. It disappears as soon as
 * they use it, because at that point they know.
 */
function LinkageHint() {
  const linkage = usePaneSelection();

  // Derived, not remembered: the nonce only ever increases, so "they have used it" is simply
  // "the nonce has moved". State plus an effect here would be a cascading render for no gain.
  if ((linkage?.selection.nonce ?? 0) > 0) {
    return null;
  }

  return (
    <p className="text-tiny text-ink-subtle">
      These are linked — click a step on the left to jump to what produced it.
    </p>
  );
}
