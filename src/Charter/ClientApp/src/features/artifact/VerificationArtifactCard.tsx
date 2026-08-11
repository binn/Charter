import { useMemo, useState } from 'react';
import type {
  AcceptanceCriterion,
  FeedbackRecord,
  FeedbackVerdict,
  Iso8601,
  VerificationArtifact,
} from '@/api/types';
import { Button, ButtonLink } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { CopyButton } from '@/components/ui/CopyButton';
import { Disclosure } from '@/components/ui/Disclosure';
import { Icon } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { SkeletonArtifactBody } from '@/components/ui/Skeleton';
import { StatusPill } from '@/components/ui/StatusPill';
import { Tabs } from '@/components/ui/Tabs';
import { ArtifactBody } from '@/features/artifact/ArtifactBody';
import { EngineerDetailsDisclosure } from '@/features/artifact/EngineerDetailsDisclosure';
import { ExpiryCountdown } from '@/features/artifact/ExpiryCountdown';
import { FeedbackButtons } from '@/features/artifact/FeedbackButtons';
import { WhatToCheck } from '@/features/artifact/WhatToCheck';
import {
  artifactIcon,
  artifactStateStyle,
  effectiveState,
  isDisplayablePreviewUrl,
  orderArtifacts,
  primaryActionFor,
} from '@/features/artifact/artifact-presentation';
import { useIsDesktop } from '@/hooks/useMediaQuery';
import { useNow } from '@/hooks/useNow';
import { elapsedSince, formatDuration } from '@/lib/format';

export interface VerificationArtifactCardProps {
  /** §27.7: several artifacts become tabs inside this one card. Never several cards. */
  artifacts: VerificationArtifact[];
  /** The approved Spec's title. The card is the payoff for that spec, so it says which one. */
  specTitle: string;
  /** Rendered verbatim as "What to check". The contract the requester approved (§10b). */
  acceptanceCriteria: AcceptanceCriterion[];
  feedback?: FeedbackRecord;
  onFeedback: (verdict: FeedbackVerdict, note?: string) => Promise<void>;
  onRebuild: (artifactId: string) => Promise<void>;
  /** Used for the elapsed timer on `pending`. Elapsed only — §6 forbids an ETA. */
  startedAt?: Iso8601;
  /** Shown under the skeleton while pending, so the wait has a subject. */
  currentMilestoneLabel?: string;
}

/**
 * **The single most-looked-at component in Charter** (§27.7). It is the payoff for the entire
 * pipeline, and it has to read well to someone who has never seen a pull request.
 *
 * Everything §27.7 asks for is here: the anatomy in the given order, tabs for multiple artifacts
 * with the primary first, a countdown visible from first render, `expired` as a designed state with
 * Rebuild as its primary action, the "What to check" list printed verbatim from the acceptance
 * criteria, two feedback buttons, and a Details disclosure that exists only when the API sent
 * `details`.
 */
export function VerificationArtifactCard({
  artifacts,
  specTitle,
  acceptanceCriteria,
  feedback,
  onFeedback,
  onRebuild,
  startedAt,
  currentMilestoneLabel,
}: VerificationArtifactCardProps) {
  const ordered = useMemo(() => orderArtifacts(artifacts), [artifacts]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [rebuilding, setRebuilding] = useState(false);
  const isDesktop = useIsDesktop();

  const selected = ordered.find((candidate) => candidate.id === selectedId) ?? ordered[0];
  const primary = ordered[0];

  // Tick once a second whenever anything on screen expires, and not otherwise.
  const anyExpiry = ordered.some((artifact) => artifact.expiresAt !== undefined);
  const isPending = ordered.some((artifact) => artifact.state === 'pending');
  const now = useNow(anyExpiry || isPending ? 1_000 : null);

  if (!selected || !primary) {
    return null;
  }

  const state = effectiveState(selected, now);
  const style = artifactStateStyle(state, selected.kind);
  const action = primaryActionFor(selected, state);
  const primaryState = effectiveState(primary, now);
  const canGiveFeedback =
    primary.kind !== 'none' &&
    (primaryState === 'ready' || primaryState === 'expiring' || primaryState === 'expired');

  const renderPrimaryAction = (block: boolean) => {
    if (action.behaviour === 'none') {
      return null;
    }
    if (action.behaviour === 'link' && action.href) {
      return (
        <ButtonLink
          block={block}
          href={action.href}
          rel="noreferrer"
          size="lg"
          target="_blank"
          variant="primary"
          {...(action.download ? { download: '' } : {})}
        >
          {action.label}
          <Icon name={action.icon} size={17} />
        </ButtonLink>
      );
    }
    if (action.behaviour === 'copy' && action.value) {
      return (
        <CopyButton
          label={action.label}
          showLabel
          size="lg"
          value={action.value}
          variant="primary"
          {...(block ? { className: 'w-full' } : {})}
        />
      );
    }
    return (
      <Button
        block={block}
        disabled={rebuilding}
        onClick={() => {
          setRebuilding(true);
          void onRebuild(selected.id).finally(() => {
            setRebuilding(false);
          });
        }}
        size="lg"
        variant="primary"
      >
        <Icon name={action.icon} size={17} />
        {rebuilding ? 'Starting it off…' : action.label}
      </Button>
    );
  };

  const secondaryAction =
    action.behaviour === 'link' && action.href && !action.download ? (
      <CopyButton label="Copy link" size="lg" value={action.href} variant="secondary" />
    ) : null;

  const body = (() => {
    switch (state) {
      case 'pending':
        return (
          <div className="space-y-3">
            <SkeletonArtifactBody />
            <p className="text-small text-ink-muted flex flex-wrap items-center gap-x-2 gap-y-1">
              <Icon className="animate-pulse-soft" name="hourglass" size={14} />
              {currentMilestoneLabel ?? 'Working on it'}
              {startedAt ? (
                <span className="text-ink-subtle">
                  &middot; {formatDuration(elapsedSince(startedAt, now))} so far
                </span>
              ) : null}
            </p>
          </div>
        );

      case 'expired':
        // §27.7: "expiry must be a designed state rather than a 404". No dead link is offered.
        return (
          <div className="border-line bg-sunken rounded-control border border-dashed px-4 py-5 text-center">
            <Icon className="text-ink-subtle mx-auto mb-2" name="clock" size={20} />
            <p className="text-ink font-medium">This preview has been cleaned up</p>
            <p className="text-small text-ink-muted mx-auto mt-1 max-w-sm text-balance">
              Previews are temporary so they do not pile up. Nothing has been lost — building it
              again takes a few minutes and gives you a fresh link.
            </p>
          </div>
        );

      case 'failed':
        return (
          <div className="border-danger-line bg-danger-soft rounded-control border px-4 py-4">
            <p className="text-ink flex items-start gap-2.5">
              <Icon className="text-danger mt-0.5" name="alert" size={17} />
              <span className="text-small">
                {selected.failureSummary ??
                  'This turned out to be bigger than expected, so there is nothing to try. An engineer has been told.'}
              </span>
            </p>
          </div>
        );

      default:
        return <ArtifactBody artifact={selected} />;
    }
  })();

  // "Nothing you do here touches the real one" is Charter vouching for a link. It is not printed
  // above a preview whose link Charter has withheld — the reassurance is the thing that would be
  // false, and it is the sentence a requester would be trusting (§16.3, §27.7).
  const vouchable =
    selected.kind !== 'hosted_preview' || isDisplayablePreviewUrl(selected.payload.url);

  const artifactPanel = (
    <div className="space-y-4 pt-4">
      {selected.instructionsMd && vouchable && state !== 'expired' && state !== 'failed' ? (
        <div className="text-small text-ink-muted">
          <Markdown>{selected.instructionsMd}</Markdown>
        </div>
      ) : null}

      {body}

      {action.behaviour !== 'none' ? (
        <div className="hidden flex-wrap items-center gap-2 sm:flex">
          {renderPrimaryAction(false)}
          {secondaryAction}
        </div>
      ) : null}
    </div>
  );

  return (
    <Card bleed>
      <div className="px-4 pt-4 sm:px-5 sm:pt-5">
        {/* Status and expiry, in that order, at the top — the anatomy in §27.7. */}
        <div className="flex flex-wrap items-center justify-between gap-2">
          {/* State changes arrive over the socket. A card that silently swaps from a skeleton to a
              button tells a screen reader user nothing, so the status itself is the live region —
              announced on change rather than duplicated into a second hidden copy. */}
          <span aria-atomic="true" aria-live="polite">
            <StatusPill icon={style.icon} tone={style.tone}>
              {style.label}
            </StatusPill>
          </span>
          <ExpiryCountdown expiresAt={selected.expiresAt} now={now} />
        </div>

        <h2 className="font-display text-display text-ink mt-3">{specTitle}</h2>
      </div>

      {ordered.length > 1 ? (
        <div className="mt-4">
          <Tabs
            activeId={selected.id}
            items={ordered.map((artifact) => {
              const artifactState = effectiveState(artifact, now);
              return {
                id: artifact.id,
                label: artifact.label,
                icon: artifactIcon(artifact.kind),
                ...(artifactState === 'expired'
                  ? { hint: 'expired' }
                  : artifactState === 'pending'
                    ? { hint: 'building' }
                    : {}),
              };
            })}
            label="Ways to check this change"
            onChange={setSelectedId}
          >
            <div className="px-4 sm:px-5">{artifactPanel}</div>
          </Tabs>
        </div>
      ) : (
        <div className="px-4 sm:px-5">{artifactPanel}</div>
      )}

      {/* Shared across tabs: the checklist and the verdict are about the change, not about which
          way you chose to look at it. */}
      {acceptanceCriteria.length > 0 ? (
        <div className="border-line mt-5 border-t px-4 pt-4 sm:px-5">
          {isDesktop ? (
            <WhatToCheck criteria={acceptanceCriteria} />
          ) : (
            // §27.7, mobile: checklist collapsed by default.
            <Disclosure summary={`What to check (${acceptanceCriteria.length})`}>
              <WhatToCheck criteria={acceptanceCriteria} />
            </Disclosure>
          )}
        </div>
      ) : null}

      {canGiveFeedback ? (
        <div className="border-line mt-4 border-t px-4 py-4 sm:px-5">
          <FeedbackButtons existing={feedback} now={now} onSubmit={onFeedback} />
        </div>
      ) : null}

      {/* Renders only because the API sent `details`. There is no role check here — §7.4. */}
      {selected.details ? (
        <div className="border-line bg-sunken/60 border-t px-4 py-2 sm:px-5">
          <EngineerDetailsDisclosure details={selected.details} />
        </div>
      ) : null}

      {/* §27.7, mobile: primary action pinned as a sticky footer button. */}
      {action.behaviour !== 'none' ? (
        <div className="border-line bg-surface/95 sticky bottom-0 z-10 border-t px-4 py-3 backdrop-blur-sm sm:hidden">
          {renderPrimaryAction(true)}
        </div>
      ) : null}
    </Card>
  );
}
