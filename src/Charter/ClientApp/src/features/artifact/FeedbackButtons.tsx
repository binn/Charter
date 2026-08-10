import { useState } from 'react';
import type { FeedbackRecord, FeedbackVerdict } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Icon } from '@/components/ui/Icon';
import { TextArea } from '@/components/ui/Field';
import { formatRelative } from '@/lib/format';

export interface FeedbackButtonsProps {
  existing: FeedbackRecord | undefined;
  now: number;
  onSubmit: (verdict: FeedbackVerdict, note?: string) => Promise<void>;
}

/**
 * §11: "Feedback is two buttons — *Works* / *Not quite*. The second opens a box and becomes a new
 * session on the same spec, same thread. **Don't make them write a bug report.**"
 *
 * So: two buttons, no category dropdown, no severity, no required field. The note after "Not quite"
 * is optional and the placeholder asks for one sentence, because the alternative — a form — is how
 * you teach someone that reporting things is work.
 */
export function FeedbackButtons({ existing, now, onSubmit }: FeedbackButtonsProps) {
  const [mode, setMode] = useState<'idle' | 'noting' | 'sending'>('idle');
  const [note, setNote] = useState('');

  if (existing) {
    return (
      <p className="text-small text-ink-muted inline-flex items-center gap-2">
        <Icon
          className={existing.verdict === 'works' ? 'text-ok' : 'text-ink-subtle'}
          name={existing.verdict === 'works' ? 'check' : 'message'}
          size={15}
        />
        {existing.verdict === 'works'
          ? 'You said this works'
          : 'You said this was not quite right'}
        <span className="text-ink-subtle">&middot; {formatRelative(existing.submittedAt, now)}</span>
      </p>
    );
  }

  if (mode === 'noting' || mode === 'sending') {
    return (
      <div className="space-y-3">
        <TextArea
          autoFocus
          label="What was not right?"
          hint="One sentence is plenty. This goes back with the original plan — you do not need to repeat it."
          onChange={(event) => {
            setNote(event.target.value);
          }}
          placeholder="e.g. it remembers the vertical but not on the copy-quote screen"
          rows={3}
          value={note}
        />
        <div className="flex flex-wrap gap-2">
          <Button
            disabled={mode === 'sending'}
            onClick={() => {
              setMode('sending');
              void onSubmit('not_quite', note.trim() === '' ? undefined : note.trim());
            }}
            variant="primary"
          >
            Send it back
          </Button>
          <Button
            disabled={mode === 'sending'}
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
    );
  }

  return (
    <div className="flex flex-wrap gap-2">
      <Button
        onClick={() => {
          void onSubmit('works');
        }}
        variant="secondary"
      >
        <Icon className="text-ok" name="check" size={15} />
        Works
      </Button>
      <Button
        onClick={() => {
          setMode('noting');
        }}
        variant="secondary"
      >
        <Icon className="text-ink-subtle" name="message" size={15} />
        Not quite
      </Button>
    </div>
  );
}
