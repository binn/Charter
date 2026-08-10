import type { Iso8601 } from '@/api/types';

/**
 * Duration and size formatting.
 *
 * The load-bearing rule here is §6: **never show an ETA**. Every function below looks backwards
 * from a start, or forwards to a hard expiry the server has already committed to. None of them
 * predicts when anything will finish, and there is deliberately no helper that could.
 */

const SECOND = 1_000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** "12m", "1h 04m", "3d 2h". Elapsed time, past tense, never an estimate. */
export function formatDuration(ms: number): string {
  const clamped = Math.max(0, ms);

  if (clamped < MINUTE) {
    return `${Math.floor(clamped / SECOND)}s`;
  }
  if (clamped < HOUR) {
    return `${Math.floor(clamped / MINUTE)}m`;
  }
  if (clamped < DAY) {
    const hours = Math.floor(clamped / HOUR);
    const minutes = Math.floor((clamped % HOUR) / MINUTE);
    return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  }
  const days = Math.floor(clamped / DAY);
  const hours = Math.floor((clamped % DAY) / HOUR);
  return `${days}d ${hours}h`;
}

/** Spelt out for screen readers, where "1h 04m" is read as gibberish. */
export function describeDuration(ms: number): string {
  const clamped = Math.max(0, ms);
  const parts: string[] = [];
  const days = Math.floor(clamped / DAY);
  const hours = Math.floor((clamped % DAY) / HOUR);
  const minutes = Math.floor((clamped % HOUR) / MINUTE);

  if (days) parts.push(`${days} day${days === 1 ? '' : 's'}`);
  if (hours) parts.push(`${hours} hour${hours === 1 ? '' : 's'}`);
  if (minutes || parts.length === 0) parts.push(`${minutes} minute${minutes === 1 ? '' : 's'}`);

  return parts.join(' ');
}

export function elapsedSince(startedAt: Iso8601, now: number): number {
  return now - Date.parse(startedAt);
}

export type ExpiryStatus = 'expired' | 'expiring' | 'ok';

export interface Expiry {
  status: ExpiryStatus;
  remainingMs: number;
}

/** §27.7: under an hour is `expiring` and the countdown turns amber. */
export function expiryOf(expiresAt: Iso8601 | undefined, now: number): Expiry | null {
  if (!expiresAt) {
    return null;
  }
  const remainingMs = Date.parse(expiresAt) - now;
  if (Number.isNaN(remainingMs)) {
    return null;
  }
  if (remainingMs <= 0) {
    return { status: 'expired', remainingMs: 0 };
  }
  return { status: remainingMs < HOUR ? 'expiring' : 'ok', remainingMs };
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

const RELATIVE = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

/** "3 minutes ago", "yesterday". Used on list rows, never on anything in flight. */
export function formatRelative(timestamp: Iso8601, now: number): string {
  const delta = Date.parse(timestamp) - now;
  const abs = Math.abs(delta);

  if (abs < MINUTE) return RELATIVE.format(Math.round(delta / SECOND), 'second');
  if (abs < HOUR) return RELATIVE.format(Math.round(delta / MINUTE), 'minute');
  if (abs < DAY) return RELATIVE.format(Math.round(delta / HOUR), 'hour');
  if (abs < 30 * DAY) return RELATIVE.format(Math.round(delta / DAY), 'day');
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(timestamp));
}

export function formatDateTime(timestamp: Iso8601): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(timestamp));
}
