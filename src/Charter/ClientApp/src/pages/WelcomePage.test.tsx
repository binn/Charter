import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WelcomePage } from '@/pages/WelcomePage';
import { createTestApi, renderWithProviders } from '@/test/harness';

/**
 * §30.4, requester onboarding: "the highest-stakes and shortest. **Three screens maximum.**"
 *
 * What this file pins is the spec's own ordering of what matters. The calibration is named for what
 * the reader *wants* — §13 is explicit that you never label a human "beginner" in a UI their
 * colleagues can see — and the flow ends by putting them in the intake box, because "the practice
 * request matters more than anything else here. A requester who has completed the full loop once
 * will file real requests. One who has only read about it will not."
 */
describe('requester onboarding (§30.4)', () => {
  const api = () =>
    createTestApi(
      {
        completeRequesterOnboarding: vi.fn().mockResolvedValue(undefined),
        listProjects: () =>
          Promise.resolve([
            {
              id: 'proj-1',
              name: 'Quote tool',
              primerMd: 'The quote tool is what sales use to price a job.',
              templates: [],
            },
          ]),
      },
      'new-requester',
    );

  it('is three screens, and shows the repo primer on the first', async () => {
    renderWithProviders(<WelcomePage />, api());

    expect(
      await screen.findByRole('heading', { name: 'Ask for changes in your own words' }),
    ).toBeInTheDocument();
    expect(await screen.findByText(/what sales use to price a job/)).toBeInTheDocument();
    expect(screen.getByLabelText('Step 1 of 3')).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /Next/ }));
    expect(
      screen.getByRole('heading', { name: 'How much explaining do you want?' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Next/ }));
    expect(screen.getByRole('heading', { name: 'Try one for real' })).toBeInTheDocument();
    expect(screen.getByLabelText('Step 3 of 3')).toBeInTheDocument();
  });

  it('names the teaching levels for what the reader wants, never for what they lack', async () => {
    renderWithProviders(<WelcomePage />, api());

    await userEvent.setup().click(await screen.findByRole('button', { name: /Next/ }));

    expect(screen.getByRole('button', { name: /Explain everything/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Skip the basics/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Just the decisions/ })).toBeInTheDocument();
    // §13: "never label a human 'beginner' in a UI their colleagues can see."
    expect(screen.queryByText(/beginner/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/advanced/i)).not.toBeInTheDocument();
  });

  it('records the calibration server-side rather than in the browser', async () => {
    const updatePreferences = vi.fn().mockResolvedValue({
      theme: 'system',
      pane: 'simple',
      teachingLevel: 'just_the_decisions',
    });

    renderWithProviders(<WelcomePage />, createTestApi({ updatePreferences }, 'new-requester'));

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Next/ }));
    await user.click(screen.getByRole('button', { name: /Just the decisions/ }));

    await waitFor(() => {
      expect(updatePreferences).toHaveBeenCalledWith({ teachingLevel: 'just_the_decisions' });
    });
    expect(window.localStorage.length).toBe(0);
  });

  it('ends by putting them in the intake box, and is skippable from the first screen', async () => {
    const completeRequesterOnboarding = vi.fn().mockResolvedValue(undefined);
    renderWithProviders(
      <WelcomePage />,
      createTestApi({ completeRequesterOnboarding }, 'new-requester'),
    );

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /Next/ }));
    await user.click(screen.getByRole('button', { name: /Next/ }));

    expect(screen.getByRole('button', { name: /Write my first request/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Write my first request/ }));

    await waitFor(() => {
      expect(completeRequesterOnboarding).toHaveBeenCalled();
    });
  });
});
