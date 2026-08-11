import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import { Button } from '@/components/ui/Button';
import { TextInput } from '@/components/ui/Field';
import { AuthPage, FormAlert } from '@/features/auth/AuthPage';

/**
 * Asking for a reset link.
 *
 * The acknowledgement is identical for an address with an account and for one without, and it comes
 * from the server rather than being composed here. Anybody can type anybody's address into this
 * form, so a page that said "no such account" would answer "does this person work here" for free —
 * the same enumeration rule that governs the sign-in refusal.
 */
export function ForgotPasswordPage() {
  const api = useApi();
  const [email, setEmail] = useState('');
  const [acknowledgement, setAcknowledgement] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .forgotPassword(email.trim())
      .then((result) => {
        setAcknowledgement(result.message);
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

  if (acknowledgement !== null) {
    return (
      <AuthPage
        footer={
          <Link className="text-accent underline underline-offset-4" to="/sign-in">
            Back to sign in
          </Link>
        }
        lede={acknowledgement}
        title="Check your email"
      >
        <p className="text-small text-ink-muted">
          The link works once and expires. If it has already expired by the time you click it, come
          back here and ask for another.
        </p>
      </AuthPage>
    );
  }

  return (
    <AuthPage
      footer={
        <Link className="text-accent underline underline-offset-4" to="/sign-in">
          Back to sign in
        </Link>
      }
      lede="Tell Charter your email address and it will send you a link to set a new password."
      title="Forgotten password"
    >
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

        <Button block disabled={busy} size="lg" type="submit" variant="primary">
          {busy ? 'Sending…' : 'Send me a link'}
        </Button>
      </form>
    </AuthPage>
  );
}
