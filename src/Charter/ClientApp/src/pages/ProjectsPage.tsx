import { useCallback } from 'react';
import { Link, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import { useViewer } from '@/app/viewer-context';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { Disclosure } from '@/components/ui/Disclosure';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { Skeleton } from '@/components/ui/Skeleton';
import { useAsync } from '@/hooks/useAsync';

/**
 * What the viewer may file against.
 *
 * §7.3: repo scope is **deny by default**, and §9: "a repo is invisible to requesters until the
 * smoke test passes". Both of those are enforced by the list simply not containing the project —
 * there is no greyed-out row explaining what you cannot have.
 */
export function ProjectsPage() {
  const api = useApi();
  const navigate = useNavigate();
  const { viewer } = useViewer();
  const load = useCallback((signal: AbortSignal) => api.listProjects(signal), [api]);
  const state = useAsync(load);

  return (
    <>
      <PageHeader
        actions={
          // §30.2's checklist sends "connect your first repository" here, and §9 is where it
          // actually happens. An affordance, not a permission: `GET /api/repos` refuses anyone else.
          viewer.capabilities.canReadRepos ? (
            <Button
              onClick={() => {
                void navigate('/settings/repositories');
              }}
            >
              <Icon name="package" size={15} />
              Connect a repository
            </Button>
          ) : undefined
        }
        description="The things you can ask for changes to."
        title="Projects"
      />

      {state.status === 'loading' ? (
        <Skeleton className="h-40 w-full" />
      ) : state.status === 'error' ? (
        <Card className="px-4 py-5">
          <p className="text-ink">Charter could not load your projects just now.</p>
          <Button className="mt-3" onClick={state.reload}>
            Try again
          </Button>
        </Card>
      ) : state.data.length === 0 ? (
        <EmptyState
          description="Whoever set up Charter for your team decides which projects you can file against. Ask them to add you and this page fills in."
          icon="package"
          secondary={
            <Link className="text-accent underline underline-offset-4" to="/requests">
              Back to requests
            </Link>
          }
          title="Nothing shared with you yet"
        />
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2">
          {state.data.map((project) => (
            <li key={project.id}>
              <Card className="flex h-full flex-col px-4 py-4">
                <h2 className="font-display text-title text-ink">{project.name}</h2>
                {project.description ? (
                  <p className="text-small text-ink-muted mt-1">{project.description}</p>
                ) : null}

                {project.primerMd ? (
                  <div className="mt-3">
                    <Disclosure summary="How this one is put together">
                      <div className="text-small text-ink-muted">
                        <Markdown>{project.primerMd}</Markdown>
                      </div>
                    </Disclosure>
                  </div>
                ) : null}

                <div className="mt-4 pt-1">
                  <Button
                    onClick={() => {
                      void navigate('/requests/new');
                    }}
                    size="sm"
                  >
                    <Icon name="plus" size={14} />
                    Ask for a change
                  </Button>
                </div>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
