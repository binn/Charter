import { useState } from 'react';
import { NavLink, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import type { TeachingLevel } from '@/api/types';
import { useViewer } from '@/app/viewer-context';
import { PageHeader } from '@/components/PageHeader';
import { ThemeToggle } from '@/components/ThemeToggle';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';

/**
 * Sub-navigation for the settings area, shared by every page in it.
 *
 * Runners appears only for an admin — but as ever, that is a navigation affordance and not the
 * control: `GET /api/runners` refuses anyone else, so typing the URL gets an error state rather
 * than a list of someone's hardware.
 */
export function SettingsNav() {
  const { viewer } = useViewer();

  const items = [
    { to: '/settings', label: 'Preferences', icon: 'user' as const, end: true, visible: true },
    {
      to: '/settings/repositories',
      label: 'Repositories',
      icon: 'package' as const,
      end: false,
      // §9 is engineer work; `GET /api/repos` refuses anybody else, so this link is an affordance
      // and not the control.
      visible: viewer.capabilities.canReadRepos,
    },
    {
      to: '/settings/runners',
      label: 'Runners',
      icon: 'server' as const,
      end: false,
      visible: viewer.capabilities.canAdminister,
    },
    {
      to: '/settings/members',
      label: 'Members',
      icon: 'user' as const,
      end: false,
      // §7.1 puts members, roles and the audit log in the administrator's column. `GET /api/members`
      // and `GET /api/audit` refuse everybody else, so these two links are affordances, not controls.
      visible: viewer.capabilities.canAdminister,
    },
    {
      to: '/settings/audit',
      label: 'Audit log',
      icon: 'list' as const,
      end: false,
      visible: viewer.capabilities.canAdminister,
    },
  ].filter((item) => item.visible);

  if (items.length < 2) {
    return null;
  }

  return (
    <nav aria-label="Settings" className="border-line -mt-2 mb-2 border-b">
      <ul className="flex gap-1">
        {items.map((item) => (
          <li key={item.to}>
            <NavLink
              className={({ isActive }) =>
                cn(
                  'text-small -mb-px inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 font-medium whitespace-nowrap transition-colors',
                  isActive
                    ? 'border-accent text-ink'
                    : 'hover:text-ink hover:border-line-strong border-transparent text-ink-muted',
                )
              }
              end={item.end}
              to={item.to}
            >
              <Icon name={item.icon} size={15} />
              {item.label}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  );
}

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
  const api = useApi();
  const navigate = useNavigate();
  const [signingOut, setSigningOut] = useState(false);

  return (
    <>
      <PageHeader description="These follow you to any device you sign in from." title="Settings" />

      <SettingsNav />

      <div className="mt-6 max-w-2xl space-y-4">
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

          {/* The session is an HTTP-only cookie; only the server can end it, which is why this is a
              request rather than something the page clears for itself. */}
          <Button
            className="mt-4"
            disabled={signingOut}
            onClick={() => {
              setSigningOut(true);
              api
                .signOut()
                .finally(() => {
                  void navigate('/sign-in', { replace: true });
                })
                .catch(() => {
                  setSigningOut(false);
                });
            }}
            size="sm"
          >
            {signingOut ? 'Signing you out…' : 'Sign out'}
          </Button>
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
