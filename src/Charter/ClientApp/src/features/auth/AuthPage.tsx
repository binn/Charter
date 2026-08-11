import type { ReactNode } from 'react';
import { CharterMark } from '@/components/CharterMark';
import { SourceFooter } from '@/components/SourceFooter';
import { Card } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';

/**
 * The frame every page you can reach without a session shares: first run, sign in, the two one-time
 * links.
 *
 * It is the same furniture as `WelcomePage` — mark, one column, the AGPL §13 footer — rather than
 * the app shell, because there is no navigation to draw for someone who is not signed in and a
 * disabled nav bar would only advertise pages they cannot reach.
 */
export function AuthPage({
  title,
  lede,
  children,
  footer,
}: {
  title: string;
  lede?: ReactNode;
  children: ReactNode;
  /** A way onwards for someone who cannot finish here. */
  footer?: ReactNode;
}) {
  return (
    <div className="flex min-h-dvh flex-col">
      <main className="mx-auto flex w-full max-w-lg grow flex-col justify-center px-4 py-10 sm:px-6">
        <div className="mb-6 flex items-center gap-2.5">
          <CharterMark size={26} />
          <span className="font-display text-title text-ink">Charter</span>
        </div>

        <h1 className="font-display text-hero text-ink">{title}</h1>
        {lede ? <div className="text-ink-muted text-lead mt-3">{lede}</div> : null}

        <div className="mt-7">{children}</div>

        {footer ? <div className="text-small text-ink-muted mt-6">{footer}</div> : null}
      </main>

      <SourceFooter />
    </div>
  );
}

/**
 * A refusal that belongs to the whole form rather than to one field — a wrong token, a throttle, a
 * spent link.
 *
 * `role="alert"` because it appears in response to a submission the person is waiting on, and the
 * icon is there because §27.7's rule generalises: never colour alone.
 */
export function FormAlert({ children, tone = 'bad' }: { children: ReactNode; tone?: 'bad' | 'warn' }) {
  return (
    <Card
      className={
        tone === 'bad'
          ? 'border-danger-line bg-danger-soft px-4 py-3'
          : 'border-warn-line bg-warn-soft px-4 py-3'
      }
      role="alert"
    >
      <p
        className={
          tone === 'bad'
            ? 'text-small text-danger flex items-start gap-2'
            : 'text-small text-warn flex items-start gap-2'
        }
      >
        <Icon className="mt-0.5" name="alert" size={15} />
        <span>{children}</span>
      </p>
    </Card>
  );
}
