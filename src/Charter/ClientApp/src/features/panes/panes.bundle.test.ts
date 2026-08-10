import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * §3 and §12: Monaco is "lazy-loaded route split; **never in the requester bundle**".
 *
 * That is a property of the module graph, so this test checks the module graph rather than the
 * built output. Walking the graph is deterministic, runs in milliseconds, needs no build step, and
 * — unlike grepping a bundle — points at the exact file that broke the rule.
 *
 * The rule it enforces: **nothing reachable from `main.tsx` by static imports may reach Monaco.**
 * A single `import MonacoDiffPane from '...'` anywhere in the app would pull three megabytes into
 * the entry chunk for every requester, and it would do so silently — the app would still work, the
 * tests would still pass, and only a bundle inspection months later would find it.
 */

const SRC = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

const EXTENSIONS = ['.ts', '.tsx', '/index.ts', '/index.tsx', '.js'];

/** Static `import ... from '...'` and bare `import '...'`. Dynamic `import(...)` is excluded. */
const STATIC_IMPORT = /(?:^|\n)\s*import\s+(?:[^'"]*?\sfrom\s+)?['"]([^'"]+)['"]/g;
const EXPORT_FROM = /(?:^|\n)\s*export\s+(?:[^'"]*?\sfrom\s+)?['"]([^'"]+)['"]/g;

function resolveModule(specifier: string, fromFile: string): string | null {
  let base: string;
  if (specifier.startsWith('@/')) {
    base = resolve(SRC, specifier.slice(2));
  } else if (specifier.startsWith('.')) {
    base = resolve(dirname(fromFile), specifier);
  } else {
    // A bare specifier: a node_module. Not part of the first-party graph we walk, but its *name*
    // is what the Monaco assertion below is looking for.
    return null;
  }

  for (const extension of EXTENSIONS) {
    const candidate = base.endsWith('.ts') || base.endsWith('.tsx') ? base : `${base}${extension}`;
    try {
      readFileSync(candidate, 'utf8');
      return candidate;
    } catch {
      continue;
    }
  }
  return null;
}

interface Graph {
  /** Every first-party file reachable from the entry by static import. */
  files: Set<string>;
  /** Bare package specifiers statically imported by those files, and who imported each. */
  packages: Map<string, string[]>;
}

function walkStaticGraph(entry: string): Graph {
  const files = new Set<string>();
  const packages = new Map<string, string[]>();
  const queue = [entry];

  while (queue.length > 0) {
    const file = queue.pop() as string;
    if (files.has(file)) {
      continue;
    }
    files.add(file);

    const source = readFileSync(file, 'utf8');

    for (const pattern of [STATIC_IMPORT, EXPORT_FROM]) {
      pattern.lastIndex = 0;
      let match: RegExpExecArray | null;
      while ((match = pattern.exec(source)) !== null) {
        const specifier = match[1] as string;
        const resolved = resolveModule(specifier, file);
        if (resolved === null) {
          if (!specifier.startsWith('.') && !specifier.startsWith('@/')) {
            packages.set(specifier, [...(packages.get(specifier) ?? []), file]);
          }
          continue;
        }
        if (!files.has(resolved)) {
          queue.push(resolved);
        }
      }
    }
  }

  return { files, packages };
}

const MONACO_MODULE = resolve(SRC, 'features/panes/MonacoDiffPane.tsx');

describe('the requester bundle (§3, §12)', () => {
  const graph = walkStaticGraph(resolve(SRC, 'main.tsx'));

  it('finds the real application graph, not an empty one', () => {
    // Guards the test itself: a resolver that silently resolves nothing would make every assertion
    // below pass vacuously.
    expect(graph.files.size).toBeGreaterThan(30);
    expect([...graph.files]).toContain(resolve(SRC, 'features/panes/ThreePaneView.tsx'));
    expect([...graph.files]).toContain(resolve(SRC, 'features/panes/DiffPaneView.tsx'));
  });

  it('never reaches MonacoDiffPane by a static import', () => {
    expect([...graph.files]).not.toContain(MONACO_MODULE);
  });

  it('never statically imports the monaco-editor package from anywhere in the entry graph', () => {
    const offenders = [...graph.packages.entries()].filter(([specifier]) =>
      specifier.startsWith('monaco-editor'),
    );

    expect(
      offenders,
      `monaco-editor is statically imported by: ${offenders
        .flatMap(([, importers]) => importers)
        .join(', ')}`,
    ).toEqual([]);
  });

  it('reaches Monaco only through a dynamic import, and only from pane 3', () => {
    const diffPane = readFileSync(resolve(SRC, 'features/panes/DiffPaneView.tsx'), 'utf8');

    // The one permitted edge into Monaco: lazy(() => import(...)).
    expect(diffPane).toMatch(/lazy\(\s*\(\)\s*=>\s*import\('@\/features\/panes\/MonacoDiffPane'\)/);

    // And nobody else references the module at all.
    const referencing = [...graph.files].filter((file) =>
      readFileSync(file, 'utf8').includes('MonacoDiffPane'),
    );
    expect(referencing).toEqual([resolve(SRC, 'features/panes/DiffPaneView.tsx')]);
  });

  it('keeps the Monaco module free of anything that would drag the app in behind it', () => {
    // The reverse risk: MonacoDiffPane importing a shared barrel that imports half the app would
    // duplicate that code into the lazy chunk. It may depend on React and on types, nothing more.
    const monacoGraph = walkStaticGraph(MONACO_MODULE);
    const firstParty = [...monacoGraph.files].filter((file) => file !== MONACO_MODULE);

    expect(firstParty).toEqual([resolve(SRC, 'api/types.ts')]);
  });
});
