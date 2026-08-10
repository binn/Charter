import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { RefinementMessage, RefinementThread } from '@/api/types';
import { CharterMark } from '@/components/CharterMark';
import { Button } from '@/components/ui/Button';
import { Icon } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { cn } from '@/lib/cn';
import { formatRelative } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

export interface RefinementConversationProps {
  thread: RefinementThread;
  onSend: (body: string) => Promise<void>;
}

/**
 * §10: "Refinement is a conversation, not a form."
 *
 * So this is a chat surface — messages in a column, a composer at the bottom, quick replies where
 * Charter offered choices, and a typing indicator so a pause reads as thinking rather than as
 * broken. There is no field labelled "acceptance criteria" and no wizard steps; the structured Spec
 * comes out the far end of the conversation, in the confirmation card.
 *
 * A `refusal` gets its own treatment. §10 requires refinement to refuse rather than dispatch
 * something ambiguous or out of scope, and §8 makes path scope a reviewable guardrail — so being
 * told no has to feel like the system working, not like an error. It says what happened, in plain
 * words, and says who has it now.
 */
export function RefinementConversation({ thread, onSend }: RefinementConversationProps) {
  const now = useNow(60_000);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }, [thread.messages.length, thread.charterIsThinking]);

  const send = (body: string) => {
    const trimmed = body.trim();
    if (trimmed === '' || sending) {
      return;
    }
    setSending(true);
    setDraft('');
    void onSend(trimmed).finally(() => {
      setSending(false);
    });
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    send(draft);
  };

  const last = thread.messages.at(-1);

  return (
    <section aria-label="Working out what you need" className="flex flex-col gap-4">
      <ol className="space-y-4">
        {thread.messages.map((message) => (
          <MessageRow key={message.id} message={message} now={now} onChoose={send} />
        ))}
      </ol>

      {thread.charterIsThinking ? (
        <p aria-live="polite" className="text-small text-ink-muted flex items-center gap-2.5">
          <CharterMark className="animate-pulse-soft" size={18} />
          Charter is thinking
          <span aria-hidden="true" className="animate-pulse-soft">
            &hellip;
          </span>
        </p>
      ) : null}

      <div ref={endRef} />

      {thread.canReply ? (
        <form className="flex flex-col gap-2" onSubmit={onSubmit}>
          <label className="sr-only" htmlFor="refinement-composer">
            Reply
          </label>
          <div className="border-line-strong bg-surface focus-within:border-accent flex items-end gap-2 rounded-card border p-2 transition-colors">
            <textarea
              className="text-base text-ink placeholder:text-ink-subtle max-h-40 min-h-11 flex-1 resize-none bg-transparent px-2 py-2 focus:outline-none"
              disabled={sending}
              id="refinement-composer"
              onChange={(event) => {
                setDraft(event.target.value);
              }}
              onKeyDown={(event) => {
                // Enter sends, Shift+Enter breaks the line — the convention everyone already knows.
                if (event.key === 'Enter' && !event.shiftKey) {
                  event.preventDefault();
                  send(draft);
                }
              }}
              placeholder={
                last?.kind === 'question' ? 'Answer in your own words…' : 'Add anything else…'
              }
              rows={1}
              value={draft}
            />
            <Button
              aria-label="Send"
              disabled={sending || draft.trim() === ''}
              type="submit"
              variant="primary"
            >
              <Icon name="arrowRight" size={16} />
            </Button>
          </div>
          <p className="text-tiny text-ink-subtle px-1">
            Nothing gets built until you have seen a written plan and approved it.
          </p>
        </form>
      ) : null}
    </section>
  );
}

function MessageRow({
  message,
  now,
  onChoose,
}: {
  message: RefinementMessage;
  now: number;
  onChoose: (body: string) => void;
}) {
  const fromRequester = message.author === 'requester';

  if (message.kind === 'refusal') {
    return (
      <li className="border-warn-line bg-warn-soft rounded-card border px-4 py-3.5">
        <p className="text-warn text-small flex items-center gap-2 font-medium">
          <Icon name="alert" size={15} />
          Charter stopped here
        </p>
        <div className="text-ink mt-2">
          <Markdown>{message.body}</Markdown>
        </div>
        {message.routedToEngineer ? (
          <p className="text-small text-ink-muted mt-2.5">
            This is with an engineer now. You will hear back on this same thread.
          </p>
        ) : null}
      </li>
    );
  }

  return (
    <li className={cn('flex gap-3', fromRequester && 'flex-row-reverse')}>
      {fromRequester ? (
        <span className="bg-sunken text-ink-subtle grid size-7 shrink-0 place-items-center rounded-full">
          <Icon name="user" size={14} />
        </span>
      ) : (
        <span className="border-line bg-surface grid size-7 shrink-0 place-items-center rounded-full border">
          <CharterMark size={15} />
        </span>
      )}

      <div className={cn('min-w-0 max-w-[42rem] flex-1', fromRequester && 'flex flex-col items-end')}>
        <div
          className={cn(
            'rounded-card px-3.5 py-2.5',
            fromRequester
              ? 'bg-accent text-accent-ink'
              : 'border-line bg-surface text-ink border',
          )}
        >
          <Markdown className="text-base">{message.body}</Markdown>
        </div>

        {message.choices && message.choices.length > 0 ? (
          <div className="mt-2 flex flex-wrap gap-2">
            {message.choices.map((choice) => (
              <Button
                key={choice.id}
                onClick={() => {
                  onChoose(choice.label);
                }}
                size="sm"
                variant="secondary"
              >
                {choice.label}
              </Button>
            ))}
          </div>
        ) : null}

        <p className="text-tiny text-ink-subtle mt-1.5 px-1">
          {formatRelative(message.createdAt, now)}
        </p>
      </div>
    </li>
  );
}
