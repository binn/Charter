import type { ButtonHTMLAttributes, AnchorHTMLAttributes, ReactNode } from 'react';
import { cn } from '@/lib/cn';

/**
 * shadcn/ui-shaped, hand-rolled. Same idea — a variant table over one base class string, `asChild`
 * replaced by an explicit `<ButtonLink>` because Radix's Slot is the only part of that pattern that
 * needs a dependency.
 */

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'quiet';
export type ButtonSize = 'sm' | 'md' | 'lg';

const BASE =
  'inline-flex items-center justify-center gap-2 rounded-control font-medium whitespace-nowrap ' +
  'transition-[background-color,border-color,color,box-shadow] duration-150 ' +
  'disabled:pointer-events-none disabled:opacity-50 ' +
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus';

const VARIANTS: Record<ButtonVariant, string> = {
  primary: 'bg-accent text-accent-ink hover:bg-accent-hover shadow-card',
  secondary:
    'border border-line-strong bg-surface text-ink hover:border-ink-subtle hover:bg-sunken',
  ghost: 'text-ink-muted hover:bg-sunken hover:text-ink',
  danger: 'border border-danger-line bg-danger-soft text-danger hover:border-danger',
  quiet: 'text-accent underline underline-offset-4 decoration-accent-line hover:decoration-accent',
};

const SIZES: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-small',
  md: 'h-10 px-4 text-base',
  lg: 'h-12 px-5 text-lead',
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Full width on small screens, auto above `sm`. Used for pinned mobile actions. */
  block?: boolean;
  children: ReactNode;
}

export function Button({
  variant = 'secondary',
  size = 'md',
  block = false,
  className,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      className={cn(BASE, VARIANTS[variant], SIZES[size], block && 'w-full', className)}
      type={type}
      {...rest}
    />
  );
}

export interface ButtonLinkProps extends AnchorHTMLAttributes<HTMLAnchorElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  block?: boolean;
  children: ReactNode;
}

export function ButtonLink({
  variant = 'secondary',
  size = 'md',
  block = false,
  className,
  ...rest
}: ButtonLinkProps) {
  return (
    <a
      className={cn(BASE, VARIANTS[variant], SIZES[size], block && 'w-full', className)}
      {...rest}
    />
  );
}
