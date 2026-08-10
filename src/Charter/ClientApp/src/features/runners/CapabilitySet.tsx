import type { AgentCapability } from '@/api/types';
import { Icon } from '@/components/ui/Icon';
import { matchRequirements } from '@/features/runners/capability-matching';
import { cn } from '@/lib/cn';

export interface CapabilitySetProps {
  capabilities: AgentCapability[];
  /**
   * When a waiting session is selected, the set switches from "what this agent has" to "what this
   * session needs, and whether this agent has it" — which is the question actually being asked.
   */
  requirements?: string[];
}

/**
 * What an agent advertises (§32.2), rendered so the routing question answers itself.
 *
 * §32.2 is emphatic that capabilities are **probed, not declared** — `xcodebuild -version` produced
 * `xcode:16.2`, nobody typed it in. That distinction is worth surfacing, because the failure it
 * prevents is an agent confidently advertising an Xcode that was upgraded out from under it. Each
 * chip carries the command that found it.
 *
 * In requirement mode the same data is re-sorted into the session's terms: one row per requirement,
 * each either met by a named capability or plainly absent. A missing requirement is the answer to
 * "why did this not route here", and it is written as a sentence rather than left as a red dot.
 */
export function CapabilitySet({ capabilities, requirements }: CapabilitySetProps) {
  if (requirements !== undefined) {
    const matches = matchRequirements(requirements, capabilities);

    return (
      <ul className="space-y-1">
        {matches.map((match) => (
          <li className="text-tiny flex items-start gap-2" key={match.requirement}>
            <Icon
              className={cn('mt-0.5 shrink-0', match.satisfiedBy ? 'text-ok' : 'text-danger')}
              name={match.satisfiedBy ? 'check' : 'cross'}
              size={12}
            />
            <span className="font-mono">{match.requirement}</span>
            <span className={cn(match.satisfiedBy ? 'text-ink-muted' : 'text-danger')}>
              {match.satisfiedBy
                ? `met by ${match.satisfiedBy.id}`
                : 'not advertised by this agent'}
            </span>
          </li>
        ))}
      </ul>
    );
  }

  if (capabilities.length === 0) {
    return (
      <p className="text-tiny text-ink-subtle">
        Nothing probed yet. Capabilities appear after the agent&rsquo;s first successful heartbeat.
      </p>
    );
  }

  return (
    <ul className="flex flex-wrap gap-1.5">
      {capabilities.map((capability) => (
        <li key={capability.id}>
          <span
            className="border-line bg-sunken text-tiny text-ink-muted inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 font-mono"
            title={
              capability.probedBy
                ? `${capability.label} — found by \`${capability.probedBy}\``
                : capability.label
            }
          >
            <span className="text-ink">{capability.id}</span>
          </span>
        </li>
      ))}
    </ul>
  );
}
