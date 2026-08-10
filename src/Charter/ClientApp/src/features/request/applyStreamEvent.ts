import type { RequestDetail, RequestStreamEvent } from '@/api/types';

/**
 * Folds a live event into the request detail the client already holds.
 *
 * Every case is idempotent by id: the container can restart mid-session (§2.3), the socket
 * reconnects, and the client may see the same milestone twice. Replaying an event must never
 * duplicate a row in the status thread.
 */
export function applyStreamEvent(
  current: RequestDetail,
  event: RequestStreamEvent,
): RequestDetail {
  switch (event.type) {
    case 'milestone':
    case 'milestone_updated': {
      const existing = current.thread.milestones.findIndex(
        (milestone) => milestone.id === event.milestone.id,
      );
      const milestones =
        existing === -1
          ? [...current.thread.milestones, event.milestone]
          : current.thread.milestones.map((milestone, index) =>
              index === existing ? event.milestone : milestone,
            );

      return {
        ...current,
        lastMilestoneLabel: event.milestone.label,
        thread: { ...current.thread, milestones },
      };
    }

    case 'status':
      return {
        ...current,
        status: event.status,
        ...(event.awaitingApprovalFrom === undefined
          ? {}
          : { awaitingApprovalFrom: event.awaitingApprovalFrom }),
      };

    case 'refinement_message': {
      if (current.refinement.messages.some((message) => message.id === event.message.id)) {
        return current;
      }
      return {
        ...current,
        refinement: {
          ...current.refinement,
          charterIsThinking: false,
          messages: [...current.refinement.messages, event.message],
        },
      };
    }

    case 'charter_thinking':
      return {
        ...current,
        refinement: { ...current.refinement, charterIsThinking: event.thinking },
      };

    case 'spec_proposed':
      return { ...current, spec: event.spec };

    case 'artifact': {
      const index = current.artifacts.findIndex(
        (artifact) => artifact.id === event.artifact.id,
      );
      const artifacts =
        index === -1
          ? [...current.artifacts, event.artifact]
          : current.artifacts.map((artifact, position) =>
              position === index ? event.artifact : artifact,
            );
      return { ...current, artifacts };
    }

    case 'artifact_state':
      return {
        ...current,
        artifacts: current.artifacts.map((artifact) =>
          artifact.id === event.artifactId
            ? {
                ...artifact,
                state: event.state,
                ...(event.expiresAt === undefined ? {} : { expiresAt: event.expiresAt }),
              }
            : artifact,
        ),
      };

    case 'failed':
      return {
        ...current,
        status: 'failed',
        thread: { ...current.thread, live: false, failureSummary: event.failureSummary },
      };

    case 'ended':
      return {
        ...current,
        cancellable: false,
        thread: { ...current.thread, live: false, endedAt: event.endedAt },
      };
  }
}
