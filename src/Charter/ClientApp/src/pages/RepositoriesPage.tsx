import { useCallback, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { Repo } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button, ButtonLink } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill, type Tone } from '@/components/ui/StatusPill';
import { TextInput } from '@/components/ui/Field';
import { FormAlert } from '@/features/auth/AuthPage';
import { SettingsNav } from '@/pages/SettingsPage';
import { useAsync } from '@/hooks/useAsync';

/**
 * §9 step 1, and the destination the §30.2 checklist's "connect your first repository" hands off to.
 *
 * A connected repository is not a usable one: §9's five steps end in a smoke test, and §7.3's repo
 * scope defaults to nobody. So this list says where each repository has got to rather than listing
 * them as though they were all equal, and connecting one drops the engineer straight into the
 * wizard rather than back here.
 */

const STATUS: Record<Repo['status'], { label: string; tone: Tone; next: string }> = {
  pending: { label: 'Just connected', tone: 'neutral', next: 'Let Charter read it' },
  recon: { label: 'Reading the repository', tone: 'active', next: 'Recon is running' },
  configuring: { label: 'Waiting on you', tone: 'attention', next: 'Confirm the scope config' },
  smoke_test: { label: 'Smoke test running', tone: 'active', next: 'Watch it run' },
  ready: { label: 'Ready', tone: 'good', next: 'Requesters can file against it' },
  disabled: { label: 'Disabled', tone: 'bad', next: 'Nobody can file against it' },
};

export function RepositoriesPage() {
  const api = useApi();
  const navigate = useNavigate();
  const load = useCallback((signal: AbortSignal) => api.listRepos(signal), [api]);
  const state = useAsync(load);

  const [fullName, setFullName] = useState('');
  const [baseBranch, setBaseBranch] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const connect = (event: FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setBusy(true);

    api
      .connectRepo({
        fullName: fullName.trim(),
        ...(baseBranch.trim() === '' ? {} : { baseBranch: baseBranch.trim() }),
      })
      .then((repo) => {
        void navigate(`/settings/repositories/${repo.id}`);
      })
      .catch((error: unknown) => {
        setFailure(
          error instanceof ApiError
            ? error.message
            : 'Charter could not reach the server just now. Try again in a moment.',
        );
      })
      .finally(() => {
        setBusy(false);
      });
  };

  return (
    <>
      <PageHeader
        description="What Charter is allowed to work on, and how far each one has got."
        title="Repositories"
      />

      <SettingsNav />

      <div className="mt-6 max-w-3xl space-y-4">
        {state.status === 'loading' ? (
          <Skeleton className="h-32 w-full" />
        ) : state.status === 'error' ? (
          <Card className="px-4 py-5">
            <p className="text-ink">
              Charter could not load your repositories. Connecting them is an engineer or
              administrator job — if you are neither, this page will stay empty.
            </p>
            <Button className="mt-3" onClick={state.reload}>
              Try again
            </Button>
          </Card>
        ) : state.data.length === 0 ? (
          /* §30.5: the single next action, named. On day one this page is the product. */
          <EmptyState
            description="Charter reads a repository before it touches one — what it is built with, how it is tested, and what it should never go near. Connect one and it will show you."
            icon="package"
            title="No repositories connected"
          />
        ) : (
          <ul className="space-y-2">
            {state.data.map((repo) => {
              const status = STATUS[repo.status];
              return (
                <li key={repo.id}>
                  <Link
                    className="border-line bg-surface hover:border-line-strong rounded-card block border px-4 py-3.5 transition-colors"
                    to={`/settings/repositories/${repo.id}`}
                  >
                    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
                      <StatusPill
                        tone={status.tone}
                        {...(repo.status === 'ready'
                          ? { icon: 'check' as const }
                          : repo.status === 'disabled'
                            ? { icon: 'alert' as const }
                            : {})}
                      >
                        {status.label}
                      </StatusPill>
                      {repo.requesterVisible ? null : (
                        <span className="text-tiny text-ink-subtle">
                          not visible to requesters yet
                        </span>
                      )}
                    </div>
                    <p className="text-ink mt-2 font-medium">{repo.fullName}</p>
                    <p className="text-small text-ink-muted mt-0.5">
                      {status.next} · base branch {repo.baseBranch}
                    </p>
                  </Link>
                </li>
              );
            })}
          </ul>
        )}

        <Card className="px-4 py-5 sm:px-5">
          <SectionLabel>Connect a repository</SectionLabel>
          <p className="text-small text-ink-muted mt-1.5">
            Charter reaches repositories through its GitHub App. Install it on the account that owns
            them first — it needs no access to anything you do not name.
          </p>

          <ButtonLink
            className="mt-3"
            href="https://github.com/apps/charter/installations/new"
            rel="noreferrer"
            size="sm"
            target="_blank"
          >
            <Icon name="external" size={14} />
            Install or review the GitHub App
          </ButtonLink>

          <form className="mt-5 space-y-4" noValidate onSubmit={connect}>
            {failure ? <FormAlert>{failure}</FormAlert> : null}

            <TextInput
              hint="As the provider spells it — owner and repository, separated by a slash."
              label="Repository"
              name="fullName"
              onChange={(event) => {
                setFullName(event.target.value);
              }}
              placeholder="northbeam/quote-tool"
              required
              spellCheck={false}
              value={fullName}
            />

            <TextInput
              hint="Defaults to main. Pull requests will be opened against it."
              label="Base branch"
              name="baseBranch"
              onChange={(event) => {
                setBaseBranch(event.target.value);
              }}
              placeholder="main"
              spellCheck={false}
              value={baseBranch}
            />

            <Button disabled={busy} size="lg" type="submit" variant="primary">
              {busy ? 'Connecting…' : 'Connect it'}
            </Button>
          </form>
        </Card>
      </div>
    </>
  );
}
