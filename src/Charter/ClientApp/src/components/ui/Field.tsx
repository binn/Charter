import { useId, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from 'react';
import { cn } from '@/lib/cn';

const CONTROL =
  'w-full rounded-control border border-line-strong bg-surface px-3 py-2.5 text-base text-ink ' +
  'placeholder:text-ink-subtle transition-colors ' +
  'hover:border-ink-subtle focus:border-accent focus:outline-none ' +
  'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-focus ' +
  'disabled:opacity-50';

/**
 * A field-level problem, wired to the control it belongs to.
 *
 * The association is the point, not the red text: `aria-describedby` on the input plus
 * `aria-invalid` is what makes a screen reader read the problem when focus lands on the field it
 * concerns. `role="alert"` makes it announced the moment it appears, which is what a form that has
 * just been rejected owes the person who submitted it.
 */
function describedBy(...ids: (string | undefined)[]): string | undefined {
  const present = ids.filter((id): id is string => id !== undefined);
  return present.length > 0 ? present.join(' ') : undefined;
}

export interface TextAreaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  /** Hide the label visually but keep it for screen readers — used for chat composers. */
  hideLabel?: boolean;
  hint?: ReactNode;
  /** What is wrong with what was typed. Announced, and read out with the field. */
  error?: string;
}

export function TextArea({ label, hideLabel, hint, error, className, id, ...rest }: TextAreaProps) {
  const generated = useId();
  const fieldId = id ?? generated;
  const hintId = hint ? `${fieldId}-hint` : undefined;
  const errorId = error ? `${fieldId}-error` : undefined;

  return (
    <div className="w-full">
      <label
        className={cn(
          'text-small text-ink mb-1.5 block font-medium',
          hideLabel && 'sr-only',
        )}
        htmlFor={fieldId}
      >
        {label}
      </label>
      <textarea
        aria-describedby={describedBy(hintId, errorId)}
        aria-invalid={error ? true : undefined}
        className={cn(CONTROL, 'resize-y leading-relaxed', error && 'border-danger', className)}
        id={fieldId}
        {...rest}
      />
      {hint ? (
        <p className="text-small text-ink-muted mt-1.5" id={hintId}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className="text-small text-danger mt-1.5" id={errorId} role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

export interface TextInputProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  hideLabel?: boolean;
  hint?: ReactNode;
  error?: string;
}

export function TextInput({
  label,
  hideLabel,
  hint,
  error,
  className,
  id,
  ...rest
}: TextInputProps) {
  const generated = useId();
  const fieldId = id ?? generated;
  const hintId = hint ? `${fieldId}-hint` : undefined;
  const errorId = error ? `${fieldId}-error` : undefined;

  return (
    <div className="w-full">
      <label
        className={cn('text-small text-ink mb-1.5 block font-medium', hideLabel && 'sr-only')}
        htmlFor={fieldId}
      >
        {label}
      </label>
      <input
        aria-describedby={describedBy(hintId, errorId)}
        aria-invalid={error ? true : undefined}
        className={cn(CONTROL, error && 'border-danger', className)}
        id={fieldId}
        {...rest}
      />
      {hint ? (
        <p className="text-small text-ink-muted mt-1.5" id={hintId}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className="text-small text-danger mt-1.5" id={errorId} role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}
