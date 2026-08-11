import type { MergeGate } from '@/api/types';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { StatusPill } from '@/components/ui/StatusPill';

/**
 * §9 step 6 and §7.4, said in words rather than in a colour.
 *
 * Charter has no merge button. That half of the guarantee is Charter's own and never moves. The
 * other half belongs to the provider: **where the base branch has no protection rule requiring
 * review, nothing stops a person merging an agent's pull request unreviewed**, and this is the one
 * place in the product where the trust boundary is weaker than it looks.
 *
 * So an advisory repository gets a sentence saying exactly that, in the same plain language as
 * everything else. It warns; it never blocks (§9) — an operator who knows the risk and accepts it is
 * making a legitimate choice, and a wizard that refused to continue would just teach people to lie
 * to it.
 */
export function MergeGateCard({ gate }: { gate: MergeGate }) {
  const enforced = gate.enforcement === 'provider_enforced';

  return (
    <Card className="px-4 py-5 sm:px-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <SectionLabel>Who stops an unreviewed merge</SectionLabel>
        <StatusPill icon={enforced ? 'check' : 'alert'} tone={enforced ? 'good' : 'warn'}>
          {enforced ? 'Your provider enforces review' : 'Advisory only'}
        </StatusPill>
      </div>

      <p className="text-small text-ink-muted mt-2">
        Charter has no merge button and never will — it opens a pull request and stops. Whether
        anything stops <em>a person</em> merging it without review is your provider’s job, and on{' '}
        <code className="font-mono">{gate.branch}</code> that is{' '}
        {enforced ? 'configured' : 'not configured'}.
      </p>

      {enforced ? (
        <p className="text-small text-ink-muted mt-3 flex items-start gap-2">
          <Icon className="text-ok mt-0.5" name="check" size={15} />
          <span>
            A protection rule covers <code className="font-mono">{gate.branch}</code> and requires a
            review before anything merges. Nothing an agent produces can ship without a human
            approving it.
          </span>
        </p>
      ) : (
        <div className="border-warn-line bg-warn-soft rounded-control mt-3 border px-3 py-2.5">
          <p className="text-small text-warn flex items-start gap-2">
            <Icon className="mt-0.5" name="alert" size={15} />
            <span>
              {gate.warning ??
                `${gate.branch} has no rule requiring review, so nothing stops somebody merging an agent’s pull request without reading it. Charter will not merge it — that is the only half of this Charter can guarantee.`}
            </span>
          </p>
          <p className="text-tiny text-warn mt-2">
            {gate.protectionConfigured
              ? 'There is a protection rule on this branch, but it does not require a review.'
              : 'There is no protection rule on this branch at all. Supported is not the same as configured.'}{' '}
            Add one in your provider’s branch settings, then re-run onboarding to re-check.
          </p>
        </div>
      )}
    </Card>
  );
}
