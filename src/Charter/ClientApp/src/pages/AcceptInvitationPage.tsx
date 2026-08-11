import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import { Button } from '@/components/ui/Button';
import { Icon } from '@/components/ui/Icon';
import { TextInput } from '@/components/ui/Field';
import { AuthPage, FormAlert } from '@/features/auth/AuthPage';

/**
 * `/accept-invitation?token=…` — the path the invitation emails already point at (§30.2, §21).
 *
 * Accepting an invitation is what *creates* an account; there is no sign-up form anywhere in
 * Charter. So this page is the first thing a new colleague ever sees of the product, and the
 * unhappy path is the one that gets hit: links get forwarded, sat on for a fortnight, clicked
 * twice, or opened after somebody withdrew them.
 *
 * When that happens the page says which of those it was, in the server's own words, and gives a way
 * forward — ask for a new one, or sign in if the account already exists. A bare "invalid token" on
 * somebody's first contact with a tool their colleague told them to try is where adoption stops.
 */
export function AcceptInvitationPage() {
  const api = useApi();
  const navigate = useNavigate();
  const [search] = useSearchParams();
  const token = search.get('token') ?? '';

  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (token === '') {
    return (
      <AuthPage
        footer={
          <Link className="text-accent underline underline-offset-4" to="/sign-in">
            I already have an account
          </Link>
        }
        lede="An invitation link carries a one-time code, and this one arrived without it."
        title="This link is incomplete"
      >
        <FormAlert>
          Some email apps shorten long links. Open the invitation email again and click the link
          rather than copying it — or ask whoever invited you to send a fresh one.
        </FormAlert>
      </AuthPage>
    );
  }

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .acceptInvitation({ token, displayName: displayName.trim(), password })
      .then(() => {
        // The response set the session cookie: they are signed in, and §30.4's three screens are
        // what they land on if they are a requester.
        void navigate('/requests', { replace: true });
      })
      .catch((error: unknown) => {
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
          Charter is a place to ask for changes to software in plain language. Somebody at your
          organisation runs it; nothing you do in it can be deployed without a person reviewing it
          first.
        </>
      }
      lede="Choose a password and you are in. There is nothing to install."
      title="You have been invited to Charter"
    >
      <form className="space-y-4" noValidate onSubmit={onSubmit}>
        {failure ? (
          <FormAlert>
            {failure}{' '}
            <Link className="underline underline-offset-4" to="/sign-in">
              If you already have an account, sign in instead.
            </Link>
          </FormAlert>
        ) : null}

        <TextInput
          autoComplete="name"
          autoFocus
          hint="What your colleagues will see next to your requests."
          label="Your name"
          name="displayName"
          onChange={(event) => {
            setDisplayName(event.target.value);
          }}
          required
          value={displayName}
        />

        <TextInput
          autoComplete="new-password"
          hint="At least 12 characters. Length is the only rule."
          label="Choose a password"
          name="password"
          onChange={(event) => {
            setPassword(event.target.value);
          }}
          required
          type="password"
          value={password}
        />

        <Button block disabled={busy} size="lg" type="submit" variant="primary">
          {busy ? 'Setting up your account…' : 'Accept and sign in'}
          {busy ? null : <Icon name="arrowRight" size={17} />}
        </Button>
      </form>
    </AuthPage>
  );
}
