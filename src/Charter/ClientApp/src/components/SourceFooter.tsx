import { useCallback } from 'react';
import { useApi } from '@/api/api-context';
import { useAsync } from '@/hooks/useAsync';
import { formatDateTime } from '@/lib/format';

/**
 * AGPL section 13.
 *
 * A network-interactive instance of Charter must offer every user the Corresponding Source of the
 * *running* version, including whatever the operator changed. So this reads `version`, `commit` and
 * `sourceUrl` from `GET /api/instance` and links to the exact commit — a hard-coded link to the
 * upstream repository would not discharge the obligation for a modified instance.
 *
 * This is a licence requirement. It renders on every page, it is not dismissible, and if the
 * endpoint fails it degrades to the licence name rather than disappearing.
 */
export function SourceFooter() {
  const api = useApi();
  const load = useCallback((signal: AbortSignal) => api.getInstance(signal), [api]);
  const state = useAsync(load);

  return (
    <footer className="border-line text-tiny text-ink-subtle mt-auto border-t px-4 py-4 sm:px-6">
      <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-x-6 gap-y-2">
        {state.status === 'ready' ? (
          <>
            <p>
              <a
                className="hover:text-ink underline decoration-dotted underline-offset-4"
                href={state.data.sourceUrl}
                rel="noreferrer"
                target="_blank"
              >
                Source code for this instance
              </a>{' '}
              &middot; {state.data.license}
            </p>
            <p className="font-mono">
              {state.data.serviceName} {state.data.version}
              <span aria-hidden="true"> &middot; </span>
              <span title={`Built ${formatDateTime(state.data.buildDate)}`}>
                {state.data.commit}
              </span>
            </p>
          </>
        ) : (
          <p>
            Charter is licensed under AGPL-3.0-only. The source link for this instance is
            unavailable right now.
          </p>
        )}
      </div>
    </footer>
  );
}
