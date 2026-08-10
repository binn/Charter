import { cn } from '@/lib/cn';

/**
 * The Charter mark from `assets/charter-mark.svg`, redrawn so the brackets take `currentColor` and
 * the dot takes the accent token. That is the whole identity: four corner brackets — a scope, a
 * boundary, the thing Charter actually sells — around one teal dot for the change inside it.
 */
export function CharterMark({ size = 24, className }: { size?: number; className?: string }) {
  return (
    <svg
      aria-hidden="true"
      className={cn('text-ink shrink-0', className)}
      fill="none"
      height={size}
      viewBox="0 0 24 24"
      width={size}
      xmlns="http://www.w3.org/2000/svg"
    >
      <g stroke="currentColor" strokeLinecap="round" strokeWidth="2">
        <path d="M3 8V5a2 2 0 0 1 2-2h3" />
        <path d="M16 3h3a2 2 0 0 1 2 2v3" />
        <path d="M21 16v3a2 2 0 0 1-2 2h-3" />
        <path d="M8 21H5a2 2 0 0 1-2-2v-3" />
      </g>
      <circle className="fill-accent" cx="12" cy="12" r="2" />
    </svg>
  );
}

export function CharterWordmark({ className }: { className?: string }) {
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <CharterMark size={22} />
      <span className="font-display text-ink text-[1.0625rem] tracking-[-0.01em]">Charter</span>
    </span>
  );
}
