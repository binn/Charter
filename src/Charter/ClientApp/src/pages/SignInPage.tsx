import { useCallback, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { AuthProvider } from '@/api/types';
import { Button, ButtonLink } from '@/components/ui/Button';
import { Icon, type IconName } from '@/components/ui/Icon';
import { TextInput } from '@/components/ui/Field';
import { AuthPage, FormAlert } from '@/features/auth/AuthPage';
import { useAsync } from '@/hooks/useAsync';

/**
 * §21. Email and password always; everything else only if the operator configured it.
 *
 * Two rules govern this page and both are security properties rather than polish:
 *
 * 1. **The refusal is one sentence, whatever went wrong.** "No such account" and "wrong password"
 *    come back from the server as the same words, and this page shows them verbatim. Splitting them
 *    into two friendlier messages would turn the form into an oracle for whether an address has an
 *    account on this instance — the client must not "improve" that.
 * 2. **Providers come from the server.** The buttons below are built from
 *    `GET /api/auth/providers`, so a provider nobody configured never appears. A hardcoded row of
 *    logos would offer people a button that lands on a misconfiguration error.
 */

/** A brand mark per provider is a dependency and a trademark problem; a shape and a name is not. */
const PROVIDER_ICONS: Record<string, IconName> = {
  github: 'diff',
  google: 'spark',
  discord: 'message',
  slack: 'message',
  saml: 'key',
};

function providerLabel(name: string): string {
  switch (name) {
    case 'github':
      return 'GitHub';
    case 'google':
      return 'Google';
    case 'discord':
      return 'Discord';
    case 'slack':
      return 'Slack';
    case 'saml':
      return 'your organisation’s single sign-on';
    default:
      return name.charAt(0).toUpperCase() + name.slice(1);
  }
}

export function SignInPage() {
  const api = useApi();
  const navigate = useNavigate();
  const [search] = useSearchParams();
  const load = useCallback((signal: AbortSignal) => api.getAuthProviders(signal), [api]);
  const providers = useAsync(load);

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  /** Where they were headed before the session ran out. Same-origin paths only. */
  const next = search.get('next');
  const destination = next !== null && next.startsWith('/') && !next.startsWith('//') ? next : '/requests';

  // Someone who has just set a new password arrives here rather than signed in: the reset endpoint
  // deliberately issues no session, so the last step is proving the password works.
  const passwordWasReset = search.get('reset') === 'done';

  const redirects: AuthProvider[] =
    providers.status === 'ready'
      ? providers.data.providers.filter(
          (provider) => provider.style === 'redirect' && provider.startUrl !== undefined,
        )
      : [];

  const selfServiceReset = providers.status === 'ready' && providers.data.selfServicePasswordReset;

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .signIn({ email: email.trim(), password })
      .then(() => {
        void navigate(destination, { replace: true });
      })
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 429) {
          // The throttle, as a sentence. A raw 429 tells a non-engineer nothing, and "try again
          // shortly" with a number is the difference between waiting and refreshing forever.
          setFailure(
            error.retryAfterSeconds === undefined
              ? `${error.message} Nothing is wrong with your account.`
              : `${error.message} Give it about ${error.retryAfterSeconds} seconds. Nothing is wrong with your account.`,
          );
          return;
        }

        setFailure(
          error instanceof ApiError
            ? error.message
            : 'Charter could not reach the server just now. Try again in a moment.',
        );
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <AuthPage
      footer={
        <>
          Charter has no sign-up form: accounts come from an invitation, or from the one-time token
          that set this instance up. If you were expecting an invitation, ask whoever runs Charter
          for your team to send one.
        </>
      }
      lede="Sign in to pick up where you left off."
      title="Sign in"
    >
      {passwordWasReset ? (
        <div className="mb-5">
          <FormAlert tone="warn">
            Your new password is saved. Sign in with it to finish.
          </FormAlert>
        </div>
      ) : null}

      <form className="space-y-4" noValidate onSubmit={onSubmit}>
        {failure ? <FormAlert>{failure}</FormAlert> : null}

        <TextInput
          autoComplete="email"
          autoFocus
          inputMode="email"
          label="Email address"
          name="email"
          onChange={(event) => {
            setEmail(event.target.value);
          }}
          required
          type="email"
          value={email}
        />

        <TextInput
          autoComplete="current-password"
          label="Password"
          name="password"
          onChange={(event) => {
            setPassword(event.target.value);
          }}
          required
          type="password"
          value={password}
        />

        <Button block disabled={busy} size="lg" type="submit" variant="primary">
          {busy ? 'Signing you in…' : 'Sign in'}
        </Button>
      </form>

      <div className="mt-4">
        {selfServiceReset ? (
          <Link className="text-small text-accent underline underline-offset-4" to="/forgot-password">
            I have forgotten my password
          </Link>
        ) : (
          <p className="text-small text-ink-muted">
            {/* Change spec 001 part C.1: with no email configured there is no self-service reset,
                and saying who to ask beats a button that could never deliver anything. */}
            This Charter cannot send email, so there is no reset link. Ask whoever administers it to
            set you a new password.
          </p>
        )}
      </div>

      {redirects.length > 0 ? (
        <div className="mt-6">
          <div className="flex items-center gap-3">
            <span aria-hidden="true" className="bg-line h-px flex-1" />
            <span className="text-tiny text-ink-subtle">or</span>
            <span aria-hidden="true" className="bg-line h-px flex-1" />
          </div>

          <ul className="mt-4 space-y-2">
            {redirects.map((provider) => (
              <li key={provider.name}>
                {/* A full page navigation, not fetch: the provider hands the browser back to
                    /api/auth/{provider}/callback, which sets the cookie and redirects into the app. */}
                <ButtonLink block href={provider.startUrl ?? '#'} size="lg">
                  <Icon name={PROVIDER_ICONS[provider.name] ?? 'key'} size={16} />
                  Continue with {providerLabel(provider.name)}
                </ButtonLink>
              </li>
            ))}
          </ul>

          <p className="text-tiny text-ink-subtle mt-3">
            Signing in this way does not create an account. It matches you to one you already have.
          </p>
        </div>
      ) : null}
    </AuthPage>
  );
}
