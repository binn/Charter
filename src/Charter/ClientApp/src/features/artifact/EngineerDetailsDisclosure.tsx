import type { EngineerDetails } from '@/api/types';
import { Disclosure } from '@/components/ui/Disclosure';
import { formatDuration } from '@/lib/format';

/**
 * §27.7, audience gating: "The `Details` disclosure (PR number, commit SHA, branch, runner,
 * duration, cost) renders only for users with repo read. Requesters never see a SHA."
 *
 * The gate is not here and it is not a role check. `details` is **omitted by the API** for anyone
 * without repo read, so the card simply has nothing to pass this component and does not render it.
 * There is no `isEngineer` prop, and adding one would be the bug.
 */
export function EngineerDetailsDisclosure({ details }: { details: EngineerDetails }) {
  return (
    <Disclosure
      aside={
        <span className="font-mono">
          PR #{details.pullRequestNumber} &middot; {details.commitSha} &middot;{' '}
          {formatDuration(details.durationMs)}
        </span>
      }
      summary="Details"
    >
      <dl className="text-small grid grid-cols-[7rem_1fr] gap-x-4 gap-y-1.5">
        <dt className="text-ink-subtle">Pull request</dt>
        <dd>
          <a
            className="text-accent underline decoration-dotted underline-offset-4"
            href={details.pullRequestUrl}
            rel="noreferrer"
            target="_blank"
          >
            #{details.pullRequestNumber}
          </a>
        </dd>

        <dt className="text-ink-subtle">Branch</dt>
        <dd className="text-ink font-mono break-all">{details.branch}</dd>

        <dt className="text-ink-subtle">Commit</dt>
        <dd className="text-ink font-mono">{details.commitSha}</dd>

        <dt className="text-ink-subtle">Runner</dt>
        <dd className="text-ink font-mono">{details.runner}</dd>

        <dt className="text-ink-subtle">Duration</dt>
        <dd className="text-ink">{formatDuration(details.durationMs)}</dd>

        <dt className="text-ink-subtle">Cost</dt>
        <dd className="text-ink font-mono">${details.costUsd.toFixed(2)}</dd>

        {details.recapUrl ? (
          <>
            <dt className="text-ink-subtle">Recap</dt>
            <dd>
              <a
                className="text-accent underline decoration-dotted underline-offset-4"
                href={details.recapUrl}
                rel="noreferrer"
                target="_blank"
              >
                Read the engineer recap
              </a>
            </dd>
          </>
        ) : null}
      </dl>
    </Disclosure>
  );
}
