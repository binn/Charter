import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ScopeProposal } from '@/api/types';
import { MergeGateCard } from '@/features/repos/MergeGateCard';
import { ScopeConfirmation } from '@/features/repos/ScopeConfirmation';
import { SmokeTestRun } from '@/features/repos/SmokeTestRun';

/**
 * §9 steps 3, 4 and 6 — the three places repo onboarding either earns trust or quietly loses it.
 */

function proposal(): ScopeProposal {
  return {
    detectedStack: ['React 19'],
    commands: [{ label: 'Tests', command: 'npm test' }],
    entries: [
      { path: 'src/app/', kind: 'directory', allowed: true, reason: 'The application itself' },
      { path: 'docs/', kind: 'directory', allowed: false, reason: 'Usually written by hand' },
      { path: 'db/migrations/', kind: 'directory', allowed: false, locked: true, reason: 'Database migrations' },
    ],
  };
}

describe('scope confirmation (§9 step 3)', () => {
  it('sends back exactly what the toggles say', async () => {
    const onConfirm = vi.fn();
    render(<ScopeConfirmation busy={false} onConfirm={onConfirm} proposal={proposal()} />);

    const user = userEvent.setup();
    // Allow the one recon proposed denying, and deny the one it proposed allowing.
    await user.click(screen.getByRole('checkbox', { name: /docs\// }));
    await user.click(screen.getByRole('checkbox', { name: /src\/app\// }));
    await user.click(screen.getByRole('button', { name: /Confirm this scope/ }));

    expect(onConfirm).toHaveBeenCalledWith(
      ['docs/'],
      expect.arrayContaining(['src/app/', 'db/migrations/']),
    );
  });

  it('accepts what recon proposed when nothing is touched', async () => {
    const onConfirm = vi.fn();
    render(<ScopeConfirmation busy={false} onConfirm={onConfirm} proposal={proposal()} />);

    await userEvent.setup().click(screen.getByRole('button', { name: /Confirm this scope/ }));

    expect(onConfirm).toHaveBeenCalledWith(['src/app/'], ['docs/', 'db/migrations/']);
  });

  it('gives the deny-by-default floor no toggle, and says who is enforcing it', () => {
    render(<ScopeConfirmation busy={false} onConfirm={vi.fn()} proposal={proposal()} />);

    // §7.3: the floor is enforced server-side and in the runner. A checkbox here would imply the
    // engineer could turn it off, and the confirmation would be silently filtered.
    expect(screen.queryByRole('checkbox', { name: /migrations/ })).not.toBeInTheDocument();
    expect(screen.getByText('Always denied, whatever you choose here')).toBeInTheDocument();
    expect(screen.getByText(/routed to an engineer/)).toBeInTheDocument();
  });

  it('counts what is allowed and says confirming queues the smoke test', async () => {
    render(<ScopeConfirmation busy={false} onConfirm={vi.fn()} proposal={proposal()} />);

    expect(screen.getByText(/1 of 2 allowed/)).toBeInTheDocument();

    await userEvent.setup().click(screen.getByRole('checkbox', { name: /docs\// }));
    expect(screen.getByText(/2 of 2 allowed/)).toBeInTheDocument();
    expect(screen.getByText(/queues the smoke test/)).toBeInTheDocument();
  });
});

describe('the smoke test (§9 step 4)', () => {
  it('shows the run happening, step by step, rather than a boolean', () => {
    render(
      <SmokeTestRun
        outcome={{
          passed: false,
          at: '2026-06-07T12:00:00.000Z',
          previewBound: false,
          pullRequestNumber: 128,
          checkpoints: [
            { id: 'request_filed', label: 'Filed a trivial request', state: 'passed' },
            { id: 'agent_ran', label: 'The agent ran', state: 'passed' },
            { id: 'checks_passed', label: 'Your checks passed', state: 'running' },
            { id: 'pull_request', label: 'A pull request opened', state: 'pending' },
            { id: 'preview_deployed', label: 'A preview deployed', state: 'pending' },
            { id: 'url_bound', label: 'The preview URL bound back', state: 'pending' },
          ],
        }}
        running
      />,
    );

    expect(screen.getByText('Running now')).toBeInTheDocument();
    expect(screen.getByText('Your checks passed')).toBeInTheDocument();
    expect(screen.getByText('The preview URL bound back')).toBeInTheDocument();
    // Never colour alone: each state is a word too.
    expect(screen.getAllByText('running').length).toBeGreaterThan(0);
    expect(screen.getAllByText('pending').length).toBe(3);
  });

  it('says nothing has run yet rather than showing an empty list', () => {
    render(<SmokeTestRun outcome={null} running={false} />);

    expect(screen.getByText('Not started')).toBeInTheDocument();
    expect(screen.getByText(/Confirming the scope above queues it/)).toBeInTheDocument();
  });

  it('warns about an empty preview without blocking (§9 seed data)', () => {
    render(
      <SmokeTestRun
        outcome={{
          passed: true,
          at: '2026-06-07T12:00:00.000Z',
          previewBound: true,
          warnings: ['The preview deployed but looks empty.'],
        }}
        running={false}
      />,
    );

    expect(screen.getByText('All six passed')).toBeInTheDocument();
    expect(screen.getByText('The preview deployed but looks empty.')).toBeInTheDocument();
  });
});

describe('the merge gate (§7.4, §9 step 6)', () => {
  it('says in words that an unprotected branch is only advisory', () => {
    render(
      <MergeGateCard
        gate={{
          enforcement: 'advisory',
          branch: 'main',
          protectionConfigured: false,
          requiresReview: false,
          checkedAt: '2026-06-07T12:00:00.000Z',
        }}
      />,
    );

    expect(screen.getByText('Advisory only')).toBeInTheDocument();
    expect(screen.getByText(/nothing stops somebody merging/i)).toBeInTheDocument();
    expect(screen.getByText(/Supported is not the same as configured/)).toBeInTheDocument();
  });

  it('says plainly when the provider does enforce review', () => {
    render(
      <MergeGateCard
        gate={{
          enforcement: 'provider_enforced',
          branch: 'main',
          protectionConfigured: true,
          requiresReview: true,
          checkedAt: '2026-06-07T12:00:00.000Z',
        }}
      />,
    );

    expect(screen.getByText('Your provider enforces review')).toBeInTheDocument();
    expect(screen.getByText(/requires a review before anything merges/)).toBeInTheDocument();
  });
});
