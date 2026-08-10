import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Icon, type IconName } from '@/components/ui/Icon';

/**
 * The bracket motif from the mark, at display size, with the empty state's icon where the teal dot
 * sits on the logo. It is the one place in the app that quotes the logo directly, which is why it
 * is reserved for empty states: they are the pages a new instance is entirely made of (§30.5), and
 * they should look deliberate rather than unfinished.
 */
function BracketFrame({ children }: { children: ReactNode }) {
  return (
    <span className="relative grid size-16 place-items-center">
      <svg
        aria-hidden="true"
        className="text-line-strong absolute inset-0"
        fill="none"
        viewBox="0 0 24 24"
      >
        <g stroke="currentColor" strokeLinecap="round" strokeWidth="1.5">
          <path d="M3 8V5a2 2 0 0 1 2-2h3" />
          <path d="M16 3h3a2 2 0 0 1 2 2v3" />
          <path d="M21 16v3a2 2 0 0 1-2 2h-3" />
          <path d="M8 21H5a2 2 0 0 1-2-2v-3" />
        </g>
      </svg>
      <span className="text-accent relative">{children}</span>
    </span>
  );
}

export interface EmptyStateProps {
  icon: IconName;
  title: string;
  /** One or two sentences. Say what goes here and why it is worth doing. */
  description: ReactNode;
  /**
   * §30.5: "a designed empty state that tells the user the *single* next action". One action, not
   * a menu. A secondary link is allowed only when it is a way out rather than a second job.
   */
  action?: ReactNode;
  secondary?: ReactNode;
  className?: string;
}

export function EmptyState({
  icon,
  title,
  description,
  action,
  secondary,
  className,
}: EmptyStateProps) {
  return (
    <div
      className={cn(
        'border-line rounded-panel mx-auto flex max-w-lg flex-col items-center border border-dashed px-6 py-12 text-center',
        className,
      )}
    >
      <BracketFrame>
        <Icon name={icon} size={20} />
      </BracketFrame>
      <h2 className="font-display text-title text-ink mt-4">{title}</h2>
      <p className="text-ink-muted mt-2 max-w-sm text-balance">{description}</p>
      {action ? <div className="mt-6">{action}</div> : null}
      {secondary ? <div className="text-small mt-3">{secondary}</div> : null}
    </div>
  );
}
