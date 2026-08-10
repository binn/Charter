import type { AgentCapability } from '@/api/types';

/**
 * Explains a routing decision the server already made.
 *
 * §27.3 matches a session's required capabilities against what each runner advertises, and that
 * matching happens server-side — `QueuedSessionDemand.eligibleAgentIds` is the answer. What is
 * missing from that answer is *why*, and "why did my Mac mini not pick this up" is the entire
 * reason an engineer opens this page.
 *
 * So this is a presentation-layer explanation, never a decision. If it ever disagrees with
 * `eligibleAgentIds`, the server is right and this is a display bug — the UI says as much rather
 * than quietly showing its own verdict.
 */

export interface RequirementMatch {
  requirement: string;
  /** The advertised capability that covers it, or `null` when nothing does. */
  satisfiedBy: AgentCapability | null;
}

/**
 * `xcode:16` is satisfied by `xcode:16.2`, because §32.2 probes exact versions
 * (`xcodebuild -version` → `16.2`) while a session asks for a floor. Bare requirements like `linux`
 * match by id alone.
 */
export function satisfiedBy(
  requirement: string,
  capabilities: AgentCapability[],
): AgentCapability | null {
  const exact = capabilities.find((capability) => capability.id === requirement);
  if (exact) {
    return exact;
  }

  const separator = requirement.indexOf(':');
  if (separator === -1) {
    return null;
  }

  const name = requirement.slice(0, separator);
  const wanted = requirement.slice(separator + 1);

  return (
    capabilities.find(
      (capability) =>
        capability.id.startsWith(`${name}:`) && (capability.version ?? '').startsWith(wanted),
    ) ?? null
  );
}

export function matchRequirements(
  requirements: string[],
  capabilities: AgentCapability[],
): RequirementMatch[] {
  return requirements.map((requirement) => ({
    requirement,
    satisfiedBy: satisfiedBy(requirement, capabilities),
  }));
}
