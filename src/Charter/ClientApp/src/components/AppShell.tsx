import { NavLink, Outlet } from 'react-router';
import { useViewer } from '@/app/viewer-context';
import { CharterWordmark } from '@/components/CharterMark';
import { SourceFooter } from '@/components/SourceFooter';
import { ThemeToggle } from '@/components/ThemeToggle';
import { Icon, type IconName } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';

interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  visible: boolean;
}

/**
 * The shell. Navigation is built from `viewer.capabilities`, which the server computes — the client
 * never does role arithmetic of its own.
 *
 * Hiding a link is not an authorisation control and is not treated as one: the pages behind these
 * links get their data from endpoints that omit whatever the viewer may not see, so typing the URL
 * of a page you have no business on gets you an empty state, never someone else's data.
 */
export function AppShell() {
  const { viewer } = useViewer();
  const { capabilities } = viewer;

  const items = ([
    { to: '/requests', label: 'Requests', icon: 'list', visible: true },
    {
      to: '/projects',
      label: 'Projects',
      icon: 'package',
      visible: capabilities.canFileRequests,
    },
    {
      to: '/approvals',
      label: 'Approvals',
      icon: 'check',
      visible: capabilities.canApproveSpend,
    },
    { to: '/settings', label: 'Settings', icon: 'user', visible: true },
  ] satisfies NavItem[]).filter((item) => item.visible);

  return (
    <div className="flex min-h-dvh flex-col">
      <a
        className="bg-accent text-accent-ink focus:ring-focus sr-only rounded-control px-4 py-2 focus:not-sr-only focus:absolute focus:top-3 focus:left-3 focus:z-50"
        href="#main"
      >
        Skip to content
      </a>

      <header className="border-line bg-surface/85 sticky top-0 z-30 border-b backdrop-blur-sm">
        <div className="mx-auto flex h-14 max-w-6xl items-center gap-3 px-4 sm:px-6">
          <NavLink aria-label="Charter home" className="mr-1 shrink-0" to="/requests">
            <CharterWordmark />
          </NavLink>

          <span aria-hidden="true" className="bg-line hidden h-5 w-px sm:block" />

          <p className="text-small text-ink-muted hidden truncate sm:block">
            {viewer.organization.name}
          </p>

          <div className="ml-auto flex items-center gap-2">
            <ThemeToggle />
          </div>
        </div>

        {/* Primary navigation. On phones it is a horizontally scrollable strip under the bar
            rather than a hamburger — four destinations do not earn a menu. */}
        <nav aria-label="Main" className="mx-auto max-w-6xl px-2 sm:px-4">
          <ul className="flex gap-1 overflow-x-auto">
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
                  to={item.to}
                >
                  <Icon name={item.icon} size={15} />
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </header>

      {/* `tabIndex={-1}` so the skip link actually moves focus, not just the scroll position. */}
      <main
        className="mx-auto w-full max-w-6xl grow px-4 py-6 sm:px-6 sm:py-8"
        id="main"
        tabIndex={-1}
      >
        <Outlet />
      </main>

      <SourceFooter />
    </div>
  );
}
