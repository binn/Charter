import { useCallback, useState, type FormEvent } from 'react';
import { Navigate, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { TextInput } from '@/components/ui/Field';
import { FullPageLoading } from '@/components/FullPageState';
import { AuthPage, FormAlert } from '@/features/auth/AuthPage';
import { useAsync } from '@/hooks/useAsync';

/**
 * §30.1, instance first run. **Security-critical.**
 *
 * "A self-hosted app that boots with open registration gets hijacked by whoever finds it first." So
 * there is no open form here: the instance printed a one-time token to stdout on boot, this page
 * redeems it, and redeeming it creates exactly one admin and ends setup mode permanently.
 *
 * The single most likely place for somebody to get stuck is not the form — it is *where the token
 * comes from*. Nothing in a browser suggests "read your container logs", so that instruction is the
 * loudest thing on the page, with the two commands that actually produce it. A page that asked for a
 * token without saying where to find one would be a dead end wearing a text field.
 *
 * A refused token leaves everything typed in place and says what to do next, because the likeliest
 * cause is a copied fragment or a container that has since restarted with a new token.
 */
export function SetupPage() {
  const api = useApi();
  const navigate = useNavigate();
  const load = useCallback((signal: AbortSignal) => api.getSetupStatus(signal), [api]);
  const status = useAsync(load);

  const [token, setToken] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [organizationName, setOrganizationName] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (status.status === 'loading') {
    return <FullPageLoading label="Checking whether this instance has been set up" />;
  }

  // Setup mode ends permanently and cannot be re-entered while a user exists (§30.1). Somebody who
  // bookmarked this page after the fact gets the sign-in form rather than a second chance at it.
  if (status.status === 'ready' && !status.data.setupRequired) {
    return <Navigate replace to="/sign-in" />;
  }

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .completeSetup({
        token: token.trim(),
        email: email.trim(),
        displayName: displayName.trim(),
        password,
        ...(organizationName.trim() === '' ? {} : { organizationName: organizationName.trim() }),
      })
      .then(() => {
        // The response set the session cookie. Nothing is held here — the app reloads the viewer
        // from the server on the other side of this navigation.
        void navigate('/requests', { replace: true });
      })
      .catch((error: unknown) => {
        setFailure(
          error instanceof ApiError
            ? error.message
            : 'Charter could not reach the server. Check that the container is still running, then try again.',
        );
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <AuthPage
      lede={
        <>
          This instance has no accounts on it yet. The first one is created with a one-time token,
          not a default password — so whoever finds this page cannot claim it, and whoever runs the
          server can.
        </>
      }
      title="Set up this Charter"
    >
      <Card className="border-accent-line bg-accent-soft px-4 py-4">
        <h2 className="text-small text-accent-soft-ink flex items-center gap-2 font-medium">
          <Icon name="terminal" size={15} />
          The token is in the server’s logs
        </h2>
        <p className="text-small text-accent-soft-ink mt-2">
          Charter printed it to standard output the first time it started. It is not emailed, not
          shown anywhere else, and it expires — so read it now:
        </p>
        <pre className="text-tiny text-ink bg-surface border-line rounded-control mt-3 overflow-x-auto border px-3 py-2 font-mono">
          <code>{'docker compose logs charter | grep -i "setup token"'}</code>
        </pre>
        <p className="text-tiny text-accent-soft-ink mt-2">
          Running it another way? It is the line beginning <strong>Charter setup token:</strong> in
          whatever you use to read this container’s output — <code>docker logs</code>,{' '}
          <code>kubectl logs</code>, your platform’s log viewer, or the terminal you started it in.
        </p>
      </Card>

      <form className="mt-5 space-y-4" noValidate onSubmit={onSubmit}>
        {failure ? (
          <FormAlert>
            {failure} Nothing you typed has been lost — correct the token and submit again. If the
            container has restarted since you copied it, the token in the logs is a new one.
          </FormAlert>
        ) : null}

        <TextInput
          autoComplete="off"
          autoFocus
          hint="Paste it exactly, including any prefix."
          label="Setup token"
          name="token"
          onChange={(event) => {
            setToken(event.target.value);
          }}
          required
          spellCheck={false}
          value={token}
        />

        <TextInput
          autoComplete="name"
          label="Your name"
          name="displayName"
          onChange={(event) => {
            setDisplayName(event.target.value);
          }}
          required
          value={displayName}
        />

        <TextInput
          autoComplete="email"
          inputMode="email"
          label="Your email address"
          name="email"
          onChange={(event) => {
            setEmail(event.target.value);
          }}
          required
          type="email"
          value={email}
        />

        <TextInput
          autoComplete="new-password"
          hint="At least 12 characters. Length is the only rule — a phrase you can remember beats a short jumble."
          label="Choose a password"
          name="password"
          onChange={(event) => {
            setPassword(event.target.value);
          }}
          required
          type="password"
          value={password}
        />

        <TextInput
          hint="Optional. It appears on every page and in the emails Charter sends, and you can set it later."
          label="Organisation name"
          name="organizationName"
          onChange={(event) => {
            setOrganizationName(event.target.value);
          }}
          value={organizationName}
        />

        <div className="pt-1">
          <Button block disabled={busy} size="lg" type="submit" variant="primary">
            {busy ? 'Creating your account…' : 'Create the first account'}
            {busy ? null : <Icon name="arrowRight" size={17} />}
          </Button>
          <p className="text-small text-ink-muted mt-2">
            This creates one administrator and closes setup for good. Everyone after you joins by
            invitation.
          </p>
        </div>
      </form>
    </AuthPage>
  );
}
