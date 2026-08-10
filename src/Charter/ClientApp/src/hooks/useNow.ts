import { useEffect, useState } from 'react';

/**
 * A ticking clock for elapsed timers and expiry countdowns.
 *
 * Pass `null` to stop ticking — a card whose artifact does not expire should not re-render every
 * second. The countdown on the artifact card is required to be visible from first render (§27.7),
 * so the initial value is a real timestamp rather than zero.
 */
export function useNow(intervalMs: number | null = 1_000): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (intervalMs === null) {
      return;
    }
    const timer = setInterval(() => {
      setNow(Date.now());
    }, intervalMs);
    return () => {
      clearInterval(timer);
    };
  }, [intervalMs]);

  return now;
}
