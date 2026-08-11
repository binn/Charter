import type { AuthProviders, Session } from '@/api/types';

/**
 * First run, sign-in and the two one-time links (§30.1, §21), as the mock plays them.
 *
 * The sentences here are the ones the control plane actually sends — they were copied from
 * `Charter.Auth` rather than invented, so what the mock rehearses is what a real instance will say.
 * That matters most for the two the spec is emphatic about: sign-in refuses an unknown account and a
 * wrong password with **one** sentence, and setup refuses a bad token with a sentence that says
 * where to get a good one.
 */

/** The minimum the control plane's hasher accepts (NIST 800-63B: length, no composition rules). */
export const MINIMUM_PASSWORD_LENGTH = 12;

/**
 * What a real instance writes to stdout on first boot. The mock accepts this one value so the setup
 * page can be driven end to end; a real token is never guessable and never appears in the UI.
 */
export const MOCK_SETUP_TOKEN = 'chtr_setup_7f3a91c4e28b40d6';

/** The account the mock will let you sign in as, printed on the sign-in page in development. */
export const MOCK_CREDENTIALS = {
  email: 'ana@northbeam.example',
  password: 'correct-horse-battery',
} as const;

/**
 * §21, and the reason the sign-in page must not "improve" its error handling: "no such account" and
 * "wrong password" arrive as the same value carrying the same words. Two messages would let anybody
 * with a browser test whether an address has an account here.
 */
export const SIGN_IN_REFUSAL = 'That email address and password do not match an account.';

/** What the throttle says. A sentence, not a raw 429. */
export const SIGN_IN_THROTTLED = 'Too many sign-in attempts. Try again shortly.';

/** After this many failures in a row the mock throttles, as the real one does. */
export const SIGN_IN_ATTEMPT_LIMIT = 5;

export const SETUP_TOKEN_REFUSAL =
  'That setup token is not correct. Check the container logs.';

export const SETUP_ALREADY_COMPLETED =
  'This instance has already been set up. Sign in instead.';

export const INVITATION_REFUSAL =
  'That invitation link is not one we recognise. Ask whoever invited you for a new one.';

export const INVITATION_EXPIRED =
  'That invitation has expired. Ask whoever invited you for a new one.';

export const RESET_LINK_EXPIRED = 'That reset link has expired. Ask for a new one.';

export const RESET_LINK_REFUSAL = 'That reset link is not one we recognise. Ask for a new one.';

/**
 * The one-time links the mock treats as live. Everything else is refused, which is what makes the
 * expiry paths on `/accept-invitation` and `/reset-password` reachable in a demo — those two pages
 * are the first thing a new colleague ever sees of Charter, and the unhappy path is the one that
 * actually gets hit.
 */
export const MOCK_INVITATION_TOKEN = 'inv_live_9d21';
export const MOCK_EXPIRED_INVITATION_TOKEN = 'inv_expired_4b70';
export const MOCK_RESET_TOKEN = 'rst_live_5c88';
export const MOCK_EXPIRED_RESET_TOKEN = 'rst_expired_1a02';

/**
 * §21: password always, then whichever federated providers the operator configured.
 *
 * The mock reports GitHub and nothing else, because that is the point of the endpoint — a provider
 * nobody set up must not appear on the page. Google, Discord, Slack and SAML are all supported by
 * the control plane and all absent from this instance.
 */
export function makeAuthProviders(): AuthProviders {
  return {
    providers: [
      { name: 'password', style: 'credential' },
      { name: 'github', style: 'redirect', startUrl: '/api/auth/github/start' },
    ],
    selfServicePasswordReset: true,
  };
}

export function makeSession(displayName: string, email: string): Session {
  return {
    userId: 'user-mock',
    displayName,
    email,
    organizationId: 'org-northbeam',
    roles: ['admin', 'engineer', 'approver', 'requester'],
    provider: 'password',
  };
}
