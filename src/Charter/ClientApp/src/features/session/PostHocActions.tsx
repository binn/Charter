import { useState } from 'react';
import { useApi } from '@/api/api-context';
import type { Id, SessionActions } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { ConfirmDestructive } from '@/components/ui/ConfirmDestructive';
import { Icon } from '@/components/ui/Icon';
import { TextArea } from '@/components/ui/Field';
import { formatRelative } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

export interface PostHocActionsProps {
  requestId: Id;
  actions: SessionActions;
  specTitle?: string;
  /** Refetch the request once an action lands, so the thread and status catch up. */
  onDone: () => void;
}

type OpenPanel = 'none' | 'steer' | 'revise';

/**
 * §7.5's four post-hoc actions, **all first-class**.
 *
 * They sit as four peers rather than one primary button and an overflow menu, because the spec is
 * explicit that a reviewer arriving at an auto-dispatched session has four legitimate answers and
 * no default one. Which are offered comes from the server (`canApprove` and friends); an action the
 * viewer may not take is not rendered, rather than rendered disabled.
 *
 * **Take over is deliberately the odd one out.** It is drawn in the danger treatment, it demands the
 * branch name typed out, and its confirmation says plainly what stops. §7.5: "an agent and a human
 * editing the same branch concurrently is the one genuinely destructive failure mode in this
 * design." A control that reads like the other three would get pressed like the other three.
 */
export function PostHocActions({ requestId, actions, specTitle, onDone }: PostHocActionsProps) {
  const api = useApi();
  const now = useNow(60_000);
  const [panel, setPanel] = useState<OpenPanel>('none');
  const [instruction, setInstruction] = useState('');
  const [revisedSpec, setRevisedSpec] = useState(
    specTitle === undefined ? '' : `**${specTitle}**\n\n`,
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = (work: Promise<void>) => {
    setBusy(true);
    setError(null);
    work
      .then(() => {
        setPanel('none');
        setInstruction('');
        onDone();
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'That did not go through.');
      })
      .finally(() => {
        setBusy(false);
      });
  };

  /*
   * §7.5: once someone has taken over, Charter "stops touching it — no further agent writes to that
   * branch". So the panel becomes a statement of fact rather than a set of controls. Leaving Steer
   * behind, greyed out, would imply the hand-off is a mode you can toggle off.
   */
  if (actions.handedOff) {
    return (
      <Card className="px-4 py-4 sm:px-5">
        <SectionLabel>This session was taken over</SectionLabel>
        <p className="text-small text-ink mt-2 flex items-start gap-2">
          <Icon className="text-ink-subtle mt-0.5 shrink-0" name="user" size={15} />
          <span>
            {actions.handedOff.byName} took over {formatRelative(actions.handedOff.at, now)} and is
            finishing <span className="font-mono">{actions.branch}</span> by hand. Charter is not
            writing to that branch any more, and cannot be asked to.
          </span>
        </p>
      </Card>
    );
  }

  const offersAnything =
    actions.canApprove || actions.canSteer || actions.canRevise || actions.canTakeOver;

  if (!offersAnything) {
    return null;
  }

  return (
    <Card className="px-4 py-5 sm:px-5">
      <SectionLabel>What happens next</SectionLabel>
      <p className="text-small text-ink-muted mt-1.5">
        Working on <span className="text-ink font-mono">{actions.branch}</span>. Merging still happens
        on your provider — Charter has no merge button.
      </p>

      <div className="mt-4 flex flex-wrap gap-2">
        {actions.canApprove ? (
          <Button
            disabled={busy}
            onClick={() => {
              run(api.approveSession(requestId));
            }}
            variant="primary"
          >
            <Icon name="check" size={15} />
            Approve
          </Button>
        ) : null}

        {actions.canSteer ? (
          <Button
            aria-expanded={panel === 'steer'}
            disabled={busy}
            onClick={() => {
              setPanel((current) => (current === 'steer' ? 'none' : 'steer'));
            }}
          >
            <Icon name="message" size={15} />
            Steer
          </Button>
        ) : null}

        {actions.canRevise ? (
          <Button
            aria-expanded={panel === 'revise'}
            disabled={busy}
            onClick={() => {
              setPanel((current) => (current === 'revise' ? 'none' : 'revise'));
            }}
          >
            <Icon name="refresh" size={15} />
            Revise and rebuild
          </Button>
        ) : null}
      </div>

      {panel === 'steer' ? (
        <div className="border-line rounded-control mt-4 border p-3.5">
          <p className="text-small text-ink-muted mb-3">
            Continues this session on the same branch, in the same thread. It keeps everything it has
            already worked out.
          </p>
          <TextArea
            label="What should it do differently?"
            onChange={(event) => {
              setInstruction(event.target.value);
            }}
            placeholder="Use the employee id rather than the auth user id, and add a test for two accounts belonging to one person."
            rows={3}
            value={instruction}
          />
          <div className="mt-3 flex gap-2">
            <Button
              disabled={busy || instruction.trim().length === 0}
              onClick={() => {
                run(api.steerSession(requestId, instruction.trim()));
              }}
              variant="primary"
            >
              {busy ? 'Sending…' : 'Send instruction'}
            </Button>
            <Button
              disabled={busy}
              onClick={() => {
                setPanel('none');
              }}
              variant="ghost"
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : null}

      {panel === 'revise' ? (
        <div className="border-line rounded-control mt-4 border p-3.5">
          <p className="text-small text-ink-muted mb-3">
            Forks the specification, and dispatches a fresh session onto the same branch. Use this
            when the plan was wrong rather than the execution.
          </p>
          <TextArea
            label="The revised specification"
            onChange={(event) => {
              setRevisedSpec(event.target.value);
            }}
            rows={7}
            value={revisedSpec}
          />
          <div className="mt-3 flex gap-2">
            <Button
              disabled={busy || revisedSpec.trim().length === 0}
              onClick={() => {
                run(api.reviseSession(requestId, revisedSpec.trim()));
              }}
              variant="primary"
            >
              {busy ? 'Dispatching…' : 'Rebuild from this'}
            </Button>
            <Button
              disabled={busy}
              onClick={() => {
                setPanel('none');
              }}
              variant="ghost"
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : null}

      {error ? (
        <p className="text-small text-danger mt-3" role="alert">
          {error}
        </p>
      ) : null}

      {actions.canTakeOver ? (
        <div className="border-line mt-5 border-t pt-4">
          <p className="text-small text-ink-muted mb-3">
            Or finish it yourself. This is the one action here you cannot undo.
          </p>
          <ConfirmDestructive
            confirmLabel="Stop agent writes and take over"
            confirmPhrase={actions.branch}
            consequences={[
              <>
                Charter stops writing to{' '}
                <span className="font-mono">{actions.branch}</span> permanently. Steer and Revise
                will no longer be offered on this request.
              </>,
              'Any session still running on this branch is stopped and its cost settled.',
              'The requester is told an engineer has picked it up, not that anything failed.',
              'Nothing already committed is removed — the branch is yours as it stands.',
            ]}
            onConfirm={() => api.takeOverSession(requestId).then(onDone)}
            phraseLabel="branch name"
            title="Take over this branch"
            triggerIcon="key"
            triggerLabel="Take over"
          />
        </div>
      ) : null}
    </Card>
  );
}
