import { useState } from 'react';
import { useApi } from '@/api/api-context';
import type { PairingToken } from '@/api/types';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { CopyButton } from '@/components/ui/CopyButton';
import { Icon } from '@/components/ui/Icon';
import { expiryOf, formatDuration } from '@/lib/format';
import { useNow } from '@/hooks/useNow';

/**
 * §33.3 steps 1 and 2: generate a pairing token, then show the exact command.
 *
 * The command is assembled **server-side** and rendered verbatim. Building it in the browser from
 * `window.location.origin` would be wrong roughly half the time — the machine running the agent is
 * a Mac mini in someone's office or a VPS, and there is no reason its route to the control plane
 * matches the one the admin's browser took. Getting that wrong produces an agent that will not dial
 * out and an error message that points nowhere.
 *
 * The token is shown **once**. There is no endpoint that reads it back, so this deliberately offers
 * no way to see it again — only to generate another. Pretending otherwise would be a lie about how
 * single-use credentials work.
 */
export function RegisterAgentPanel({ onRegistered }: { onRegistered: () => void }) {
  const api = useApi();
  const now = useNow(1_000);
  const [pairing, setPairing] = useState<PairingToken | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const generate = () => {
    setBusy(true);
    setError(null);
    api
      .createPairingToken()
      .then((token) => {
        setPairing(token);
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Could not generate a pairing token.');
      })
      .finally(() => {
        setBusy(false);
      });
  };

  const expiry = expiryOf(pairing?.expiresAt, now);
  const expired = expiry?.status === 'expired';

  return (
    <Card className="px-4 py-5 sm:px-5">
      <SectionLabel>Register a Charter Agent</SectionLabel>
      <p className="text-small text-ink-muted mt-1.5 max-w-prose">
        An agent runs jobs on hardware you control and dials out to Charter — no inbound ports, no
        firewall changes. Use one when a project needs a toolchain, a licence, or a device that only
        exists on your own machine.
      </p>

      {pairing === null ? (
        <div className="mt-4">
          <Button disabled={busy} onClick={generate} variant="primary">
            <Icon name="key" size={15} />
            {busy ? 'Generating…' : 'Generate a pairing token'}
          </Button>
        </div>
      ) : (
        <div className="mt-4 space-y-3">
          <ol className="text-small text-ink space-y-3">
            <li>
              <p className="font-medium">1. Install the agent on the machine that will run jobs.</p>
              <p className="text-ink-muted">
                One static binary, from the Charter releases page. Nothing else to configure.
              </p>
            </li>
            <li>
              <p className="font-medium">2. Run this command there.</p>
              <div className="border-line bg-sunken rounded-control mt-2 flex items-start gap-2 border p-3">
                <code className="text-tiny text-ink min-w-0 flex-1 font-mono break-all">
                  {pairing.command}
                </code>
                <CopyButton label="Copy the pairing command" size="sm" value={pairing.command} />
              </div>
            </li>
          </ol>

          <div
            className={
              expired
                ? 'border-danger-line bg-danger-soft rounded-control border px-3 py-2.5'
                : 'border-warn-line bg-warn-soft rounded-control border px-3 py-2.5'
            }
          >
            <p className="text-small text-ink flex items-start gap-2">
              <Icon
                className={expired ? 'text-danger mt-0.5 shrink-0' : 'text-warn mt-0.5 shrink-0'}
                name={expired ? 'cross' : 'clock'}
                size={14}
              />
              <span>
                {expired ? (
                  <>
                    <span className="font-medium">This token has expired.</span> Generate another —
                    they are cheap, and a long-lived one is a standing risk.
                  </>
                ) : (
                  <>
                    <span className="font-medium">
                      Single use, expires in {formatDuration(expiry?.remainingMs ?? 0)}.
                    </span>{' '}
                    It is shown once and cannot be retrieved again. The agent exchanges it for a
                    long-lived credential the moment it connects.
                  </>
                )}
              </span>
            </p>
          </div>

          <div className="flex flex-wrap gap-2">
            <Button
              onClick={() => {
                setPairing(null);
                onRegistered();
              }}
            >
              <Icon name="refresh" size={15} />
              Done — check for the agent
            </Button>
            <Button disabled={busy} onClick={generate} variant="ghost">
              Generate another
            </Button>
          </div>
        </div>
      )}

      {error ? (
        <p className="text-small text-danger mt-3" role="alert">
          {error}
        </p>
      ) : null}
    </Card>
  );
}
