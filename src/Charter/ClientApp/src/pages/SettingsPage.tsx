import type { TeachingLevel } from '@/api/types';
import { useViewer } from '@/app/viewer-context';
import { PageHeader } from '@/components/PageHeader';
import { ThemeToggle } from '@/components/ThemeToggle';
import { Card, SectionLabel } from '@/components/ui/Card';
import { cn } from '@/lib/cn';

/**
 * §13, calibration. The levels are "named for what the reader *wants*, never for what they lack —
 * never label a human 'beginner' in a UI their colleagues can see."
 *
 * Everything on this page writes through `PATCH /api/me/preferences` and comes back from
 * `GET /api/me`. No preference is held in the browser.
 */
const TEACHING: { value: TeachingLevel; label: string; description: string }[] = [
  {
    value: 'explain_everything',
    label: 'Explain everything',
    description: 'Define the words as they come up. Assume nothing.',
  },
  {
    value: 'skip_the_basics',
    label: 'Skip the basics',
    description: 'I know what a database and a deploy are. Tell me the reasoning.',
  },
  {
    value: 'just_the_decisions',
    label: 'Just the decisions',
    description: 'Trade-offs and alternatives only. Spare me the mechanics.',
  },
];

export function SettingsPage() {
  const { viewer, updatePreferences } = useViewer();

  return (
    <>
      <PageHeader description="These follow you to any device you sign in from." title="Settings" />

      <div className="max-w-2xl space-y-4">
        <Card className="px-4 py-5 sm:px-5">
          <SectionLabel>You</SectionLabel>
          <dl className="text-small mt-3 grid grid-cols-[7rem_1fr] gap-x-4 gap-y-1.5">
            <dt className="text-ink-subtle">Name</dt>
            <dd className="text-ink">{viewer.displayName}</dd>
            <dt className="text-ink-subtle">Email</dt>
            <dd className="text-ink">{viewer.email}</dd>
            <dt className="text-ink-subtle">Organisation</dt>
            <dd className="text-ink">{viewer.organization.name}</dd>
          </dl>
        </Card>

        <Card className="px-4 py-5 sm:px-5">
          <SectionLabel>Appearance</SectionLabel>
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <ThemeToggle />
            <p className="text-small text-ink-muted">
              Saved against your account, not this browser.
            </p>
          </div>
        </Card>

        <Card className="px-4 py-5 sm:px-5">
          <SectionLabel>How much explaining do you want?</SectionLabel>
          <p className="text-small text-ink-muted mt-1.5">
            Charter can explain what changed after each build. Change this whenever you like — it
            also quietly stops re-explaining things it has already told you.
          </p>
          <ul className="mt-3 space-y-2">
            {TEACHING.map((option) => {
              const selected = viewer.preferences.teachingLevel === option.value;
              return (
                <li key={option.value}>
                  <button
                    aria-pressed={selected}
                    className={cn(
                      'w-full rounded-control border px-3.5 py-3 text-left transition-colors',
                      selected
                        ? 'border-accent-line bg-accent-soft'
                        : 'border-line hover:border-line-strong',
                    )}
                    onClick={() => {
                      void updatePreferences({ teachingLevel: option.value });
                    }}
                    type="button"
                  >
                    <span className="text-ink block font-medium">{option.label}</span>
                    <span className="text-small text-ink-muted block">{option.description}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </Card>
      </div>
    </>
  );
}
