import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/api/client';
import { SignInPage } from '@/pages/SignInPage';
import { createTestApi, renderPublicPage } from '@/test/harness';

/**
 * §21, sign-in.
 *
 * The load-bearing test in this file is the second one. "No such account" and "wrong password" must
 * be **the same sentence**, because two different messages let anybody with a browser ask whether a
 * given address has an account on this instance — a free directory of who works somewhere, and the
 * first step of a targeted phishing run. It reads like an unhelpful error message and it is a
 * deliberate one, so it gets a test that fails the moment somebody "improves" it.
 */
async function signInWith(email: string, password: string) {
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Email address'), email);
  await user.type(screen.getByLabelText('Password'), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

const REFUSAL = 'That email address and password do not match an account.';

describe('sign in (§21)', () => {
  it('signs in with an email and a password and goes where they were headed', async () => {
    const signIn = vi.fn().mockResolvedValue({
      userId: 'u1',
      displayName: 'Ana',
      email: 'ana@northbeam.example',
      organizationId: 'org1',
      roles: ['requester'],
      provider: 'password',
    });

    renderPublicPage(<SignInPage />, createTestApi({ signIn }), '/sign-in?next=/requests/req-pdf');

    await signInWith('ana@northbeam.example', 'correct-horse-battery');

    expect(signIn).toHaveBeenCalledWith({
      email: 'ana@northbeam.example',
      password: 'correct-horse-battery',
    });

    // No token is handed to the client and nothing is written down: the response set an HTTP-only
    // cookie, which is the entirety of the client's involvement in the session.
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('says exactly the same thing for an unknown account and for a wrong password', async () => {
    const unknownAccount = vi.fn().mockRejectedValue(new ApiError(401, REFUSAL));
    const view = renderPublicPage(
      <SignInPage />,
      createTestApi({ signIn: unknownAccount }),
      '/sign-in',
    );

    await signInWith('nobody@northbeam.example', 'whatever-they-typed');
    const forUnknownAccount = (await screen.findByRole('alert')).textContent;

    view.unmount();

    const wrongPassword = vi.fn().mockRejectedValue(new ApiError(401, REFUSAL));
    renderPublicPage(<SignInPage />, createTestApi({ signIn: wrongPassword }), '/sign-in');

    await signInWith('ana@northbeam.example', 'not-her-password');
    const forWrongPassword = (await screen.findByRole('alert')).textContent;

    expect(forWrongPassword).toBe(forUnknownAccount);
    expect(forWrongPassword).toBe(REFUSAL);
  });

  it('turns the rate limiter into a sentence rather than a raw error', async () => {
    const signIn = vi
      .fn()
      .mockRejectedValue(new ApiError(429, 'Too many sign-in attempts. Try again shortly.', 60));

    renderPublicPage(<SignInPage />, createTestApi({ signIn }), '/sign-in');
    await signInWith('ana@northbeam.example', 'wrong');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Too many sign-in attempts. Try again shortly.');
    expect(alert).toHaveTextContent(/about 60 seconds/);
    expect(alert).toHaveTextContent(/Nothing is wrong with your account/);
    expect(alert.textContent).not.toMatch(/429/);
  });

  it('renders a provider button only for a provider the instance reported', async () => {
    renderPublicPage(
      <SignInPage />,
      createTestApi({
        getAuthProviders: () =>
          Promise.resolve({
            providers: [
              { name: 'password', style: 'credential' },
              { name: 'github', style: 'redirect', startUrl: '/api/auth/github/start' },
            ],
            selfServicePasswordReset: true,
          }),
      }),
      '/sign-in',
    );

    expect(await screen.findByRole('link', { name: /Continue with GitHub/ })).toHaveAttribute(
      'href',
      '/api/auth/github/start',
    );

    // Google, Discord, Slack and SAML are all things Charter supports and this instance has not
    // configured. A button for one of them would lead somewhere broken.
    expect(screen.queryByRole('link', { name: /Google/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Slack/ })).not.toBeInTheDocument();
  });

  it('offers no provider buttons at all when the instance reports only the password form', async () => {
    renderPublicPage(<SignInPage />, createTestApi(), '/sign-in');

    expect(await screen.findByLabelText('Email address')).toBeInTheDocument();
    expect(screen.queryByText(/Continue with/)).not.toBeInTheDocument();
  });

  it('says who to ask when this instance cannot email a reset link', async () => {
    renderPublicPage(
      <SignInPage />,
      createTestApi({
        getAuthProviders: () =>
          Promise.resolve({
            providers: [{ name: 'password', style: 'credential' }],
            selfServicePasswordReset: false,
          }),
      }),
      '/sign-in',
    );

    expect(await screen.findByText(/cannot send email/)).toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: /I have forgotten my password/ }),
    ).not.toBeInTheDocument();
  });

  it('associates its labels with its fields and leaves the submit button enabled', async () => {
    renderPublicPage(<SignInPage />, createTestApi(), '/sign-in');

    expect(await screen.findByLabelText('Email address')).toHaveAttribute('type', 'email');
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password');
    // Nothing is disabled until a request is actually in flight, and then the label says so.
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled();
  });
});
