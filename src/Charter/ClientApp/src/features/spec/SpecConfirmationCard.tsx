import { useState } from 'react';
import type { RequesterSpec } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Disclosure } from '@/components/ui/Disclosure';
import { Icon } from '@/components/ui/Icon';
import { TextArea } from '@/components/ui/Field';
import { StatusPill } from '@/components/ui/StatusPill';
import { formatRelative } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

export interface SpecConfirmationCardProps {
  spec: RequesterSpec;
  onApprove: () => Promise<void>;
  onRequestChanges: (note: string) => Promise<void>;
}

/**
 * The spec confirmation card (§10, §10b).
 *
 * "This is the ownership moment: later, when a preview is wrong, the conversation is 'the spec said
 * X' rather than 'the AI misunderstood.'"
 *
 * It renders `title`, `outcome` and `acceptanceCriteria`. **Nothing else, ever.** The technical
 * approach, the file scope and the risk list are engineer-facing, and they are not omitted here by
 * a conditional — they are not in `RequesterSpec` at all, so there is no expression this component
 * could write that would put them on screen. That is what §7.4 means by authorisation not being a
 * rendering concern.
 *
 * The acceptance criteria carry the weight: "the requester approves the acceptance criteria, not
 * the technical approach. That is the thing they can meaningfully judge." So they are the visually
 * dominant part of the card, not a footnote under the prose.
 */
export function SpecConfirmationCard({
  spec,
  onApprove,
  onRequestChanges,
}: SpecConfirmationCardProps) {
  const now = useNow(60_000);
  const [mode, setMode] = useState<'idle' | 'changes' | 'busy'>('idle');
  const [note, setNote] = useState('');
  const approved = spec.approvedAt !== undefined;

  return (
    <Card className="overflow-hidden">
      <div className="border-accent-line bg-accent-soft border-b px-4 py-2.5 sm:px-5">
        <p className="text-accent-soft-ink text-small flex items-center gap-2 font-medium">
          <Icon name="spark" size={15} />
          {approved ? 'You approved this' : 'Does this describe what you want?'}
        </p>
      </div>

      <div className="space-y-5 px-4 py-5 sm:px-5">
        <div>
          <h2 className="font-display text-display text-ink">{spec.title}</h2>
          <p className="text-ink-muted text-lead mt-2">{spec.outcome}</p>
        </div>

        <div>
          <SectionLabel>What will be true when this is done</SectionLabel>
          <ul className="mt-2.5 space-y-2">
            {spec.acceptanceCriteria.map((criterion) => (
              <li className="flex items-start gap-2.5" key={criterion.id}>
                <Icon className="text-accent mt-[0.3rem]" name="check" size={14} />
                <span className="text-ink">{criterion.text}</span>
              </li>
            ))}
          </ul>
          <p className="text-tiny text-ink-subtle mt-3">
            These are the exact words you will be asked to check against at the end. If one of them
            is wrong, say so now — it is much cheaper than saying so later.
          </p>
        </div>

        {spec.openQuestions && spec.openQuestions.length > 0 ? (
          <div className="border-warn-line bg-warn-soft rounded-control border px-3.5 py-3">
            <p className="text-warn text-small flex items-center gap-2 font-medium">
              <Icon name="alert" size={14} />
              Still open
            </p>
            <ul className="text-small text-ink mt-2 space-y-1.5">
              {spec.openQuestions.map((question) => (
                <li className="flex gap-2" key={question}>
                  <span aria-hidden="true" className="text-ink-subtle">
                    &bull;
                  </span>
                  {question}
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {/* §10b's Explain lens, at its cheapest: the domain words this spec leans on, defined from
            the repo's own glossary.yml rather than from a model's general knowledge. */}
        {spec.glossary && spec.glossary.length > 0 ? (
          <Disclosure summary="Words used here">
            <dl className="text-small space-y-2">
              {spec.glossary.map((entry) => (
                <div key={entry.term}>
                  <dt className="text-ink font-medium">{entry.term}</dt>
                  <dd className="text-ink-muted">{entry.definition}</dd>
                </div>
              ))}
            </dl>
          </Disclosure>
        ) : null}
      </div>

      <div className="border-line bg-sunken/60 border-t px-4 py-3.5 sm:px-5">
        {approved ? (
          <p className="text-small text-ink-muted flex flex-wrap items-center gap-2">
            <StatusPill icon="check" tone="good">
              Approved
            </StatusPill>
            {spec.approvedByName ? `by ${spec.approvedByName}` : null}
            {spec.approvedAt ? (
              <span className="text-ink-subtle">{formatRelative(spec.approvedAt, now)}</span>
            ) : null}
          </p>
        ) : mode === 'changes' || mode === 'busy' ? (
          <div className="space-y-3">
            <TextArea
              autoFocus
              hint="Say it however you like. Charter will come back with a corrected version."
              label="What is not right?"
              onChange={(event) => {
                setNote(event.target.value);
              }}
              placeholder="e.g. it should only remember it for me, not for the whole team"
              rows={3}
              value={note}
            />
            <div className="flex flex-wrap gap-2">
              <Button
                disabled={mode === 'busy' || note.trim() === ''}
                onClick={() => {
                  setMode('busy');
                  void onRequestChanges(note.trim());
                }}
                variant="primary"
              >
                Send this back
              </Button>
              <Button
                disabled={mode === 'busy'}
                onClick={() => {
                  setMode('idle');
                  setNote('');
                }}
                variant="ghost"
              >
                Cancel
              </Button>
            </div>
          </div>
        ) : (
          <div className="flex flex-wrap items-center gap-2">
            <Button
              onClick={() => {
                setMode('busy');
                void onApprove().finally(() => {
                  setMode('idle');
                });
              }}
              size="lg"
              variant="primary"
            >
              <Icon name="check" size={17} />
              Yes, build this
            </Button>
            <Button
              onClick={() => {
                setMode('changes');
              }}
              size="lg"
              variant="secondary"
            >
              Not quite right
            </Button>
          </div>
        )}
      </div>
    </Card>
  );
}
