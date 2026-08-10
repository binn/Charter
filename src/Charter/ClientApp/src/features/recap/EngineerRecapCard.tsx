import type { ReactNode } from 'react';
import type { ChangedFile, EngineerRecap } from '@/api/types';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Disclosure } from '@/components/ui/Disclosure';
import { Icon } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { StatusPill } from '@/components/ui/StatusPill';
import { usePaneSelection } from '@/features/panes/pane-selection';
import { cn } from '@/lib/cn';
import { formatRelative } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

const RISK_TONE: Record<ChangedFile['risk'], 'bad' | 'warn' | 'neutral'> = {
  high: 'bad',
  medium: 'warn',
  low: 'neutral',
};

const RISK_ICON = { high: 'alert', medium: 'eye', low: 'check' } as const;

export interface EngineerRecapCardProps {
  recap: EngineerRecap;
}

/**
 * §14, the engineer recap.
 *
 * The section order is the spec's order and it is not arbitrary: what and why, then **where the
 * agent deviated** — "the highest-value section and the thing reviewers most often miss" — then the
 * risk-ranked files, then what could not be verified, then where to start reading. Sorting the
 * files alphabetically or burying the deviations in a disclosure would each individually undo the
 * point of the feature.
 *
 * **It must never say "looks good."** That is a constraint on generation, but the rendering carries
 * it too: there is no verdict badge here, no tick, no score. The card says in as many words that it
 * is an orientation aid, because the failure mode §14 warns about is reviewers trusting the summary
 * instead of reading the diff.
 *
 * Every file listed is a button into pane 3 — §12's linkage reaching one surface further, so
 * "start with CurrentUserAccessor.cs" is a click rather than an instruction to go and find it.
 */
export function EngineerRecapCard({ recap }: EngineerRecapCardProps) {
  const linkage = usePaneSelection();
  const now = useNow(60_000);

  /*
   * A recap can exist without a Developer pane to link into — a session that failed before writing
   * anything has a recap and no diff. In that case these paths must render as text, not as buttons
   * that quietly do nothing. A control that looks interactive and is not is worse than plain text.
   */
  const canOpenFiles = linkage !== null;

  const openFile = (path: string, fromKeyboard: boolean) => {
    linkage?.selectFile(path, { fromKeyboard });
  };

  return (
    <Card className="px-4 py-5 sm:px-5">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <SectionLabel>Recap for the reviewer</SectionLabel>
        <p className="text-tiny text-ink-subtle">
          written {formatRelative(recap.generatedAt, now)}
        </p>
      </div>

      {/*
       * §7.5: "the engineer recap leads with the fact that no human approved the specification
       * before the build". Leading with it means first, above the summary — not a footnote.
       */}
      {recap.autoDispatched ? (
        <div className="border-warn-line bg-warn-soft rounded-control mt-3 flex gap-2.5 border px-3.5 py-3">
          <Icon className="text-warn mt-0.5 shrink-0" name="alert" size={16} />
          <div>
            <p className="text-ink text-small font-medium">
              Nobody approved this specification before it was built.
            </p>
            <p className="text-small text-ink-muted mt-0.5">
              It was dispatched automatically under your organisation&rsquo;s policy. The full spec is
              below rather than a summary of it, because a summary is not something you can review.
            </p>
          </div>
        </div>
      ) : null}

      <div className="text-ink-muted text-small mt-4">
        <Markdown>{recap.summaryMd}</Markdown>
      </div>

      {recap.specMd ? (
        <div className="border-line bg-sunken rounded-control text-small text-ink-muted mt-4 border px-3.5 py-3">
          <SectionLabel className="mb-2">The specification, in full</SectionLabel>
          <Markdown>{recap.specMd}</Markdown>
        </div>
      ) : null}

      {/* Highest-value section (§14), so it sits above the file list and is never collapsed. */}
      <section className="mt-6">
        <SectionLabel>Where it deviated, or decided for itself</SectionLabel>
        {recap.deviations.length === 0 ? (
          <p className="text-small text-ink-muted mt-2">
            Nothing recorded. That is a claim about the spec being followed, not a claim about the
            code being right.
          </p>
        ) : (
          <ul className="mt-3 space-y-3">
            {recap.deviations.map((deviation) => (
              <li className="border-line border-l-2 pl-3" key={deviation.id}>
                {deviation.specSaid ? (
                  <p className="text-small text-ink-subtle">
                    <span className="font-medium">The spec said:</span> {deviation.specSaid}
                  </p>
                ) : (
                  <p className="text-small text-ink-subtle font-medium">
                    The spec did not cover this.
                  </p>
                )}
                <p className="text-small text-ink mt-1">{deviation.agentDid}</p>
                {deviation.path ? (
                  canOpenFiles ? (
                    <button
                      className="text-tiny text-accent mt-1.5 inline-flex items-center gap-1 font-mono underline decoration-dotted underline-offset-2"
                      onClick={(event) => {
                        openFile(deviation.path as string, event.detail === 0);
                      }}
                      type="button"
                    >
                      {deviation.path}
                      <Icon name="chevronRight" size={11} />
                    </button>
                  ) : (
                    <span className="text-tiny text-ink-subtle mt-1.5 block font-mono">
                      {deviation.path}
                    </span>
                  )
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="mt-6">
        <SectionLabel>Files, riskiest first</SectionLabel>
        <ul className="mt-2 space-y-0.5">
          {recap.files.map((file) => (
            <li key={file.path}>
              <RecapRow
                canOpen={canOpenFiles}
                onOpen={(fromKeyboard) => {
                  openFile(file.path, fromKeyboard);
                }}
              >
                <span className="flex items-center gap-2">
                  <StatusPill icon={RISK_ICON[file.risk]} tone={RISK_TONE[file.risk]}>
                    {file.risk}
                  </StatusPill>
                  <span className="text-small text-ink min-w-0 flex-1 truncate font-mono">
                    {file.path}
                  </span>
                  <span className="text-tiny text-ok shrink-0 font-mono">+{file.additions}</span>
                  <span className="text-tiny text-danger shrink-0 font-mono">
                    &minus;{file.deletions}
                  </span>
                </span>
                {file.riskReasons && file.riskReasons.length > 0 ? (
                  <span className="text-tiny text-ink-subtle mt-0.5 block pl-1">
                    {file.riskReasons.join(' · ')}
                  </span>
                ) : null}
              </RecapRow>
            </li>
          ))}
        </ul>
      </section>

      <section className="mt-6">
        <SectionLabel>What it could not verify</SectionLabel>
        {recap.couldNotVerify.length === 0 ? (
          <p className="text-small text-ink-muted mt-2">Nothing flagged.</p>
        ) : (
          <ul className="mt-2 space-y-1.5">
            {recap.couldNotVerify.map((note) => (
              <li className="text-small text-ink flex gap-2" key={note.id}>
                <Icon className="text-ink-subtle mt-1 shrink-0" name="eye" size={12} />
                {note.text}
              </li>
            ))}
          </ul>
        )}
      </section>

      {recap.reviewOrder.length > 0 ? (
        <section className="mt-6">
          <SectionLabel>Suggested reading order</SectionLabel>
          <ol className="mt-2 space-y-1">
            {recap.reviewOrder.map((path, index) => (
              <li className="flex items-baseline gap-2" key={path}>
                <span className="text-tiny text-ink-subtle w-4 shrink-0 text-right tabular-nums">
                  {index + 1}
                </span>
                {canOpenFiles ? (
                  <button
                    className={cn(
                      'text-small text-accent min-w-0 truncate font-mono underline',
                      'decoration-dotted underline-offset-2',
                    )}
                    onClick={(event) => {
                      openFile(path, event.detail === 0);
                    }}
                    type="button"
                  >
                    {path}
                  </button>
                ) : (
                  <span className="text-small text-ink min-w-0 truncate font-mono">{path}</span>
                )}
              </li>
            ))}
          </ol>
        </section>
      ) : null}

      <div className="border-line mt-6 border-t pt-3">
        <Disclosure summary="What this is, and what it is not">
          <p className="text-small text-ink-muted">
            This is an orientation aid written from the session&rsquo;s own events. It is not a review
            and it does not have an opinion on whether the code is any good — if it ever appears to,
            that is a bug worth reporting. Read the diff.
          </p>
        </Disclosure>

        {recap.postedToUrl ? (
          <p className="text-tiny text-ink-subtle mt-2">
            Also posted on the{' '}
            <a
              className="text-accent underline underline-offset-2"
              href={recap.postedToUrl}
              rel="noreferrer"
              target="_blank"
            >
              {recap.postedToTerm ?? 'change request'}
            </a>
            , where the review actually happens.
          </p>
        ) : (
          <p className="text-tiny text-ink-subtle mt-2">
            Your provider has nowhere to post this, so this view is the only copy.
          </p>
        )}
      </div>
    </Card>
  );
}

/**
 * One file in the risk-ranked list: a control when there is a Developer pane to open it in, and
 * plain text when there is not.
 */
function RecapRow({
  canOpen,
  onOpen,
  children,
}: {
  canOpen: boolean;
  onOpen: (fromKeyboard: boolean) => void;
  children: ReactNode;
}) {
  if (!canOpen) {
    return <div className="px-2 py-1.5">{children}</div>;
  }

  return (
    <button
      className="hover:bg-sunken w-full rounded-[0.375rem] px-2 py-1.5 text-left transition-colors"
      onClick={(event) => {
        onOpen(event.detail === 0);
      }}
      type="button"
    >
      {children}
    </button>
  );
}
