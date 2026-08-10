import type { SetupChecklist } from '@/api/types';

/**
 * §30.2, the admin setup checklist.
 *
 * Deliberately part-done: four of seven, with the two that matter most already ticked and a
 * blocked one in the middle. A checklist fixture that is empty or complete shows neither the
 * progress rendering nor the "you cannot start this one yet" case, and those are the two states
 * this component exists for.
 *
 * The order matches §30.2 exactly. It is not sorted by done-ness — reordering a checklist under
 * someone as they tick things off is disorienting, and the sequence is itself the advice.
 */
export function makeSetupChecklist(): SetupChecklist {
  return {
    tasks: [
      {
        id: 'name_organisation',
        title: 'Name your organisation',
        description: 'It appears on every page and in the emails Charter sends.',
        done: true,
        href: '/settings',
        doneSummary: 'Northbeam Solar',
      },
      {
        id: 'connect_github',
        title: 'Connect GitHub',
        description: 'Install the Charter GitHub App on the account that owns your repositories.',
        done: true,
        href: 'https://github.com/apps/charter/installations/new',
        external: true,
        doneSummary: 'Installed on northbeam',
      },
      {
        id: 'add_model_credential',
        title: 'Add a model credential',
        description: 'Nothing can be refined or built until Charter can reach a model.',
        done: true,
        href: '/settings',
        doneSummary: 'Anthropic · subscription OAuth',
      },
      {
        id: 'connect_repository',
        title: 'Connect your first repository',
        description:
          'Charter reads it, proposes a scope config, and runs a smoke test before anyone can file against it.',
        done: true,
        href: '/projects',
        doneSummary: '1 repository · quote-tool',
      },
      {
        id: 'set_budgets',
        title: 'Set budgets',
        description:
          'In a small team the monthly cap does more governing than the approval queue ever will.',
        done: false,
        href: '/settings',
      },
      {
        id: 'invite_people',
        title: 'Invite people',
        description: 'Charter is not much use with one account on it.',
        done: false,
        href: '/settings',
      },
      {
        id: 'notification_channels',
        title: 'Choose notification channels',
        description:
          'Only two states notify — a question for the requester, and something ready to try.',
        done: false,
        href: '/settings',
        blockedBy: 'invite_people',
      },
    ],
  };
}
