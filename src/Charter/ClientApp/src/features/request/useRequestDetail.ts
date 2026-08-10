import { useCallback, useEffect, useState } from 'react';
import { useApi } from '@/api/api-context';
import type { RequestDetail } from '@/api/types';
import { applyStreamEvent } from '@/features/request/applyStreamEvent';

export type RequestDetailState =
  | { status: 'loading' }
  | { status: 'error'; error: Error }
  | { status: 'ready'; request: RequestDetail };

const LOADING: RequestDetailState = { status: 'loading' };

/**
 * Loads one request and keeps it live.
 *
 * The subscription runs for as long as the page is open rather than only while a build is in
 * flight, because refinement messages arrive over the same channel and §11's "stream something"
 * applies to the conversation just as much as to the build.
 *
 * Navigating between two requests resets to `loading` during render rather than in an effect —
 * showing the previous request's thread under the new request's title for a frame is exactly the
 * "which of three cards is live" confusion §11 exists to prevent.
 */
export function useRequestDetail(id: string): RequestDetailState & { refresh: () => void } {
  const api = useApi();
  const [state, setState] = useState<RequestDetailState>(LOADING);
  const [nonce, setNonce] = useState(0);
  const [loadedId, setLoadedId] = useState(id);

  if (loadedId !== id) {
    setLoadedId(id);
    setState(LOADING);
  }

  useEffect(() => {
    const controller = new AbortController();
    let live = true;

    api
      .getRequest(id, controller.signal)
      .then((request) => {
        if (live) {
          setState({ status: 'ready', request });
        }
      })
      .catch((cause: unknown) => {
        if (live && !controller.signal.aborted) {
          setState({
            status: 'error',
            error: cause instanceof Error ? cause : new Error('Could not load this request'),
          });
        }
      });

    return () => {
      live = false;
      controller.abort();
    };
  }, [api, id, nonce]);

  useEffect(() => {
    const unsubscribe = api.subscribeToRequest(id, (event) => {
      setState((current) =>
        current.status === 'ready'
          ? { status: 'ready', request: applyStreamEvent(current.request, event) }
          : current,
      );
    });
    return unsubscribe;
  }, [api, id]);

  const refresh = useCallback(() => {
    setNonce((value) => value + 1);
  }, []);

  return { ...state, refresh };
}
