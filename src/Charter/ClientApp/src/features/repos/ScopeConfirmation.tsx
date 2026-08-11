import { useMemo, useState } from 'react';
import type { ScopeProposal } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { cn } from '@/lib/cn';

/**
 * §9 step 3: "visual file tree with allow/deny toggles. Defaults denied: migrations, auth, CI
 * config, infra, secrets."
 *
 * Two things this deliberately does not do:
 *
 * - **It does not offer a toggle it cannot honour.** The floor above is enforced server-side; a
 *   confirmation that sent `allow: ["src/auth/**"]` would be filtered and the engineer would be left
 *   believing something untrue. So those rows render as denials with the reason attached, and say
 *   who is enforcing them.
 * - **It does not pretend this is the enforcement.** Path scope applies to the *agent* and is
 *   enforced in the runner (§7.3), not here. This screen writes `.charter/config.yml` as a pull
 *   request; the runner is what refuses to write outside it.
 */
export function ScopeConfirmation({
  proposal,
  busy,
  onConfirm,
}: {
  proposal: ScopeProposal;
  busy: boolean;
  onConfirm: (allow: string[], deny: string[]) => void;
}) {
  const [allowed, setAllowed] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(
      proposal.entries.map((entry) => [entry.path, entry.locked === true ? false : entry.allowed]),
    ),
  );

  const { open, locked } = useMemo(
    () => ({
      open: proposal.entries.filter((entry) => entry.locked !== true),
      locked: proposal.entries.filter((entry) => entry.locked === true),
    }),
    [proposal.entries],
  );

  const allowCount = open.filter((entry) => allowed[entry.path] === true).length;

  return (
    <Card className="px-4 py-5 sm:px-5">
      <SectionLabel>What Charter may change</SectionLabel>
      <p className="text-small text-ink-muted mt-1.5">
        Recon read {proposal.detectedStack.join(', ')} and proposed the split below. Confirming it
        opens <code className="font-mono">.charter/config.yml</code> as a pull request — you review
        it like any other change, and you can widen or narrow it later.
      </p>

      {proposal.importedFrom && proposal.importedFrom.length > 0 ? (
        <p className="text-small text-ink-muted mt-2 flex items-start gap-2">
          <Icon className="text-ok mt-0.5" name="check" size={14} />
          <span>
            Imported your existing {proposal.importedFrom.join(' and ')} and extended it. Nothing in
            it was overwritten.
          </span>
        </p>
      ) : null}

      <fieldset className="mt-4">
        <legend className="text-small text-ink font-medium">
          Allowed — the agent may read and change these
        </legend>
        <ul className="mt-2 space-y-1">
          {open.map((entry) => {
            const on = allowed[entry.path] === true;
            return (
              <li key={entry.path}>
                <label
                  className={cn(
                    'rounded-control flex cursor-pointer items-start gap-3 border px-3 py-2.5 transition-colors',
                    on ? 'border-accent-line bg-accent-soft' : 'border-line hover:border-line-strong',
                  )}
                >
                  <input
                    checked={on}
                    className="accent-accent focus-visible:outline-focus mt-0.5 size-4 focus-visible:outline-2 focus-visible:outline-offset-2"
                    onChange={(event) => {
                      setAllowed((current) => ({ ...current, [entry.path]: event.target.checked }));
                    }}
                    type="checkbox"
                  />
                  <span className="min-w-0 flex-1">
                    <span className="text-ink block font-mono text-small">{entry.path}</span>
                    {entry.reason ? (
                      <span className="text-tiny text-ink-muted block">{entry.reason}</span>
                    ) : null}
                  </span>
                  <span className="text-tiny text-ink-subtle shrink-0">
                    {on ? 'Allowed' : 'Denied'}
                  </span>
                </label>
              </li>
            );
          })}
        </ul>
      </fieldset>

      {locked.length > 0 ? (
        <div className="mt-5">
          <h3 className="text-small text-ink font-medium">
            Always denied, whatever you choose here
          </h3>
          <p className="text-small text-ink-muted mt-1">
            Charter refuses these itself and filters them out of anything this page sends, so there
            is no toggle to give you. A request that needs one of them is routed to an engineer with
            the conversation attached, rather than refused outright.
          </p>
          <ul className="mt-2 space-y-1">
            {locked.map((entry) => (
              <li
                className="border-line rounded-control flex items-start gap-3 border border-dashed px-3 py-2.5"
                key={entry.path}
              >
                <Icon className="text-ink-subtle mt-0.5" name="key" size={15} />
                <span className="min-w-0 flex-1">
                  <span className="text-ink-muted block font-mono text-small">{entry.path}</span>
                  {entry.reason ? (
                    <span className="text-tiny text-ink-subtle block">{entry.reason}</span>
                  ) : null}
                </span>
                <span className="text-tiny text-ink-subtle shrink-0">Denied</span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {proposal.commands.length > 0 ? (
        <dl className="border-line mt-5 grid grid-cols-[5rem_1fr] gap-x-4 gap-y-1.5 border-t pt-4">
          {proposal.commands.map((command) => (
            <div className="contents" key={command.label}>
              <dt className="text-small text-ink-subtle">{command.label}</dt>
              <dd className="text-small text-ink overflow-x-auto font-mono">{command.command}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      <div className="mt-5 flex flex-wrap items-center gap-3">
        <Button
          disabled={busy}
          onClick={() => {
            onConfirm(
              open.filter((entry) => allowed[entry.path] === true).map((entry) => entry.path),
              [
                ...open.filter((entry) => allowed[entry.path] !== true).map((entry) => entry.path),
                ...locked.map((entry) => entry.path),
              ],
            );
          }}
          size="lg"
          variant="primary"
        >
          {busy ? 'Opening the pull request…' : 'Confirm this scope'}
        </Button>
        <p className="text-small text-ink-muted">
          {allowCount} of {open.length} allowed. This also queues the smoke test.
        </p>
      </div>
    </Card>
  );
}
