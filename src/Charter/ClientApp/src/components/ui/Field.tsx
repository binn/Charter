import { useId, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from 'react';
import { cn } from '@/lib/cn';

const CONTROL =
  'w-full rounded-control border border-line-strong bg-surface px-3 py-2.5 text-base text-ink ' +
  'placeholder:text-ink-subtle transition-colors ' +
  'hover:border-ink-subtle focus:border-accent focus:outline-none ' +
  'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-focus ' +
  'disabled:opacity-50';

export interface TextAreaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  /** Hide the label visually but keep it for screen readers — used for chat composers. */
  hideLabel?: boolean;
  hint?: ReactNode;
}

export function TextArea({ label, hideLabel, hint, className, id, ...rest }: TextAreaProps) {
  const generated = useId();
  const fieldId = id ?? generated;
  const hintId = hint ? `${fieldId}-hint` : undefined;

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
        aria-describedby={hintId}
        className={cn(CONTROL, 'resize-y leading-relaxed', className)}
        id={fieldId}
        {...rest}
      />
      {hint ? (
        <p className="text-small text-ink-muted mt-1.5" id={hintId}>
          {hint}
        </p>
      ) : null}
    </div>
  );
}

export interface TextInputProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  hideLabel?: boolean;
  hint?: ReactNode;
}

export function TextInput({ label, hideLabel, hint, className, id, ...rest }: TextInputProps) {
  const generated = useId();
  const fieldId = id ?? generated;
  const hintId = hint ? `${fieldId}-hint` : undefined;

  return (
    <div className="w-full">
      <label
        className={cn('text-small text-ink mb-1.5 block font-medium', hideLabel && 'sr-only')}
        htmlFor={fieldId}
      >
        {label}
      </label>
      <input aria-describedby={hintId} className={cn(CONTROL, className)} id={fieldId} {...rest} />
      {hint ? (
        <p className="text-small text-ink-muted mt-1.5" id={hintId}>
          {hint}
        </p>
      ) : null}
    </div>
  );
}
