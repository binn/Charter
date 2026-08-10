import { useRef, type KeyboardEvent } from 'react';
import { useViewer } from '@/app/viewer-context';
import type { ThemePreference } from '@/api/types';
import { Icon, type IconName } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';

const OPTIONS: { value: ThemePreference; label: string; icon: IconName }[] = [
  { value: 'light', label: 'Light', icon: 'sun' },
  { value: 'dark', label: 'Dark', icon: 'moon' },
  { value: 'system', label: 'Match my device', icon: 'monitor' },
];

/**
 * Writes straight through to `PATCH /api/me/preferences`. Nothing is stored in the browser, so the
 * choice follows the person to their phone and cannot drift out of step with what the server
 * believes about them (AGENTS.md, "No browser storage APIs in the frontend").
 *
 * One tab stop, arrow keys between the three options — see `SegmentedControl` for why.
 */
export function ThemeToggle({ className }: { className?: string }) {
  const { viewer, updatePreferences } = useViewer();
  const current = viewer.preferences.theme;
  const groupRef = useRef<HTMLDivElement>(null);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      return;
    }
    event.preventDefault();
    const from = OPTIONS.findIndex((option) => option.value === current);
    const step = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 1;
    const next = (from + step + OPTIONS.length) % OPTIONS.length;
    const target = OPTIONS[next];
    if (target) {
      void updatePreferences({ theme: target.value });
      groupRef.current?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
    }
  };

  return (
    <div
      aria-label="Colour theme"
      className={cn('border-line bg-sunken rounded-control inline-flex border p-0.5', className)}
      onKeyDown={onKeyDown}
      ref={groupRef}
      role="radiogroup"
    >
      {OPTIONS.map((option) => {
        const selected = option.value === current;
        return (
          <button
            aria-checked={selected}
            aria-label={option.label}
            className={cn(
              'grid size-7 place-items-center rounded-[0.375rem] transition-colors',
              selected ? 'bg-surface text-ink shadow-card' : 'text-ink-subtle hover:text-ink',
            )}
            key={option.value}
            onClick={() => {
              void updatePreferences({ theme: option.value });
            }}
            role="radio"
            tabIndex={selected ? 0 : -1}
            title={option.label}
            type="button"
          >
            <Icon name={option.icon} size={15} />
          </button>
        );
      })}
    </div>
  );
}
