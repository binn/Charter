import { cn } from '@/lib/cn';

export function Skeleton({ className }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn('bg-sunken block rounded-[0.375rem] animate-pulse-soft', className)}
    />
  );
}

/**
 * The `pending` body of the artifact card (§27.7): skeleton, elapsed timer, current milestone.
 * The timer and milestone live in the card; this is only the shape.
 */
export function SkeletonArtifactBody() {
  return (
    <div className="space-y-3">
      <Skeleton className="h-10 w-full" />
      <div className="flex gap-2">
        <Skeleton className="h-10 w-40" />
        <Skeleton className="h-10 w-10" />
      </div>
    </div>
  );
}
