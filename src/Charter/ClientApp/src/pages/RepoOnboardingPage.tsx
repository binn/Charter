import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { useApi } from '@/api/api-context';
import { ApiError } from '@/api/client';
import type { OnboardingStep } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { TextArea } from '@/components/ui/Field';
import { FormAlert } from '@/features/auth/AuthPage';
import { MergeGateCard } from '@/features/repos/MergeGateCard';
import { ScopeConfirmation } from '@/features/repos/ScopeConfirmation';
import { SmokeTestRun } from '@/features/repos/SmokeTestRun';
import { cn } from '@/lib/cn';

/**
 * §9, the repo onboarding wizard — "a wizard that ends in **proof**, not configuration. If
 * connecting a repo is a manual engineer chore, adoption stalls at repo one."
 *
 * Connect → recon → confirm scope → **watch the smoke test** → primer → merge-gate check. The order
 * is the spec's and so is the emphasis: the smoke test is the demo, so it gets the room, and the
 * merge-gate assessment gets said in words because it is the one place §7.4's guarantee weakens.
 *
 * Every step is on one page rather than behind Next buttons. A repository drifts, recon gets re-run,
 * a smoke test gets fixed and re-queued; a linear wizard would make each of those a fresh journey
 * through screens the engineer has already read.
 */
export function RepoOnboardingPage() {
  const api = useApi();
  const { id = '' } = useParams();
  const load = useCallback((signal: AbortSignal) => api.getRepoOnboarding(id, signal), [api, id]);
  const state = useAsyncPolling(load);

  const [busy, setBusy] = useState<'recon' | 'scope' | 'primer' | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [primer, setPrimer] = useState<string | null>(null);

  const onboarding = state.status === 'ready' ? state.data : null;
  const running = onboarding?.repo.status === 'smoke_test';
  const { reload } = state;

  // Poll while the smoke test is in flight. This is the only place in the app that polls: the
  // request stream has a socket, and a run that happens once per repository does not earn one.
  useEffect(() => {
    if (running !== true) {
      return;
    }
    const timer = setInterval(reload, 2_000);
    return () => {
      clearInterval(timer);
    };
  }, [running, reload]);

  const act = (
    kind: 'recon' | 'scope' | 'primer',
    run: () => Promise<{ explanation: string; warnings: string[] }>,
  ) => {
    setBusy(kind);
    setFailure(null);
    setNotice(null);

    run()
      .then((result) => {
        setNotice([result.explanation, ...result.warnings].join(' '));
        state.reload();
      })
      .catch((error: unknown) => {
        setFailure(
          error instanceof ApiError
            ? error.message
            : 'Charter could not reach the server just now. Try again in a moment.',
        );
      })
      .finally(() => {
        setBusy(null);
      });
  };

  if (state.status === 'loading') {
    return <Skeleton className="h-64 w-full" />;
  }

  if (state.status === 'error' || onboarding === null) {
    return (
      <Card className="px-4 py-5">
        <p className="text-ink">
          Charter could not load this repository. It may have been disconnected, or it may belong to
          somebody else.
        </p>
        <div className="mt-3 flex gap-2">
          <Button onClick={state.reload}>Try again</Button>
          <Link className="text-accent text-small self-center underline underline-offset-4" to="/settings/repositories">
            Back to repositories
          </Link>
        </div>
      </Card>
    );
  }

  const { repo, steps, proposedScope, lastSmokeTest, mergeGate, scopeConfigPullRequest } = onboarding;
  const primerDraft = primer ?? onboarding.primerDraftMd ?? '';

  return (
    <>
      <PageHeader
        description={
          <>
            Base branch <code className="font-mono">{repo.baseBranch}</code>. Nobody can file
            against this repository until its smoke test passes — that is what makes it ready, and
            there is no way to set it by hand.
          </>
        }
        title={repo.fullName}
      />

      <div className="grid gap-4 lg:grid-cols-[16rem_1fr]">
        <StepList steps={steps} />

        <div className="space-y-4">
          {failure ? <FormAlert>{failure}</FormAlert> : null}
          {notice ? (
            <Card className="border-accent-line bg-accent-soft px-4 py-3">
              <p className="text-small text-accent-soft-ink" role="status">
                {notice}
              </p>
            </Card>
          ) : null}

          {proposedScope === undefined ? (
            <Card className="px-4 py-5 sm:px-5">
              <SectionLabel>Let Charter read it</SectionLabel>
              <p className="text-small text-ink-muted mt-1.5">
                A read-only agent run over the repository: what it is built with, how it is
                structured, which commands run the tests, and what it should and should not be
                allowed to touch. It writes nothing.
              </p>
              <Button
                className="mt-4"
                disabled={busy === 'recon'}
                onClick={() => {
                  act('recon', () => api.startRecon(repo.id));
                }}
                size="lg"
                variant="primary"
              >
                {busy === 'recon' ? 'Reading the repository…' : 'Start recon'}
              </Button>
            </Card>
          ) : (
            <ScopeConfirmation
              busy={busy === 'scope'}
              onConfirm={(allow, deny) => {
                act('scope', () => api.confirmScope(repo.id, { allow, deny }));
              }}
              proposal={proposedScope}
            />
          )}

          {scopeConfigPullRequest === undefined ? null : (
            <p className="text-small text-ink-muted">
              The scope config is pull request #{scopeConfigPullRequest}. Review it like any other
              change — Charter cannot merge it.
            </p>
          )}

          <SmokeTestRun outcome={lastSmokeTest ?? null} running={running ?? false} />

          {mergeGate ? <MergeGateCard gate={mergeGate} /> : null}

          <Card className="px-4 py-5 sm:px-5">
            <SectionLabel>The primer</SectionLabel>
            <p className="text-small text-ink-muted mt-1.5">
              One page explaining what this project is, in the words the people who ask for changes
              actually use. Charter drafted it from what recon found; you know what it got wrong.
              Requesters read it once, before their first request.
            </p>

            {repo.hasPrimer ? (
              <p className="text-small text-ok mt-3 flex items-center gap-2">
                <Icon name="check" size={15} />
                Published. Edit and publish again whenever the project moves on.
              </p>
            ) : null}

            <div className="mt-3">
              <TextArea
                label="Primer"
                onChange={(event) => {
                  setPrimer(event.target.value);
                }}
                rows={12}
                value={primerDraft}
              />
            </div>

            <Button
              className="mt-3"
              disabled={busy === 'primer' || primerDraft.trim() === ''}
              onClick={() => {
                act('primer', () => api.publishPrimer(repo.id, { markdown: primerDraft }));
              }}
              variant="primary"
            >
              {busy === 'primer' ? 'Publishing…' : repo.hasPrimer ? 'Publish changes' : 'Publish the primer'}
            </Button>
            {primerDraft.trim() === '' ? (
              <p className="text-small text-ink-muted mt-2">
                There is nothing to publish yet — write a paragraph or two above first.
              </p>
            ) : null}
          </Card>
        </div>
      </div>
    </>
  );
}

function StepList({ steps }: { steps: OnboardingStep[] }) {
  return (
    <Card className="h-fit px-4 py-4 lg:sticky lg:top-20">
      <SectionLabel>Getting this repository ready</SectionLabel>
      <ol className="mt-3 space-y-1">
        {steps.map((step) => (
          <li className="flex items-start gap-2.5 px-1 py-1.5" key={step.id}>
            <span
              className={cn(
                'mt-0.5 grid size-5 shrink-0 place-items-center rounded-full border',
                step.done
                  ? 'border-ok bg-ok-soft text-ok'
                  : step.current
                    ? 'border-accent text-accent'
                    : 'border-line-strong text-ink-subtle',
              )}
            >
              {step.done ? <Icon name="check" size={12} /> : null}
            </span>
            <span
              className={cn(
                'text-small flex-1',
                step.done ? 'text-ink-muted' : step.current ? 'text-ink font-medium' : 'text-ink-muted',
              )}
            >
              {step.label}
            </span>
            {step.current && !step.done ? (
              <StatusPill tone="active">Next</StatusPill>
            ) : null}
          </li>
        ))}
      </ol>
    </Card>
  );
}

/**
 * `useAsync` with a stable identity for the polling effect above.
 *
 * Kept here rather than in `hooks/` because it is one line of difference and only this page needs
 * it: the returned object must be safe to depend on, which `useAsync`'s spread result is not.
 */
function useAsyncPolling<T>(load: (signal: AbortSignal) => Promise<T>) {
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => {
    setNonce((value) => value + 1);
  }, []);
  const [result, setResult] = useState<
    { status: 'loading' } | { status: 'ready'; data: T } | { status: 'error' }
  >({ status: 'loading' });

  useEffect(() => {
    const controller = new AbortController();
    let live = true;

    load(controller.signal)
      .then((data) => {
        if (live) {
          setResult({ status: 'ready', data });
        }
      })
      .catch(() => {
        if (live && !controller.signal.aborted) {
          setResult({ status: 'error' });
        }
      });

    return () => {
      live = false;
      controller.abort();
    };
  }, [load, nonce]);

  return { ...result, reload };
}
