import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from '@/lib/cn';

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /**
   * Mobile: the verification artifact card goes full-bleed (§27.7). Everything else keeps its
   * gutters.
   */
  bleed?: boolean;
  children: ReactNode;
}

export function Card({ bleed = false, className, ...rest }: CardProps) {
  return (
    <div
      className={cn(
        'border-line bg-surface shadow-card border',
        // Full-bleed on a phone: the side borders would sit off-screen, so drop them there.
        bleed
          ? '-mx-4 rounded-none border-x-0 sm:mx-0 sm:rounded-card sm:border-x'
          : 'rounded-card',
        className,
      )}
      {...rest}
    />
  );
}

export function CardHeader({ className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('px-4 pt-4 sm:px-5 sm:pt-5', className)} {...rest} />;
}

export function CardBody({ className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('px-4 py-4 sm:px-5 sm:py-5', className)} {...rest} />;
}

export function CardFooter({ className, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={cn('border-line border-t px-4 py-3 sm:px-5 sm:py-4', className)} {...rest} />
  );
}

export function SectionLabel({ className, ...rest }: HTMLAttributes<HTMLHeadingElement>) {
  return (
    <h3
      className={cn('text-micro text-ink-subtle font-semibold tracking-[0.08em] uppercase', className)}
      {...rest}
    />
  );
}
