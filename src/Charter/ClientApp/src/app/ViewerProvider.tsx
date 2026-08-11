import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { ThemePreference, UserPreferences, Viewer } from '@/api/types';
import { ViewerContext, type ViewerContextValue } from '@/app/viewer-context';
import { FullPageError, FullPageLoading } from '@/components/FullPageState';

/**
 * Applies the resolved theme to <html>.
 *
 * `system` removes the attribute so the `prefers-color-scheme` block in index.css takes over; an
 * explicit choice stamps the attribute and wins in both directions. Nothing is written to storage —
 * on the next load the preference comes back from `GET /api/me`, which is the point of holding it
 * server-side (AGENTS.md).
 */
function applyTheme(theme: ThemePreference): void {
  const root = document.documentElement;
  if (theme === 'system') {
    root.removeAttribute('data-theme');
  } else {
    root.setAttribute('data-theme', theme);
  }
}

export function ViewerProvider({ children }: { children: ReactNode }) {
  const api = useApi();
  const location = useLocation();
  const [viewer, setViewer] = useState<Viewer | null>(null);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let live = true;

    api
      .getViewer(controller.signal)
      .then((next) => {
        if (live) {
          setViewer(next);
        }
      })
      .catch((cause: unknown) => {
        if (live && !controller.signal.aborted) {
          setError(cause instanceof Error ? cause : new Error('Could not load your account'));
        }
      });

    return () => {
      live = false;
      controller.abort();
    };
  }, [api]);

  useEffect(() => {
    if (viewer) {
      applyTheme(viewer.preferences.theme);
    }
  }, [viewer]);

  const updatePreferences = useCallback(
    async (patch: Partial<UserPreferences>) => {
      // Optimistic: a theme toggle that waits on a round trip feels broken. The server response
      // below is authoritative and replaces this.
      setViewer((current) =>
        current ? { ...current, preferences: { ...current.preferences, ...patch } } : current,
      );
      const preferences = await api.updatePreferences(patch);
      setViewer((current) => (current ? { ...current, preferences } : current));
    },
    [api],
  );

  const completeOnboarding = useCallback(async () => {
    const next = await api.completeRequesterOnboarding();
    setViewer(next);
  }, [api]);

  const value = useMemo<ViewerContextValue | null>(
    () => (viewer ? { viewer, updatePreferences, completeOnboarding } : null),
    [viewer, updatePreferences, completeOnboarding],
  );

  if (error) {
    // §30.1: an instance with no users answers everything but `/api/setup` with 503, so this is
    // how a fresh container ends up on the setup page rather than on a sign-in form that could not
    // possibly work.
    if (error instanceof ApiError && error.status === 503) {
      return <Navigate replace to="/setup" />;
    }

    // No cookie, or an expired one. The path they were heading for is carried across so signing in
    // puts them back where they were, and it is a same-origin path rather than a URL — a `next`
    // that could be absolute is an open redirect.
    if (error instanceof ApiError && error.status === 401) {
      const next = `${location.pathname}${location.search}`;
      const to =
        next === '/' || next.startsWith('/sign-in')
          ? '/sign-in'
          : `/sign-in?next=${encodeURIComponent(next)}`;
      return <Navigate replace to={to} />;
    }

    return (
      <FullPageError
        title="Charter cannot reach your account"
        detail="The control plane did not answer. If this keeps happening, whoever runs your Charter will want to know."
      />
    );
  }

  if (!value) {
    return <FullPageLoading label="Loading Charter" />;
  }

  return <ViewerContext value={value}>{children}</ViewerContext>;
}
