import type { Iso8601 } from '@/api/types';
import { Icon } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';
import { describeDuration, expiryOf, formatDuration } from '@/lib/format';

export interface ExpiryCountdownProps {
  expiresAt: Iso8601 | undefined;
  now: number;
  className?: string;
}

/**
 * §27.7: "The countdown must be visible from first render, and expiry must be a designed state
 * rather than a 404."
 *
 * So this renders on the `ready` card too, not only once time is short — the whole point is that
 * someone reading the card at 9am knows the link dies at 2pm. Under an hour it turns amber *and*
 * gains a warning icon, because amber alone is not a signal to everyone who will read this.
 *
 * `role="timer"` deliberately does not announce every tick; the full duration is spelt out for
 * screen readers once, in words, alongside the abbreviated visual form.
 */
export function ExpiryCountdown({ expiresAt, now, className }: ExpiryCountdownProps) {
  const expiry = expiryOf(expiresAt, now);

  if (!expiry) {
    return null;
  }

  if (expiry.status === 'expired') {
    return (
      <span
        className={cn('text-tiny text-ink-subtle inline-flex items-center gap-1.5', className)}
      >
        <Icon name="clock" size={13} />
        Expired
      </span>
    );
  }

  const urgent = expiry.status === 'expiring';

  return (
    <span
      className={cn(
        'text-tiny inline-flex items-center gap-1.5 font-medium',
        urgent ? 'text-warn' : 'text-ink-muted',
        className,
      )}
      role="timer"
    >
      <Icon name={urgent ? 'alert' : 'clock'} size={13} />
      <span aria-hidden="true">expires in {formatDuration(expiry.remainingMs)}</span>
      <span className="sr-only">
        {urgent ? 'Expiring soon. ' : ''}
        Expires in about {describeDuration(expiry.remainingMs)}
      </span>
    </span>
  );
}
