import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Icon } from '@/components/ui/Icon';

export interface DisclosureProps {
  summary: ReactNode;
  children: ReactNode;
  defaultOpen?: boolean;
  className?: string;
  /** Right-aligned metadata that stays visible when collapsed. */
  aside?: ReactNode;
}

/**
 * Native `<details>` — keyboard-operable, findable by in-page search, and correct without a line of
 * JavaScript. The §27.7 Details block and the mobile "What to check" collapse both use it.
 */
export function Disclosure({ summary, children, defaultOpen, className, aside }: DisclosureProps) {
  return (
    <details className={cn('group', className)} open={defaultOpen ?? false}>
      <summary
        className={cn(
          'text-small text-ink-muted flex cursor-pointer list-none items-center gap-2 py-1.5',
          'hover:text-ink marker:content-none [&::-webkit-details-marker]:hidden',
        )}
      >
        <Icon
          className="transition-transform group-open:rotate-90"
          name="chevronRight"
          size={14}
        />
        <span className="font-medium">{summary}</span>
        {aside ? <span className="text-tiny text-ink-subtle ml-auto">{aside}</span> : null}
      </summary>
      <div className="pt-2 pb-1">{children}</div>
    </details>
  );
}
