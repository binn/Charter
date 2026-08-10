import { CharterMark } from '@/components/CharterMark';

export function FullPageLoading({ label }: { label: string }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-4 px-6">
      <CharterMark className="animate-pulse-soft" size={32} />
      <p aria-live="polite" className="text-ink-muted text-small">
        {label}
        <span aria-hidden="true">&hellip;</span>
      </p>
    </div>
  );
}

export function FullPageError({ title, detail }: { title: string; detail: string }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-3 px-6 text-center">
      <CharterMark size={32} />
      <h1 className="font-display text-title text-ink">{title}</h1>
      <p className="text-ink-muted max-w-sm text-balance">{detail}</p>
    </div>
  );
}
