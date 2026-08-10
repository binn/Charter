import type { RequestStatus } from '@/api/types';

/**
 * §6, requester-facing labels.
 *
 * One table, used by the list row, the detail header and the empty-state copy, so those three can
 * never disagree. Internal state names never reach the screen.
 *
 * `notifies` records the two states that actually send anything — "only two states notify.
 * Notifying on all of them gets Charter muted within a week." It is here so the badge and the
 * notification policy are read off the same line.
 */
export interface RequesterStatus {
  label: string;
  /** A short sentence for the detail header, where there is room to be kind. */
  blurb?: string;
  tone: 'neutral' | 'active' | 'attention' | 'good' | 'bad';
  notifies: boolean;
}

const TABLE: Record<RequestStatus, RequesterStatus> = {
  draft: {
    label: 'Not sent yet',
    tone: 'neutral',
    notifies: false,
  },
  refining: {
    label: "Let's figure out what you need",
    blurb: 'A few questions first, so nobody builds the wrong thing.',
    tone: 'active',
    notifies: false,
  },
  spec_ready: {
    label: 'Waiting to be approved',
    blurb: 'Someone needs to okay the cost before this starts.',
    tone: 'neutral',
    notifies: false,
  },
  rejected: {
    label: 'Not going ahead',
    tone: 'neutral',
    notifies: false,
  },
  queued: {
    label: 'Building this now',
    tone: 'active',
    notifies: false,
  },
  running: {
    label: 'Building this now',
    tone: 'active',
    notifies: false,
  },
  needs_input: {
    label: 'Question for you',
    blurb: 'This is paused until you answer.',
    tone: 'attention',
    notifies: true,
  },
  pr_open: {
    label: 'Building this now',
    tone: 'active',
    notifies: false,
  },
  preview_ready: {
    label: 'Ready to try',
    tone: 'attention',
    notifies: true,
  },
  in_review: {
    label: 'An engineer is checking it',
    tone: 'neutral',
    notifies: false,
  },
  merged: {
    label: 'This is live',
    tone: 'good',
    notifies: false,
  },
  no_changes_needed: {
    label: 'Nothing needed changing',
    blurb:
      'Most often that means what you asked for already works the way you wanted. Nothing went wrong, and there is nothing for you to do.',
    tone: 'good',
    notifies: false,
  },
  failed: {
    label: 'This turned out to be bigger than expected',
    blurb: 'An engineer has been told. You do not need to do anything.',
    tone: 'bad',
    notifies: false,
  },
  cancelled: {
    label: 'You stopped this',
    tone: 'neutral',
    notifies: false,
  },
  stale: {
    label: 'Out of date',
    blurb: 'The app moved on underneath this. It needs building again.',
    tone: 'neutral',
    notifies: false,
  },
};

export function requesterStatus(status: RequestStatus): RequesterStatus {
  return TABLE[status];
}

/** True while a build is in flight — drives the live subscription and the elapsed timer. */
export function isInFlight(status: RequestStatus): boolean {
  return status === 'queued' || status === 'running' || status === 'pr_open';
}
