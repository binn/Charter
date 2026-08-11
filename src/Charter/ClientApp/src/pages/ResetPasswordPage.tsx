import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import { Button } from '@/components/ui/Button';
import { TextInput } from '@/components/ui/Field';
import { AuthPage, FormAlert } from '@/features/auth/AuthPage';

/**
 * `/reset-password?token=…` — the path the reset emails already point at.
 *
 * Reset links are short-lived and single-use, and they are invalidated by the password changing, so
 * "this link no longer works" is a routine outcome rather than an error state. The page says which
 * kind of no it was — expired, already used, unrecognised — and puts the way out one click away.
 *
 * **It does not end signed in, and that is deliberate on the server's part**: proving control of a
 * mailbox is enough to choose a password and not enough to be handed a signed-in browser. So the
 * last step is signing in with the password they just set, and the sign-in page says so rather than
 * leaving them wondering why they are being asked again.
 */
export function ResetPasswordPage() {
  const api = useApi();
  const navigate = useNavigate();
  const [search] = useSearchParams();
  const token = search.get('token') ?? '';

  const [password, setPassword] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (token === '') {
    return (
      <AuthPage
        footer={
          <Link className="text-accent underline underline-offset-4" to="/sign-in">
            Back to sign in
          </Link>
        }
        lede="A reset link carries a one-time code, and this one arrived without it."
        title="This link is incomplete"
      >
        <FormAlert>
          Open the email again and click the link rather than copying it. If that does not work,{' '}
          <Link className="underline underline-offset-4" to="/forgot-password">
            ask for a new link
          </Link>
          .
        </FormAlert>
      </AuthPage>
    );
  }

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .resetPassword({ token, password })
      .then(() => {
        void navigate('/sign-in?reset=done', { replace: true });
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
      lede="Pick a new one and you are done."
      title="Choose a new password"
    >
      <form className="space-y-4" noValidate onSubmit={onSubmit}>
        {failure ? (
          <FormAlert>
            {failure}{' '}
            <Link className="underline underline-offset-4" to="/forgot-password">
              Ask for a new link
            </Link>{' '}
            — it takes a moment and the old one cannot be revived.
          </FormAlert>
        ) : null}

        <TextInput
          autoComplete="new-password"
          autoFocus
          hint="At least 12 characters. Length is the only rule — a phrase you can remember beats a short jumble."
          label="New password"
          name="password"
          onChange={(event) => {
            setPassword(event.target.value);
          }}
          required
          type="password"
          value={password}
        />

        <Button block disabled={busy} size="lg" type="submit" variant="primary">
          {busy ? 'Saving your password…' : 'Save this password'}
        </Button>

        <p className="text-small text-ink-muted">
          You will sign in with it on the next screen. Charter never sends a password by email and
          nobody who administers it can read yours.
        </p>
      </form>
    </AuthPage>
  );
}
