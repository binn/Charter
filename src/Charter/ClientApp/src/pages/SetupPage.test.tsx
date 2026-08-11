import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/api/client';
import { SetupPage } from '@/pages/SetupPage';
import { createTestApi, renderPublicPage } from '@/test/harness';

/**
 * §30.1, instance first run — the one flow in this app that is security-critical rather than merely
 * important. An instance that boots with open registration belongs to whoever finds it first, so the
 * only way in is a token the operator can read and a stranger cannot.
 *
 * The two things worth pinning are therefore: **the page says where the token comes from**, because
 * "read it from the container logs" is the single least discoverable step in installing Charter; and
 * **a refused token is recoverable**, because the likeliest reasons — a half-copied value, a
 * container that restarted and printed a new one — are all fixable in place.
 */
describe('first run (§30.1)', () => {
  const setupApi = (overrides = {}) =>
    createTestApi({
      getSetupStatus: () => Promise.resolve({ setupRequired: true }),
      ...overrides,
    });

  it('tells the operator the token is in the container logs, and how to read it', async () => {
    renderPublicPage(<SetupPage />, setupApi(), '/setup');

    expect(await screen.findByText(/The token is in the server’s logs/)).toBeInTheDocument();
    expect(screen.getByText(/docker compose logs charter/)).toBeInTheDocument();
    expect(screen.getByText(/kubectl logs/)).toBeInTheDocument();
  });

  it('says plainly that this creates one administrator and closes setup', async () => {
    renderPublicPage(<SetupPage />, setupApi(), '/setup');

    expect(
      await screen.findByText(/creates one administrator and closes setup for good/i),
    ).toBeInTheDocument();
  });

  it('redeems the token and never touches browser storage on the way', async () => {
    const completeSetup = vi.fn().mockResolvedValue({
      userId: 'u1',
      displayName: 'Ana',
      email: 'ana@northbeam.example',
      organizationId: 'org1',
      roles: ['admin'],
      provider: 'password',
    });

    renderPublicPage(<SetupPage />, setupApi({ completeSetup }), '/setup');

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Setup token'), 'chtr_setup_7f3a91c4e28b40d6');
    await user.type(screen.getByLabelText('Your name'), 'Ana Ferreira');
    await user.type(screen.getByLabelText('Your email address'), 'ana@northbeam.example');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Create the first account/ }));

    await waitFor(() => {
      expect(completeSetup).toHaveBeenCalledWith(
        expect.objectContaining({
          token: 'chtr_setup_7f3a91c4e28b40d6',
          email: 'ana@northbeam.example',
          displayName: 'Ana Ferreira',
          password: 'correct-horse-battery',
        }),
      );
    });

    // The session is an HTTP-only cookie the server set on that response. Nothing about it — no
    // token, no flag, no "signed in" marker — is written anywhere the client can reach.
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('a wrong token is a recoverable message, not a dead end', async () => {
    const completeSetup = vi
      .fn()
      .mockRejectedValue(
        new ApiError(400, 'That setup token is not correct. Check the container logs.'),
      );

    renderPublicPage(<SetupPage />, setupApi({ completeSetup }), '/setup');

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Setup token'), 'not-the-token');
    await user.type(screen.getByLabelText('Your name'), 'Ana');
    await user.type(screen.getByLabelText('Your email address'), 'ana@northbeam.example');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Create the first account/ }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('That setup token is not correct. Check the container logs.');
    expect(alert).toHaveTextContent(/Nothing you typed has been lost/);
    expect(alert).toHaveTextContent(/the token in the logs is a new one/);

    // Everything typed survives, and the form is still submittable — the whole point of "recoverable".
    expect(screen.getByLabelText('Your name')).toHaveValue('Ana');
    expect(screen.getByRole('button', { name: /Create the first account/ })).toBeEnabled();
  });

  it('an expired token says how to get a fresh one, in the server’s own words', async () => {
    const completeSetup = vi
      .fn()
      .mockRejectedValue(
        new ApiError(
          400,
          'That setup token has expired. Restart Charter and use the new token from the logs.',
        ),
      );

    renderPublicPage(<SetupPage />, setupApi({ completeSetup }), '/setup');

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Setup token'), 'stale');
    await user.type(screen.getByLabelText('Your name'), 'Ana');
    await user.type(screen.getByLabelText('Your email address'), 'ana@northbeam.example');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Create the first account/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /Restart Charter and use the new token from the logs/,
    );
  });

  it('sends somebody who already set this instance up to sign in instead', async () => {
    renderPublicPage(
      <SetupPage />,
      createTestApi({ getSetupStatus: () => Promise.resolve({ setupRequired: false }) }),
      '/setup',
    );

    // Setup mode ends permanently: there is no second chance at claiming an instance.
    await waitFor(() => {
      expect(screen.queryByLabelText('Setup token')).not.toBeInTheDocument();
    });
  });
});
