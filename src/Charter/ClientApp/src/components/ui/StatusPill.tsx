import { cn } from '@/lib/cn';
import { Icon, type IconName } from '@/components/ui/Icon';

export type Tone = 'neutral' | 'active' | 'attention' | 'good' | 'bad' | 'warn';

const TONES: Record<Tone, string> = {
  neutral: 'border-line bg-sunken text-ink-muted',
  active: 'border-accent-line bg-accent-soft text-accent-soft-ink',
  attention: 'border-accent-line bg-accent-soft text-accent-soft-ink',
  good: 'border-ok-line bg-ok-soft text-ok',
  warn: 'border-warn-line bg-warn-soft text-warn',
  bad: 'border-danger-line bg-danger-soft text-danger',
};

const DOTS: Record<Tone, string> = {
  neutral: 'bg-ink-subtle',
  active: 'bg-accent',
  attention: 'bg-accent',
  good: 'bg-ok',
  warn: 'bg-warn',
  bad: 'bg-danger',
};

export interface StatusPillProps {
  tone: Tone;
  children: string;
  /**
   * Required for any pass/fail meaning. §27.7: "Pass/fail must not rely on colour alone — pair
   * every state with an icon and a text label." The label is `children`; this is the icon. Purely
   * informational pills (a project name, a count) may omit it.
   */
  icon?: IconName;
  /** Slow pulse on the dot, for a live state. Suppressed under `prefers-reduced-motion`. */
  pulse?: boolean;
  className?: string;
}

export function StatusPill({ tone, children, icon, pulse = false, className }: StatusPillProps) {
  return (
    <span
      className={cn(
        'text-tiny inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 font-medium',
        TONES[tone],
        className,
      )}
    >
      {icon ? (
        <Icon name={icon} size={13} />
      ) : (
        <span
          aria-hidden="true"
          className={cn('size-1.5 rounded-full', DOTS[tone], pulse && 'animate-pulse-soft')}
        />
      )}
      {children}
    </span>
  );
}
