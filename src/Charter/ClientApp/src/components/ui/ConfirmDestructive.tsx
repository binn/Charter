import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Button } from '@/components/ui/Button';
import { Icon, type IconName } from '@/components/ui/Icon';
import { TextInput } from '@/components/ui/Field';
import { cn } from '@/lib/cn';

export interface ConfirmDestructiveProps {
  /** The resting control. */
  triggerLabel: string;
  triggerIcon?: IconName;
  /** Heading of the expanded panel. Name the thing that will happen, not "Are you sure?". */
  title: string;
  /** What this actually does, in plain language. One item per consequence, no euphemisms. */
  consequences: ReactNode[];
  /**
   * The exact string the person has to type. Use the name of the thing at risk — the branch, the
   * agent — so that confirming requires reading which one they are about to affect.
   */
  confirmPhrase: string;
  /** Label of the confirming button. A verb phrase, never "OK". */
  confirmLabel: string;
  /** What they are typing, for the field label: "branch name", "agent name". */
  phraseLabel: string;
  onConfirm: () => Promise<void> | void;
  className?: string;
}

/**
 * An inline, typed confirmation for an action that cannot be undone.
 *
 * **Not a modal**, for the same reason §30.2 refuses one for onboarding: a dialog that traps focus
 * is exactly wrong when the honest answer to "am I sure?" is "let me go and look at that branch
 * first". This expands in place, leaves the page readable behind it, and can be abandoned with
 * Escape.
 *
 * The typed phrase is deliberate friction. Charter has two genuinely destructive actions — taking
 * over a branch (§7.5) and revoking an agent (§33.3) — and both sit one click away from something
 * ordinary. Requiring the name to be typed makes the wrong one hard to do by accident, and makes it
 * impossible to do without reading which one it is.
 */
export function ConfirmDestructive({
  triggerLabel,
  triggerIcon,
  title,
  consequences,
  confirmPhrase,
  confirmLabel,
  phraseLabel,
  onConfirm,
  className,
}: ConfirmDestructiveProps) {
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const wasOpen = useRef(false);
  const panelId = useId();

  // Collapsing must put focus back on the trigger. Without this, dismissing the panel drops focus
  // to the document body and a keyboard user restarts from the top of the page.
  useEffect(() => {
    if (wasOpen.current && !open) {
      containerRef.current?.querySelector('button')?.focus();
    }
    wasOpen.current = open;
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Escape' && open) {
      event.stopPropagation();
      setOpen(false);
      setTyped('');
      setError(null);
    }
  };

  const matches = typed.trim() === confirmPhrase;

  return (
    <div className={cn('contents', className)} onKeyDown={onKeyDown} ref={containerRef}>
      {open ? (
        <div
          aria-labelledby={`${panelId}-title`}
          className="border-danger-line bg-danger-soft rounded-card w-full border p-4"
          role="group"
        >
          <h3 className="text-ink flex items-center gap-2 font-medium" id={`${panelId}-title`}>
            <Icon className="text-danger" name="alert" size={16} />
            {title}
          </h3>

          <ul className="text-small text-ink mt-3 space-y-1.5">
            {consequences.map((consequence, index) => (
              <li className="flex gap-2" key={index}>
                <Icon className="text-danger mt-1 shrink-0" name="arrowRight" size={12} />
                <span>{consequence}</span>
              </li>
            ))}
          </ul>

          <div className="mt-4 max-w-md">
            <TextInput
              // Correct use of autofocus: the field appeared because the person asked for it.
              autoFocus
              autoComplete="off"
              hint={
                <>
                  Type <span className="text-ink font-mono">{confirmPhrase}</span> to confirm.
                </>
              }
              label={`Confirm by typing the ${phraseLabel}`}
              onChange={(event) => {
                setTyped(event.target.value);
              }}
              spellCheck={false}
              value={typed}
            />
          </div>

          {error ? (
            <p className="text-small text-danger mt-2" role="alert">
              {error}
            </p>
          ) : null}

          <div className="mt-4 flex flex-wrap gap-2">
            <Button
              disabled={!matches || busy}
              onClick={() => {
                setBusy(true);
                setError(null);
                void Promise.resolve(onConfirm())
                  .then(() => {
                    setOpen(false);
                    setTyped('');
                  })
                  .catch((cause: unknown) => {
                    setError(cause instanceof Error ? cause.message : 'That did not go through.');
                  })
                  .finally(() => {
                    setBusy(false);
                  });
              }}
              variant="danger"
            >
              {busy ? 'Working…' : confirmLabel}
            </Button>
            <Button
              disabled={busy}
              onClick={() => {
                setOpen(false);
                setTyped('');
                setError(null);
              }}
              variant="ghost"
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <Button
          aria-expanded={false}
          onClick={() => {
            setOpen(true);
          }}
          variant="danger"
        >
          {triggerIcon ? <Icon name={triggerIcon} size={15} /> : null}
          {triggerLabel}
        </Button>
      )}
    </div>
  );
}
