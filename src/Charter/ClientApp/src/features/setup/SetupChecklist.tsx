import { useCallback, useState } from 'react';
import { Link } from 'react-router';
import { useApi } from '@/api/api-context';
import type { SetupChecklist as SetupChecklistData, SetupTask } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { useAsync } from '@/hooks/useAsync';
import { cn } from '@/lib/cn';

/**
 * §30.2, the admin setup checklist. **A persistent dashboard checklist, not a modal wizard.**
 *
 * The spec gives the reason and it is worth restating, because a wizard is the obvious build:
 * "modal wizards trap people who need to go find a token." Connecting GitHub means leaving to
 * install an app. Adding a model credential means leaving to copy a key. A dialog that owns the
 * screen while you do that is hostile, and one that loses your place when you come back is worse.
 *
 * So: it lives on the page the admin lands on, it never blocks anything, no step is disabled, and
 * it survives being abandoned halfway. `blockedBy` produces a sentence explaining the sensible
 * order, not a lock — someone who wants to do step six first is allowed to.
 *
 * It renders only when the API sent a checklist, which it does only for an admin. A requester's
 * dashboard has no checklist rather than an empty one.
 */
export function SetupChecklist() {
  const api = useApi();
  const load = useCallback((signal: AbortSignal) => api.getSetupChecklist(signal), [api]);
  const state = useAsync(load);
  const [dismissed, setDismissed] = useState(false);

  if (state.status !== 'ready' || state.data === null || dismissed) {
    return null;
  }

  const checklist: SetupChecklistData = state.data;
  if (checklist.dismissedAt !== undefined) {
    return null;
  }

  const done = checklist.tasks.filter((task) => task.done).length;
  const total = checklist.tasks.length;
  const complete = done === total;

  return (
    <Card className="mb-6 px-4 py-5 sm:px-5">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <SectionLabel>Setting up Charter</SectionLabel>
        <p className="text-tiny text-ink-subtle tabular-nums">
          {done} of {total} done
        </p>
      </div>

      {/*
       * A meter, not a decorative bar: `role="progressbar"` with real values, plus the count above
       * in text. Progress conveyed only by a coloured strip is progress a screen reader cannot see.
       */}
      <div
        aria-label="Setup progress"
        aria-valuemax={total}
        aria-valuemin={0}
        aria-valuenow={done}
        aria-valuetext={`${done} of ${total} steps done`}
        className="bg-sunken mt-2 h-1.5 w-full overflow-hidden rounded-full"
        role="progressbar"
      >
        <div
          className="bg-accent h-full rounded-full transition-[width] duration-300"
          style={{ width: `${Math.round((done / total) * 100)}%` }}
        />
      </div>

      <p className="text-small text-ink-muted mt-3">
        {complete
          ? 'Everything is set up. You can put this away — it will not come back.'
          : 'Leave and come back whenever you like; nothing here has to be done in one sitting.'}
      </p>

      <ol className="mt-4 space-y-1">
        {checklist.tasks.map((task) => (
          <li key={task.id}>
            <SetupRow task={task} tasks={checklist.tasks} />
          </li>
        ))}
      </ol>

      {complete ? (
        <div className="mt-4">
          <Button
            onClick={() => {
              setDismissed(true);
              void api.dismissSetupChecklist();
            }}
            variant="secondary"
          >
            <Icon name="check" size={15} />
            Hide this checklist
          </Button>
        </div>
      ) : null}
    </Card>
  );
}

function SetupRow({ task, tasks }: { task: SetupTask; tasks: SetupTask[] }) {
  const blocker =
    task.blockedBy === undefined
      ? undefined
      : tasks.find((candidate) => candidate.id === task.blockedBy);
  const blocked = blocker !== undefined && !blocker.done;

  const body = (
    <>
      <span
        className={cn(
          'mt-0.5 grid size-5 shrink-0 place-items-center rounded-full border',
          task.done ? 'border-ok bg-ok-soft text-ok' : 'border-line-strong text-ink-subtle',
        )}
      >
        {task.done ? <Icon name="check" size={12} /> : null}
      </span>

      <span className="min-w-0 flex-1">
        <span className="flex flex-wrap items-baseline gap-x-2">
          <span className={cn('font-medium', task.done ? 'text-ink-muted' : 'text-ink')}>
            {task.title}
          </span>
          {task.done && task.doneSummary ? (
            <span className="text-tiny text-ok">{task.doneSummary}</span>
          ) : null}
          {task.external && !task.done ? (
            <span className="text-tiny text-ink-subtle inline-flex items-center gap-1">
              <Icon name="external" size={11} />
              leaves Charter
            </span>
          ) : null}
        </span>
        <span className="text-small text-ink-muted mt-0.5 block">{task.description}</span>
        {blocked ? (
          <span className="text-tiny text-ink-subtle mt-1 block">
            Easier once &ldquo;{blocker.title}&rdquo; is done — but you can do it now if you prefer.
          </span>
        ) : null}
      </span>

      {task.done ? null : (
        <Icon className="text-ink-subtle mt-1 shrink-0" name="chevronRight" size={14} />
      )}
    </>
  );

  const className = cn(
    'hover:bg-sunken flex w-full gap-3 rounded-control px-2 py-2 text-left transition-colors',
  );

  if (task.done) {
    return <div className={cn(className, 'hover:bg-transparent')}>{body}</div>;
  }

  return task.external ? (
    <a className={className} href={task.href} rel="noreferrer" target="_blank">
      {body}
    </a>
  ) : (
    <Link className={className} to={task.href}>
      {body}
    </Link>
  );
}
