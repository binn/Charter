import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router';
import { ApiProvider } from '@/api/ApiProvider';
import { ApiError } from '@/api/client';
import { ViewerProvider } from '@/app/ViewerProvider';
import { createTestApi } from '@/test/harness';

/**
 * What happens when `GET /api/me` says no.
 *
 * Two of its refusals are routing decisions rather than errors, and both come from the spec: an
 * instance with no users answers **503** for everything but `/api/setup` (§30.1), and a browser
 * without a valid cookie gets **401**. Rendering "Charter cannot reach your account" for either
 * would strand someone one click from the page that fixes it.
 *
 * This is not the authorisation boundary and is not tested as one — the server refuses those
 * endpoints regardless of what the router drew. It decides which screen a person is looking at.
 */
function renderAt(path: string, error: unknown) {
  return render(
    <ApiProvider api={createTestApi({ getViewer: () => Promise.reject(error) })}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route element={<p>Setup page</p>} path="/setup" />
          <Route element={<SignInProbe />} path="/sign-in" />
          <Route
            element={
              <ViewerProvider>
                <p>Signed in</p>
              </ViewerProvider>
            }
            path="*"
          />
        </Routes>
      </MemoryRouter>
    </ApiProvider>,
  );
}

function SignInProbe() {
  const [params] = useSearchParams();
  return <p>Sign-in page, next: {params.get('next') ?? 'nowhere'}</p>;
}

describe('the session boundary', () => {
  it('sends a browser to first-run setup when the instance has no users (§30.1)', async () => {
    renderAt('/requests', new ApiError(503, 'This instance has no users.'));

    expect(await screen.findByText('Setup page')).toBeInTheDocument();
  });

  it('sends a browser with no session to sign in', async () => {
    renderAt('/requests', new ApiError(401, 'Sign in again and we will bring you back here.'));

    expect(await screen.findByText(/Sign-in page/)).toBeInTheDocument();

    // The path they were heading for comes with them, so signing in puts them back where they were.
    expect(screen.getByText(/next: \/requests/)).toBeInTheDocument();
  });

  it('shows a failure rather than a redirect loop when the server is simply broken', async () => {
    renderAt('/requests', new ApiError(500, 'Something went wrong on our side.'));

    expect(await screen.findByText('Charter cannot reach your account')).toBeInTheDocument();
  });
});
