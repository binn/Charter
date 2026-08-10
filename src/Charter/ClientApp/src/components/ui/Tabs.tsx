import { useCallback, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Icon, type IconName } from '@/components/ui/Icon';

export interface TabItem {
  id: string;
  label: string;
  icon?: IconName;
  /** Rendered after the label — a count, or a state word like "expired". */
  hint?: string;
}

export interface TabsProps {
  items: TabItem[];
  activeId: string;
  onChange: (id: string) => void;
  /** Announced by screen readers as the purpose of the tab list. */
  label: string;
  children: ReactNode;
  className?: string;
}

/**
 * WAI-ARIA tab pattern with manual activation and roving tabindex: arrow keys move focus, Enter or
 * Space selects. Manual rather than automatic activation because a tab panel here can be a whole
 * verification artifact, and arrowing through them should not fire off four renders.
 */
export function Tabs({ items, activeId, onChange, label, children, className }: TabsProps) {
  const baseId = useId();
  const listRef = useRef<HTMLDivElement>(null);

  const onKeyDown = useCallback(
    (event: KeyboardEvent<HTMLDivElement>) => {
      const keys = ['ArrowLeft', 'ArrowRight', 'Home', 'End'];
      if (!keys.includes(event.key)) {
        return;
      }
      event.preventDefault();

      const buttons = Array.from(
        listRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]') ?? [],
      );
      const current = buttons.findIndex((button) => button === document.activeElement);
      const from = current === -1 ? items.findIndex((item) => item.id === activeId) : current;

      let next = from;
      if (event.key === 'ArrowLeft') next = (from - 1 + buttons.length) % buttons.length;
      if (event.key === 'ArrowRight') next = (from + 1) % buttons.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = buttons.length - 1;

      buttons[next]?.focus();
    },
    [activeId, items],
  );

  return (
    <div className={className}>
      <div
        aria-label={label}
        className="border-line flex gap-1 overflow-x-auto border-b px-1"
        onKeyDown={onKeyDown}
        ref={listRef}
        role="tablist"
      >
        {items.map((item) => {
          const selected = item.id === activeId;
          return (
            <button
              aria-controls={`${baseId}-panel-${item.id}`}
              aria-selected={selected}
              className={cn(
                'text-small -mb-px inline-flex items-center gap-1.5 rounded-t-[0.5rem] border-b-2 px-3 py-2.5 font-medium whitespace-nowrap transition-colors',
                selected
                  ? 'border-accent text-ink'
                  : 'hover:text-ink hover:border-line-strong border-transparent text-ink-muted',
              )}
              id={`${baseId}-tab-${item.id}`}
              key={item.id}
              onClick={() => {
                onChange(item.id);
              }}
              role="tab"
              tabIndex={selected ? 0 : -1}
              type="button"
            >
              {item.icon ? <Icon name={item.icon} size={14} /> : null}
              {item.label}
              {item.hint ? (
                <span className="text-tiny text-ink-subtle font-normal">{item.hint}</span>
              ) : null}
            </button>
          );
        })}
      </div>

      <div
        aria-labelledby={`${baseId}-tab-${activeId}`}
        id={`${baseId}-panel-${activeId}`}
        role="tabpanel"
        tabIndex={0}
      >
        {children}
      </div>
    </div>
  );
}
