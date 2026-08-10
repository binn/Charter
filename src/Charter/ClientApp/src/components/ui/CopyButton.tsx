import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, type ButtonSize, type ButtonVariant } from '@/components/ui/Button';
import { Icon } from '@/components/ui/Icon';

export interface CopyButtonProps {
  value: string;
  /** What is being copied, for the accessible name: "Copy preview link". */
  label: string;
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Show the label as visible text rather than only as an accessible name. */
  showLabel?: boolean;
  className?: string;
}

/**
 * Clipboard only — `navigator.clipboard.writeText` writes to the system clipboard, which is not
 * browser storage and not a preference. Nothing here persists.
 *
 * The confirmation goes through an `aria-live` region as well as the icon swap, because a purely
 * visual tick tells a screen reader user nothing about whether the copy worked.
 */
export function CopyButton({
  value,
  label,
  variant = 'secondary',
  size = 'md',
  showLabel = false,
  className,
}: CopyButtonProps) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (timer.current) {
        clearTimeout(timer.current);
      }
    },
    [],
  );

  const copy = useCallback(() => {
    const write = navigator.clipboard?.writeText(value);
    if (!write) {
      setState('failed');
      return;
    }
    write
      .then(() => {
        setState('copied');
      })
      .catch(() => {
        setState('failed');
      })
      .finally(() => {
        if (timer.current) {
          clearTimeout(timer.current);
        }
        timer.current = setTimeout(() => {
          setState('idle');
        }, 2_000);
      });
  }, [value]);

  return (
    <>
      <Button
        aria-label={showLabel ? undefined : label}
        className={className}
        onClick={copy}
        size={size}
        title={showLabel ? undefined : label}
        variant={variant}
      >
        <Icon name={state === 'copied' ? 'check' : 'copy'} size={15} />
        {showLabel ? (state === 'copied' ? 'Copied' : label) : null}
      </Button>
      <span aria-live="polite" className="sr-only">
        {state === 'copied' ? `${label}: copied` : null}
        {state === 'failed' ? `${label}: could not copy` : null}
      </span>
    </>
  );
}
