import { createContext, use } from 'react';
import type { CharterApi } from '@/api/client';
import { httpApi } from '@/api/client';
import { mockApi } from '@/api/mock/mockApi';

/**
 * Chooses the implementation the app runs against.
 *
 * The control plane has no request, spec, or artifact endpoints yet, so the app runs on the mock.
 * When the backend lands, delete the `mockApi` branch — no component changes, because nothing
 * outside this module imports either implementation directly.
 */
export function resolveApi(): CharterApi {
  return import.meta.env.VITE_CHARTER_LIVE_API === 'true' ? httpApi : mockApi;
}

export const ApiContext = createContext<CharterApi | null>(null);

export function useApi(): CharterApi {
  const api = use(ApiContext);
  if (!api) {
    throw new Error('useApi must be used inside <ApiProvider>');
  }
  return api;
}
