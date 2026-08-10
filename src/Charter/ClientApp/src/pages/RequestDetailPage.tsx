import { useCallback, useState } from 'react';
import { Link, useParams } from 'react-router';
import { useApi } from '@/api/api-context';
import type { FeedbackVerdict } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Disclosure } from '@/components/ui/Disclosure';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon } from '@/components/ui/Icon';
import { Skeleton } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { VerificationArtifactCard } from '@/features/artifact/VerificationArtifactCard';
import { ThreePaneView } from '@/features/panes/ThreePaneView';
import { EngineerRecapCard } from '@/features/recap/EngineerRecapCard';
import { PostHocActions } from '@/features/session/PostHocActions';
import { RefinementConversation } from '@/features/refinement/RefinementConversation';
import { SpecConfirmationCard } from '@/features/spec/SpecConfirmationCard';
import { StatusThreadView } from '@/features/status/StatusThreadView';
import { useRequestDetail } from '@/features/request/useRequestDetail';
import { requesterStatus } from '@/lib/status';

/**
 * One request, start to finish.
 *
 * §11: **one thread per request, forever.** Several sessions, a rebuild, a "not quite" and its
 * follow-up all live on this page — there is no second card and no second URL, because "a requester
 * should never wonder which of three cards is live."
 *
 * The order of the page follows what the person actually came for. If there is something to try,
 * that is at the top. The conversation and the approved spec sink into disclosures once they are
 * history, but they never disappear: "the spec said X" only works if the spec is still there.
 */
export function RequestDetailPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const state = useRequestDetail(id);
  const [cancelling, setCancelling] = useState(false);

  const onFeedback = useCallback(
    async (verdict: FeedbackVerdict, note?: string) => {
      await api.submitFeedback(id, { verdict, ...(note === undefined ? {} : { note }) });
      state.refresh();
    },
    [api, id, state],
  );

  const onRebuild = useCallback(
    async (artifactId: string) => {
      await api.rebuildArtifact(id, artifactId);
      state.refresh();
    },
    [api, id, state],
  );

  const onApprove = useCallback(async () => {
    const version = state.status === 'ready' ? state.request.spec?.version : undefined;
    if (version === undefined) {
      return;
    }
    await api.approveSpec(id, version);
    state.refresh();
  }, [api, id, state]);

  const onRequestChanges = useCallback(
    async (note: string) => {
      const version = state.status === 'ready' ? state.request.spec?.version : undefined;
      if (version === undefined) {
        return;
      }
      await api.requestSpecChanges(id, version, note);
      state.refresh();
    },
    [api, id, state],
  );

  const onSend = useCallback(
    async (body: string) => {
      await api.sendRefinementMessage(id, { body });
      state.refresh();
    },
    [api, id, state],
  );

  if (state.status === 'loading') {
    return <Skeleton className="h-96 w-full" />;
  }

  if (state.status === 'error') {
    return (
      <EmptyState
        description="This request either does not exist or is not yours to see."
        icon="search"
        secondary={
          <Link className="text-accent underline underline-offset-4" to="/requests">
            Back to your requests
          </Link>
        }
        title="Not found"
      />
    );
  }

  const request = state.request;
  const status = requesterStatus(request.status);
  const spec = request.spec;
  const approved = spec?.approvedAt !== undefined;
  const isRefining = request.status === 'refining' || request.status === 'draft';
  const hasArtifacts = request.artifacts.length > 0;
  const currentMilestone = request.thread.milestones.findLast(
    (milestone) => milestone.state === 'active',
  );

  const paneOne = (
    <div className="space-y-6">
      {hasArtifacts && !isRefining && spec ? (
        <VerificationArtifactCard
          acceptanceCriteria={spec.acceptanceCriteria}
          artifacts={request.artifacts}
          onFeedback={onFeedback}
          onRebuild={onRebuild}
          specTitle={spec.title}
          {...(request.thread.feedback ? { feedback: request.thread.feedback } : {})}
          {...(request.thread.startedAt ? { startedAt: request.thread.startedAt } : {})}
          {...(currentMilestone ? { currentMilestoneLabel: currentMilestone.label } : {})}
        />
      ) : null}

      {isRefining ? (
        <RefinementConversation onSend={onSend} thread={request.refinement} />
      ) : null}

      {spec && !approved ? (
        <SpecConfirmationCard
          onApprove={onApprove}
          onRequestChanges={onRequestChanges}
          spec={spec}
        />
      ) : null}

      {request.thread.milestones.length > 0 ? (
        <Card className="px-4 py-5 sm:px-5">
          <SectionLabel>Progress</SectionLabel>
          <div className="mt-4">
            <StatusThreadView thread={request.thread} />
          </div>
        </Card>
      ) : null}

      {/*
       * §14 and §7.5. Both are present only when the API sent them, which it does only for a viewer
       * with repo read — so a requester's pane 1 ends at the progress thread, and the engineer
       * surfaces are absent from their payload rather than hidden from their screen.
       */}
      {request.recap ? <EngineerRecapCard recap={request.recap} /> : null}

      {request.sessionActions ? (
        <PostHocActions
          actions={request.sessionActions}
          onDone={state.refresh}
          requestId={request.id}
          {...(spec ? { specTitle: spec.title } : {})}
        />
      ) : null}

      {spec && approved ? (
        <Card className="px-4 py-4 sm:px-5">
          <Disclosure summary="What you approved">
            <h3 className="text-ink font-medium">{spec.title}</h3>
            <p className="text-ink-muted text-small mt-1">{spec.outcome}</p>
            <ul className="mt-3 space-y-1.5">
              {spec.acceptanceCriteria.map((criterion) => (
                <li className="text-small text-ink flex items-start gap-2" key={criterion.id}>
                  <Icon className="text-accent mt-1" name="check" size={13} />
                  {criterion.text}
                </li>
              ))}
            </ul>
          </Disclosure>
        </Card>
      ) : null}

      {!isRefining && request.refinement.messages.length > 0 ? (
        <Card className="px-4 py-4 sm:px-5">
          <Disclosure summary="How we worked this out">
            <div className="pt-2">
              <RefinementConversation
                onSend={onSend}
                thread={{ ...request.refinement, canReply: false }}
              />
            </div>
          </Disclosure>
        </Card>
      ) : null}
    </div>
  );

  return (
    <>
      <Link
        className="text-small text-ink-muted hover:text-ink mb-4 inline-flex items-center gap-1.5"
        to="/requests"
      >
        <Icon className="rotate-180" name="arrowRight" size={14} />
        All requests
      </Link>

      <PageHeader
        actions={
          request.cancellable ? (
            <Button
              disabled={cancelling}
              onClick={() => {
                setCancelling(true);
                void api
                  .cancelRequest(request.id)
                  .then(() => {
                    state.refresh();
                  })
                  .finally(() => {
                    setCancelling(false);
                  });
              }}
              variant="danger"
            >
              <Icon name="stop" size={15} />
              {cancelling ? 'Stopping…' : 'Stop this'}
            </Button>
          ) : null
        }
        description={
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <StatusPill
              pulse={status.tone === 'active'}
              tone={status.tone}
              {...(status.tone === 'good'
                ? { icon: 'check' as const }
                : status.tone === 'bad'
                  ? { icon: 'alert' as const }
                  : {})}
            >
              {status.label}
            </StatusPill>
            {status.blurb ? <span>{status.blurb}</span> : null}
            <span className="text-ink-subtle">&middot; {request.projectName}</span>
          </span>
        }
        title={spec?.title ?? request.title}
      />

      <ThreePaneView request={request}>{paneOne}</ThreePaneView>
    </>
  );
}
