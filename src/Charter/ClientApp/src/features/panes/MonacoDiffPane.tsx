import { useEffect, useRef } from 'react';
/*
 * Subpath imports go through monaco's `exports` map, which rewrites `monaco-editor/x` to
 * `esm/vs/x` — so the path here deliberately omits the `esm/vs` prefix that appears on disk.
 *
 * `editor.api.js` is the core: the editor, the diff editor, and nothing else. The alternative,
 * `editor.main.js`, additionally pulls in every language service and every contribution, and is
 * several megabytes. This is a read-only diff viewer, so it does not need a suggest widget.
 */
import * as monaco from 'monaco-editor/editor/editor.api.js';
// Ctrl+F inside the diff. The one contribution worth its weight in a read-only viewer.
import 'monaco-editor/editor/contrib/find/browser/findController.js';
// Registers every basic language as a *lazy* loader — 4 kB of `registerLanguage({ loader })` calls.
// The tokenizer for the language actually being viewed arrives as its own chunk, on demand.
import 'monaco-editor/basic-languages/monaco.contribution.js';
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker';
import type { FileDiff } from '@/api/types';

/**
 * Pane 3 (§12, §3): Monaco's `DiffEditor`.
 *
 * **This module is the reason pane 3 is a route split.** It is the only file in the app that imports
 * Monaco, and the only import of *it* is the `lazy(() => import(...))` in `DiffPaneView`. Nothing
 * statically reachable from `main.tsx` touches this file, so Monaco cannot end up in the entry
 * chunk — the requester, who may not see a diff at all, never downloads a byte of it.
 * `panes.bundle.test.ts` walks the static import graph and fails if that ever stops being true.
 *
 * **Viewer, not editor, in v1** (§12). `readOnly` on both sides is not a UI preference here; it is
 * the answer to the question §12 says is unanswerable — what happens when a human edits a file the
 * agent is concurrently writing in the same worktree. The escape hatch is "open in your provider".
 */

/*
 * The diff itself is computed in a worker, so a large file does not block the main thread. Vite's
 * `?worker` import emits it as a separate asset and bundles it properly; the default Monaco
 * behaviour of fetching a worker from a CDN would be both a network dependency and a CSP problem.
 */
globalThis.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

/*
 * Charter's tokens, as hex.
 *
 * Monaco takes colours as strings and cannot read CSS custom properties, so these are the exact
 * conversions of the `oklch()` values in `index.css` rather than a second palette invented here. If
 * a token changes there, it changes here — checked by eye against the same source, which is why the
 * variable names match one for one.
 */
const LIGHT = {
  bg: '#ffffff',
  gutter: '#f9fafc',
  ink: '#14171b',
  inkSubtle: '#686c72',
  line: '#e0e3e7',
  accent: '#206b5d',
  insertedBg: '#e3f8e9',
  removedBg: '#ffefec',
  selection: '#b6ded4',
};

const DARK = {
  bg: '#16191c',
  gutter: '#0c0f12',
  ink: '#eceff2',
  inkSubtle: '#888d93',
  line: '#272a2f',
  accent: '#66c1ae',
  insertedBg: '#0c2b19',
  removedBg: '#3f1917',
  selection: '#214b42',
};

let themesDefined = false;

function defineThemes(): void {
  if (themesDefined) {
    return;
  }
  themesDefined = true;

  monaco.editor.defineTheme('charter-light', {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': LIGHT.bg,
      'editor.foreground': LIGHT.ink,
      'editorGutter.background': LIGHT.gutter,
      'editorLineNumber.foreground': LIGHT.inkSubtle,
      'editorLineNumber.activeForeground': LIGHT.accent,
      'editorIndentGuide.background1': LIGHT.line,
      'editor.selectionBackground': LIGHT.selection,
      'diffEditor.insertedTextBackground': `${LIGHT.insertedBg}cc`,
      'diffEditor.removedTextBackground': `${LIGHT.removedBg}cc`,
      'diffEditor.border': LIGHT.line,
      'editorOverviewRuler.border': LIGHT.line,
      'scrollbarSlider.background': `${LIGHT.line}cc`,
    },
  });

  monaco.editor.defineTheme('charter-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': DARK.bg,
      'editor.foreground': DARK.ink,
      'editorGutter.background': DARK.gutter,
      'editorLineNumber.foreground': DARK.inkSubtle,
      'editorLineNumber.activeForeground': DARK.accent,
      'editorIndentGuide.background1': DARK.line,
      'editor.selectionBackground': DARK.selection,
      'diffEditor.insertedTextBackground': `${DARK.insertedBg}cc`,
      'diffEditor.removedTextBackground': `${DARK.removedBg}cc`,
      'diffEditor.border': DARK.line,
      'editorOverviewRuler.border': DARK.line,
      'scrollbarSlider.background': `${DARK.line}cc`,
    },
  });
}

export interface MonacoDiffPaneProps {
  diff: FileDiff;
  /** Resolved light/dark, computed by the caller from the server-held preference. */
  dark: boolean;
  /** Index into `diff.hunks`, from the pane-2 file-write event that opened this file (§12). */
  hunkIndex?: number;
  /** Bumped on every navigation, so re-selecting the same hunk scrolls back to it. */
  revealNonce: number;
  /** Side-by-side needs width; the caller decides from the viewport, not the editor. */
  sideBySide: boolean;
}

export default function MonacoDiffPane({
  diff,
  dark,
  hunkIndex,
  revealNonce,
  sideBySide,
}: MonacoDiffPaneProps) {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) {
      return;
    }

    defineThemes();

    const editor = monaco.editor.createDiffEditor(host, {
      // §12: viewer, not editor.
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      renderSideBySide: sideBySide,
      renderOverviewRuler: false,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: 12,
      lineHeight: 19,
      fontFamily:
        "ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace",
      theme: dark ? 'charter-dark' : 'charter-light',
      scrollbar: { alwaysConsumeMouseWheel: false },
      // Monaco puts a real textbox on the page for each side. Unlabelled, a screen reader announces
      // two anonymous edit fields and gives no clue which is the old code and which is the new.
      originalAriaLabel: 'Original file, before the change',
      modifiedAriaLabel: 'Modified file, after the change',
    });

    editorRef.current = editor;

    return () => {
      editorRef.current = null;
      const model = editor.getModel();
      editor.dispose();
      model?.original.dispose();
      model?.modified.dispose();
    };
    // Created once. Everything that changes afterwards is applied through the effects below, because
    // tearing down and rebuilding an editor loses scroll position and selection.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) {
      return;
    }

    const previous = editor.getModel();
    const original = monaco.editor.createModel(diff.originalText, diff.language);
    const modified = monaco.editor.createModel(diff.modifiedText, diff.language);
    editor.setModel({ original, modified });
    previous?.original.dispose();
    previous?.modified.dispose();
  }, [diff]);

  useEffect(() => {
    monaco.editor.setTheme(dark ? 'charter-dark' : 'charter-light');
  }, [dark]);

  useEffect(() => {
    editorRef.current?.updateOptions({ renderSideBySide: sideBySide });
  }, [sideBySide]);

  /*
   * §12's second link: "clicking a file-write event in pane 2 opens pane 3 **at that hunk**".
   * Opening at the top of the file would be opening the file, not the hunk, and the reader would
   * still have to go looking for what changed.
   */
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) {
      return;
    }
    const hunk = hunkIndex === undefined ? diff.hunks[0] : diff.hunks[hunkIndex];
    if (!hunk) {
      return;
    }
    const modifiedEditor = editor.getModifiedEditor();
    modifiedEditor.revealLineInCenter(hunk.modifiedStartLine);
    modifiedEditor.setPosition({ lineNumber: hunk.modifiedStartLine, column: 1 });
  }, [diff, hunkIndex, revealNonce]);

  return <div className="h-full min-h-0 w-full" data-testid="monaco-diff" ref={hostRef} />;
}
