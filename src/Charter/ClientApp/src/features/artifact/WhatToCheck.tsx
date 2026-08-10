import { useId, useState } from 'react';
import type { AcceptanceCriterion } from '@/api/types';
import { SectionLabel } from '@/components/ui/Card';
import { cn } from '@/lib/cn';

export interface WhatToCheckProps {
  criteria: AcceptanceCriterion[];
  className?: string;
}

/**
 * §11: "'What to check' beside the preview button, derived from acceptance criteria. Without it a
 * preview URL is a dead end."
 *
 * §27.7 is stricter about where the text comes from: the list is rendered **from
 * `acceptance_criteria` verbatim. It is not regenerated, because it is the contract the requester
 * approved.** So this component takes the criteria and prints `criterion.text` — it does not
 * summarise, re-order, re-word, or filter them, and there is deliberately no prop that would let a
 * caller do any of that.
 *
 * Ticking a box is a scratchpad for the person reading, held in component state for as long as the
 * card is open. It is not saved anywhere — there is no browser storage in this app, and a
 * half-ticked checklist is not something the server needs to know about.
 */
export function WhatToCheck({ criteria, className }: WhatToCheckProps) {
  const baseId = useId();
  const [ticked, setTicked] = useState<ReadonlySet<string>>(() => new Set());

  if (criteria.length === 0) {
    return null;
  }

  const toggle = (id: string) => {
    setTicked((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  return (
    <div className={className}>
      <SectionLabel>What to check</SectionLabel>
      <ul className="mt-2.5 space-y-1">
        {criteria.map((criterion) => {
          const id = `${baseId}-${criterion.id}`;
          const checked = ticked.has(criterion.id);
          return (
            <li key={criterion.id}>
              <label
                className="hover:bg-sunken group flex cursor-pointer items-start gap-2.5 rounded-[0.375rem] px-1.5 py-1.5 -mx-1.5 transition-colors"
                htmlFor={id}
              >
                <input
                  checked={checked}
                  className="accent-accent mt-[0.2rem] size-4 shrink-0 cursor-pointer"
                  id={id}
                  onChange={() => {
                    toggle(criterion.id);
                  }}
                  type="checkbox"
                />
                <span
                  className={cn(
                    'text-small transition-colors',
                    checked ? 'text-ink-subtle line-through' : 'text-ink',
                  )}
                >
                  {criterion.text}
                </span>
              </label>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
