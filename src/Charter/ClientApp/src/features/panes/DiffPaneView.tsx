import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react';
import type { ChangedFile, ChangesPane, FileDiff, Id } from '@/api/types';
import { useApi } from '@/api/api-context';
import { useViewer } from '@/app/viewer-context';
import { SectionLabel } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { usePaneSelection } from '@/features/panes/pane-selection';
import { useMediaQuery } from '@/hooks/useMediaQuery';
import { cn } from '@/lib/cn';

/**
 * The route split §3 and §12 both call for.
 *
 * Monaco is roughly ten times the size of the whole rest of this application. A requester — who by
 * §7.4 may not see a diff at all — must never pay for it, and neither should an engineer who stays
 * in Simple or Detailed. `lazy` puts it behind a dynamic import, so the bundler emits it as its own
 * chunk and the browser fetches it the first time somebody actually opens a file.
 */
const MonacoDiffPane = lazy(() => import('@/features/panes/MonacoDiffPane'));

const RISK_TONE: Record<ChangedFile['risk'], 'bad' | 'warn' | 'neutral'> = {
  high: 'bad',
  medium: 'warn',
  low: 'neutral',
};

/** Never colour alone (§27.7). Each risk level carries a glyph and the word as well as a hue. */
const RISK_ICON = {
  high: 'alert',
  medium: 'eye',
  low: 'check',
} as const;

export interface DiffPaneViewProps {
  requestId: Id;
  pane: ChangesPane;
  paneId: string;
}

/**
 * Pane 3 (§12): the changed files, and the diff of whichever one is selected.
 *
 * The file list is **rendered in the order the server sent it**. §14 ranks these by risk — auth,
 * migrations, money math and external calls above tests and formatting — and re-sorting client-side
 * would throw that away and replace it with alphabetical noise. `riskReasons` is rendered next to
 * each file for the same reason: a bare red "high" tells a reviewer nothing about where to start.
 */
export function DiffPaneView({ requestId, pane, paneId }: DiffPaneViewProps) {
  const api = useApi();
  const linkage = usePaneSelection();
  const { viewer } = useViewer();

  /*
   * Both of these are keyed by path rather than being bare values, so "which file is on screen" is
   * derived from comparing them with the selection instead of being synchronised into a `loading`
   * flag. That removes the effect's synchronous `setLoading(true)` — a cascading render — and, more
   * usefully, makes it impossible to show one file's diff under another file's name while a fetch
   * is in flight.
   */
  const [loaded, setLoaded] = useState<{ path: string; diff: FileDiff } | null>(null);
  const [failed, setFailed] = useState<{ path: string; error: Error } | null>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const selectedPath = linkage?.selection.filePath;
  const hunkIndex = linkage?.selection.hunkIndex;
  const nonce = linkage?.selection.nonce ?? 0;
  const focusPane = linkage?.selection.focusPane ?? null;

  const prefersDark = useMediaQuery('(prefers-color-scheme: dark)');
  const sideBySide = useMediaQuery('(min-width: 90rem)');
  const themePreference = viewer.preferences.theme;
  const dark = themePreference === 'dark' || (themePreference === 'system' && prefersDark);

  useEffect(() => {
    if (selectedPath === undefined) {
      return;
    }

    const controller = new AbortController();
    let live = true;

    api
      .getFileDiff(requestId, selectedPath, controller.signal)
      .then((next) => {
        if (live) {
          setLoaded({ path: selectedPath, diff: next });
        }
      })
      .catch((cause: unknown) => {
        if (live && !controller.signal.aborted) {
          setFailed({
            path: selectedPath,
            error: cause instanceof Error ? cause : new Error('Could not load this file'),
          });
        }
      });

    return () => {
      live = false;
      controller.abort();
    };
  }, [api, requestId, selectedPath]);

  const diff = loaded !== null && loaded.path === selectedPath ? loaded.diff : null;
  const error = failed !== null && failed.path === selectedPath ? failed.error : null;
  const loading = selectedPath !== undefined && diff === null && error === null;

  useEffect(() => {
    if (focusPane === 3) {
      // Focus the selected file's control rather than the pane, so the next Tab continues from a
      // sensible place and a screen reader reads out which file was opened.
      listRef.current?.querySelector<HTMLButtonElement>('[aria-current="true"]')?.focus();
    }
  }, [nonce, focusPane]);

  const onSelect = useCallback(
    (path: string, fromKeyboard: boolean) => {
      linkage?.selectFile(path, { fromKeyboard });
    },
    [linkage],
  );

  const headingId = `${paneId}-heading`;

  if (pane.files.length === 0) {
    return (
      <section aria-labelledby={headingId} className="flex h-full flex-col">
        <SectionLabel id={headingId}>Developer</SectionLabel>
        <EmptyState
          className="mt-4 border-0"
          description="Nothing has been written to the branch yet. Files appear here as the agent changes them, riskiest first."
          icon="diff"
          title="No file changes yet"
        />
      </section>
    );
  }

  return (
    <section aria-labelledby={headingId} className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <SectionLabel id={headingId}>Developer</SectionLabel>
        <p className="text-tiny text-ink-subtle">{pane.files.length} files, riskiest first</p>
      </div>

      <ul className="mt-2 max-h-56 shrink-0 space-y-0.5 overflow-y-auto" ref={listRef}>
        {pane.files.map((file) => {
          const selected = file.path === selectedPath;
          return (
            <li key={file.path}>
              <button
                aria-current={selected}
                className={cn(
                  'hover:bg-sunken w-full rounded-[0.375rem] px-2 py-1.5 text-left transition-colors',
                  selected && 'bg-accent-soft',
                )}
                onClick={(event) => {
                  onSelect(file.path, event.detail === 0);
                }}
                type="button"
              >
                <span className="flex items-center gap-2">
                  <StatusPill icon={RISK_ICON[file.risk]} tone={RISK_TONE[file.risk]}>
                    {file.risk}
                  </StatusPill>
                  <span className="text-small text-ink min-w-0 flex-1 truncate font-mono">
                    {file.path}
                  </span>
                  <span className="text-tiny text-ok shrink-0 font-mono">+{file.additions}</span>
                  <span className="text-tiny text-danger shrink-0 font-mono">
                    &minus;{file.deletions}
                  </span>
                </span>
                {file.riskReasons && file.riskReasons.length > 0 ? (
                  <span className="text-tiny text-ink-subtle mt-0.5 block pl-1">
                    {file.riskReasons.join(' · ')}
                  </span>
                ) : null}
              </button>
            </li>
          );
        })}
      </ul>

      <div className="border-line bg-surface rounded-control mt-3 min-h-0 flex-1 overflow-hidden border">
        {selectedPath === undefined ? (
          <div className="grid h-full place-items-center p-6">
            <p className="text-small text-ink-muted max-w-xs text-center text-balance">
              Pick a file above, or click a file change in the Detailed pane to open it at the exact
              hunk that produced it.
            </p>
          </div>
        ) : error ? (
          <div className="grid h-full place-items-center p-6">
            <p className="text-small text-danger flex items-center gap-2">
              <Icon name="alert" size={15} />
              {error.message}
            </p>
          </div>
        ) : loading || !diff ? (
          <div className="space-y-2 p-3">
            <Skeleton className="h-4 w-2/3" />
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-5/6" />
            <Skeleton className="h-4 w-1/2" />
          </div>
        ) : diff.binary ? (
          <div className="grid h-full place-items-center p-6 text-center">
            <p className="text-small text-ink-muted">
              This file is binary, so there is nothing to read side by side. Open the change request
              with your provider to download it.
            </p>
          </div>
        ) : (
          <div className="flex h-full flex-col">
            <p className="border-line text-tiny text-ink-subtle flex shrink-0 flex-wrap items-center gap-x-2 border-b px-2.5 py-1.5 font-mono">
              <span className="text-ink truncate">{diff.path}</span>
              {diff.hunks[hunkIndex ?? 0] ? (
                <span className="text-accent">{diff.hunks[hunkIndex ?? 0]?.header}</span>
              ) : null}
              {diff.truncated ? (
                <span className="text-warn">truncated — open in your provider for the rest</span>
              ) : null}
            </p>
            <div className="min-h-0 flex-1">
              <Suspense
                fallback={
                  <div className="space-y-2 p-3">
                    <Skeleton className="h-4 w-2/3" />
                    <Skeleton className="h-4 w-full" />
                    <Skeleton className="h-4 w-4/6" />
                  </div>
                }
              >
                <MonacoDiffPane
                  dark={dark}
                  diff={diff}
                  revealNonce={nonce}
                  sideBySide={sideBySide}
                  {...(hunkIndex === undefined ? {} : { hunkIndex })}
                />
              </Suspense>
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
