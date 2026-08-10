import { useRef, type KeyboardEvent } from 'react';
import { cn } from '@/lib/cn';

export interface Segment<T extends string> {
  value: T;
  label: string;
  /** Shown as a tooltip — the pane names need explaining once (§12). */
  hint?: string;
}

export interface SegmentedControlProps<T extends string> {
  segments: Segment<T>[];
  value: T;
  onChange: (value: T) => void;
  label: string;
  className?: string;
  size?: 'sm' | 'md';
}

/**
 * A radio group dressed as a segmented control. §12: "Mobile collapses to a segmented control, one
 * pane at a time." Radios rather than tabs because the panes are a persisted user preference, not a
 * transient view state, and radio semantics are what a screen reader user expects from a choice
 * that sticks.
 *
 * That semantic choice brings an obligation: a radio group is **one** tab stop, and arrow keys move
 * between the options, selecting as they go. Leaving each button separately tabbable would be the
 * easy version and the wrong one.
 */
export function SegmentedControl<T extends string>({
  segments,
  value,
  onChange,
  label,
  className,
  size = 'md',
}: SegmentedControlProps<T>) {
  const groupRef = useRef<HTMLDivElement>(null);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const keys = ['ArrowLeft', 'ArrowUp', 'ArrowRight', 'ArrowDown', 'Home', 'End'];
    if (!keys.includes(event.key)) {
      return;
    }
    event.preventDefault();

    const from = segments.findIndex((segment) => segment.value === value);
    let next: number;
    if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
      next = (from - 1 + segments.length) % segments.length;
    } else if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
      next = (from + 1) % segments.length;
    } else if (event.key === 'Home') {
      next = 0;
    } else {
      next = segments.length - 1;
    }

    const target = segments[next];
    if (target) {
      onChange(target.value);
      const radios = groupRef.current?.querySelectorAll<HTMLButtonElement>('[role="radio"]');
      radios?.[next]?.focus();
    }
  };

  return (
    <div
      aria-label={label}
      className={cn('border-line bg-sunken rounded-control inline-flex border p-0.5', className)}
      onKeyDown={onKeyDown}
      ref={groupRef}
      role="radiogroup"
    >
      {segments.map((segment) => {
        const selected = segment.value === value;
        return (
          <button
            aria-checked={selected}
            className={cn(
              'rounded-[0.375rem] font-medium transition-colors',
              size === 'sm' ? 'text-tiny px-2.5 py-1' : 'text-small px-3 py-1.5',
              selected ? 'bg-surface text-ink shadow-card' : 'hover:text-ink text-ink-muted',
            )}
            key={segment.value}
            onClick={() => {
              onChange(segment.value);
            }}
            role="radio"
            tabIndex={selected ? 0 : -1}
            title={segment.hint ?? undefined}
            type="button"
          >
            {segment.label}
          </button>
        );
      })}
    </div>
  );
}
