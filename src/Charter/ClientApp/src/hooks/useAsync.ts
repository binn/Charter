import { useCallback, useEffect, useState } from 'react';

export type AsyncState<T> =
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'error'; error: Error };

/**
 * Fetch-on-mount with abort and a manual `reload`.
 *
 * `load` must be stable — wrap it in `useCallback`. It is the effect's only dependency, which is
 * what keeps this honest: there is no dependency array to get out of step with the closure.
 *
 * Deliberately not a query library. This app has a dozen endpoints and a live socket, and the
 * socket is the cache-invalidation story.
 */
export function useAsync<T>(
  load: (signal: AbortSignal) => Promise<T>,
): AsyncState<T> & { reload: () => void } {
  const [state, setState] = useState<AsyncState<T>>({ status: 'loading' });
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let live = true;

    load(controller.signal)
      .then((data) => {
        if (live) {
          setState({ status: 'ready', data });
        }
      })
      .catch((error: unknown) => {
        if (!live || controller.signal.aborted) {
          return;
        }
        setState({
          status: 'error',
          error: error instanceof Error ? error : new Error('Something went wrong'),
        });
      });

    return () => {
      live = false;
      controller.abort();
    };
  }, [load, nonce]);

  const reload = useCallback(() => {
    setNonce((value) => value + 1);
  }, []);

  return { ...state, reload };
}
