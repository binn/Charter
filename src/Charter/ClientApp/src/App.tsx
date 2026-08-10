import { useEffect, useState } from 'react';

interface Instance {
  version: string;
  commit: string;
  buildDate: string;
  sourceUrl: string;
  license: string;
  serviceName: string;
}

type LoadState =
  | { status: 'loading' }
  | { status: 'ready'; instance: Instance }
  | { status: 'error'; message: string };

function useInstance(): LoadState {
  const [state, setState] = useState<LoadState>({ status: 'loading' });

  useEffect(() => {
    const controller = new AbortController();

    fetch('/api/instance', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`the control plane returned ${response.status}`);
        }
        return (await response.json()) as Instance;
      })
      .then((instance) => {
        setState({ status: 'ready', instance });
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === 'AbortError') {
          return;
        }
        setState({
          status: 'error',
          message: error instanceof Error ? error.message : 'the control plane is unreachable',
        });
      });

    return () => {
      controller.abort();
    };
  }, []);

  return state;
}

export default function App() {
  const state = useInstance();

  return (
    <div className="flex min-h-full flex-col">
      <main className="mx-auto flex w-full max-w-3xl grow flex-col justify-center px-6 py-16">
        <h1 className="text-3xl font-semibold tracking-tight text-ink">Charter</h1>
        <p className="mt-3 max-w-prose text-ink-muted">
          File a request in plain language. Charter refines it into a specification you approve, has
          a coding agent implement it, and hands back something you can click and judge for
          yourself.
        </p>

        <section
          className="mt-10 rounded-lg border border-line bg-surface-raised p-5"
          aria-live="polite"
        >
          <h2 className="text-sm font-medium tracking-wide text-ink-muted uppercase">
            Control plane
          </h2>

          {state.status === 'loading' && (
            <p className="mt-3 text-sm text-ink-muted">Checking the control plane&hellip;</p>
          )}

          {state.status === 'error' && (
            <p className="mt-3 text-sm text-bad">Not reachable: {state.message}</p>
          )}

          {state.status === 'ready' && (
            <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
              <dt className="text-ink-muted">Version</dt>
              <dd className="font-mono text-ink">{state.instance.version}</dd>
              <dt className="text-ink-muted">Commit</dt>
              <dd className="font-mono text-ink">{state.instance.commit}</dd>
              <dt className="text-ink-muted">Built</dt>
              <dd className="font-mono text-ink">{state.instance.buildDate}</dd>
            </dl>
          )}
        </section>
      </main>

      {/*
        Section 24: AGPL section 13 requires a network-interactive instance to offer its users the
        Corresponding Source, including any operator modifications. This link is not decorative.
      */}
      <footer className="border-t border-line px-6 py-4 text-center text-xs text-ink-muted">
        {state.status === 'ready' ? (
          <a
            className="underline underline-offset-2 hover:text-ink"
            href={state.instance.sourceUrl}
            rel="noreferrer"
            target="_blank"
          >
            Source ({state.instance.license})
          </a>
        ) : (
          <span>Source (AGPL-3.0-only)</span>
        )}
      </footer>
    </div>
  );
}
