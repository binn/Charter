import type { ChangedFile, ChangesPane } from '@/api/types';
import { SectionLabel } from '@/components/ui/Card';
import { StatusPill } from '@/components/ui/StatusPill';
import { cn } from '@/lib/cn';

const RISK_TONE: Record<ChangedFile['risk'], 'bad' | 'warn' | 'neutral'> = {
  high: 'bad',
  medium: 'warn',
  low: 'neutral',
};

const RISK_ORDER: Record<ChangedFile['risk'], number> = { high: 0, medium: 1, low: 2 };

export interface ChangesPaneViewProps {
  pane: ChangesPane;
  selectedPath?: string;
  onSelectFile?: (path: string) => void;
}

/**
 * Pane 3 (§12): the diff viewer.
 *
 * **Not finished.** §3 specifies Monaco's `DiffEditor`, lazy-loaded, and that is a deliberate
 * Phase 2 item rather than something to fake: a diff viewer that is nearly right is worse than an
 * honest file list. What is here is the risk-ranked file list from §14 — auth, migrations, money
 * math and external calls above tests and formatting — which is the part that decides where a
 * reviewer starts.
 *
 * §12 also fixes the shape of the eventual editor: **viewer, not editor, in v1**, because a human
 * and an agent writing the same worktree concurrently is unanswerable. The escape hatch is "open in
 * GitHub".
 */
export function ChangesPaneView({ pane, selectedPath, onSelectFile }: ChangesPaneViewProps) {
  const files = [...pane.files].sort((a, b) => RISK_ORDER[a.risk] - RISK_ORDER[b.risk]);

  return (
    <div>
      <SectionLabel>Files changed</SectionLabel>
      <ul className="mt-3 space-y-1">
        {files.map((file) => (
          <li key={file.path}>
            <button
              aria-current={file.path === selectedPath}
              className={cn(
                'hover:bg-sunken flex w-full items-center gap-2 rounded-[0.375rem] px-2 py-1.5 text-left transition-colors',
                file.path === selectedPath && 'bg-sunken',
              )}
              onClick={() => {
                onSelectFile?.(file.path);
              }}
              type="button"
            >
              <span className="text-small text-ink min-w-0 flex-1 truncate font-mono">
                {file.path}
              </span>
              <span className="text-tiny text-ok font-mono">+{file.additions}</span>
              <span className="text-tiny text-danger font-mono">&minus;{file.deletions}</span>
              <StatusPill tone={RISK_TONE[file.risk]}>{file.risk}</StatusPill>
            </button>
          </li>
        ))}
      </ul>

      <p className="border-line text-tiny text-ink-subtle mt-4 rounded-control border border-dashed px-3 py-2.5">
        The side-by-side diff lands with Monaco, lazy-loaded, in the next phase. Until then, open the
        pull request on GitHub to read the change.
      </p>
    </div>
  );
}
