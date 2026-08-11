import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@/api/client';
import { AcceptInvitationPage } from '@/pages/AcceptInvitationPage';
import { ResetPasswordPage } from '@/pages/ResetPasswordPage';
import { createTestApi, renderPublicPage } from '@/test/harness';

/**
 * `/accept-invitation` and `/reset-password` — the two paths the emails Charter sends already point
 * at (`AccountService.AcceptInvitationPath`, `ResetPasswordPath`), each carrying `?token=`.
 *
 * Both links are single-use and short-lived, so *expired* is a routine outcome rather than an edge
 * case: people forward invitations, sit on them over a weekend, click them twice, or open one after
 * an admin has withdrawn it. For an invitation this is somebody's **first** contact with Charter —
 * a bare "invalid token" there is where a new colleague gives up and never comes back — so both
 * pages must say which kind of no it was and what to do about it.
 */
describe('accepting an invitation (§30.2)', () => {
  it('creates the account from the link and ends signed in', async () => {
    const acceptInvitation = vi.fn().mockResolvedValue({
      userId: 'u2',
      displayName: 'Priya',
      email: 'priya@northbeam.example',
      organizationId: 'org1',
      roles: ['requester'],
      provider: 'password',
    });

    renderPublicPage(
      <AcceptInvitationPage />,
      createTestApi({ acceptInvitation }),
      '/accept-invitation?token=inv_live_9d21',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Your name'), 'Priya Raman');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Accept and sign in/ }));

    expect(acceptInvitation).toHaveBeenCalledWith({
      token: 'inv_live_9d21',
      displayName: 'Priya Raman',
      password: 'correct-horse-battery',
    });
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('explains an expired invitation and offers a way forward', async () => {
    const acceptInvitation = vi
      .fn()
      .mockRejectedValue(
        new ApiError(400, 'That invitation has expired. Ask whoever invited you for a new one.'),
      );

    renderPublicPage(
      <AcceptInvitationPage />,
      createTestApi({ acceptInvitation }),
      '/accept-invitation?token=inv_expired_4b70',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Your name'), 'Priya');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Accept and sign in/ }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('That invitation has expired. Ask whoever invited you for a new one.');
    expect(screen.getByRole('link', { name: /sign in instead/i })).toHaveAttribute(
      'href',
      '/sign-in',
    );
  });

  it('says a spent invitation has been used rather than calling it invalid', async () => {
    const acceptInvitation = vi
      .fn()
      .mockRejectedValue(
        new ApiError(
          400,
          'That invitation has already been used. Sign in instead, or ask for a new one.',
        ),
      );

    renderPublicPage(
      <AcceptInvitationPage />,
      createTestApi({ acceptInvitation }),
      '/accept-invitation?token=spent',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('Your name'), 'Priya');
    await user.type(screen.getByLabelText('Choose a password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Accept and sign in/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/already been used/);
  });

  it('handles a link that arrived without its token', async () => {
    renderPublicPage(<AcceptInvitationPage />, createTestApi(), '/accept-invitation');

    expect(await screen.findByText('This link is incomplete')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(/ask whoever invited you to send a fresh one/i);
  });
});

describe('resetting a password', () => {
  it('sets the password from the link', async () => {
    const resetPassword = vi.fn().mockResolvedValue(undefined);

    renderPublicPage(
      <ResetPasswordPage />,
      createTestApi({ resetPassword }),
      '/reset-password?token=rst_live_5c88',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('New password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Save this password/ }));

    expect(resetPassword).toHaveBeenCalledWith({
      token: 'rst_live_5c88',
      password: 'correct-horse-battery',
    });
  });

  it('explains an expired reset link and links straight to a new one', async () => {
    const resetPassword = vi
      .fn()
      .mockRejectedValue(new ApiError(400, 'That reset link has expired. Ask for a new one.'));

    renderPublicPage(
      <ResetPasswordPage />,
      createTestApi({ resetPassword }),
      '/reset-password?token=rst_expired_1a02',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('New password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Save this password/ }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('That reset link has expired. Ask for a new one.');
    expect(screen.getByRole('link', { name: /Ask for a new link/ })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
  });

  it('says a spent link cannot be revived instead of failing silently', async () => {
    const resetPassword = vi
      .fn()
      .mockRejectedValue(new ApiError(400, 'That reset link has already been used. Ask for a new one.'));

    renderPublicPage(
      <ResetPasswordPage />,
      createTestApi({ resetPassword }),
      '/reset-password?token=spent',
    );

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('New password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: /Save this password/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/already been used/);
  });

  it('handles a link that arrived without its token', async () => {
    renderPublicPage(<ResetPasswordPage />, createTestApi(), '/reset-password');

    expect(await screen.findByText('This link is incomplete')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /ask for a new link/i })).toBeInTheDocument();
  });
});
