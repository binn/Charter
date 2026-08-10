import { createContext, use } from 'react';
import type { UserPreferences, Viewer } from '@/api/types';

export interface ViewerContextValue {
  viewer: Viewer;
  /**
   * Preferences are written straight back to the user record (`PATCH /api/me/preferences`) and the
   * response replaces local state. There is no browser storage anywhere in this app: a preference
   * that only existed in the tab could desynchronise from server-enforced permissions, which is
   * exactly what AGENTS.md forbids.
   */
  updatePreferences: (patch: Partial<UserPreferences>) => Promise<void>;
  completeOnboarding: () => Promise<void>;
}

export const ViewerContext = createContext<ViewerContextValue | null>(null);

export function useViewer(): ViewerContextValue {
  const value = use(ViewerContext);
  if (!value) {
    throw new Error('useViewer must be used inside <ViewerProvider>');
  }
  return value;
}
