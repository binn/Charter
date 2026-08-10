import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { RequesterSpec } from '@/api/types';
import { SpecConfirmationCard } from '@/features/spec/SpecConfirmationCard';

/**
 * The point of these tests is the *absence* of things.
 *
 * §10b: the requester view renders `title`, `outcome` and `acceptance_criteria`, and nothing else.
 * §7.4: engineer-only fields are omitted by the API, never hidden with CSS. So the strongest test
 * available is that a payload carrying engineer-only fields — which the real API will never send,
 * but a regression might — still puts none of them on screen.
 */

const SPEC: RequesterSpec = {
  id: 'spec-1',
  version: 2,
  title: 'Remember the last selected vertical',
  outcome:
    'When you start a new quote, the vertical you chose last time is already selected.',
  acceptanceCriteria: [
    { id: 'ac-1', text: 'Starting a new quote pre-selects the vertical you chose last time.' },
    { id: 'ac-2', text: 'Changing the vertical on a quote still works exactly as it does now.' },
  ],
};

function renderCard(spec: RequesterSpec = SPEC) {
  const onApprove = vi.fn().mockResolvedValue(undefined);
  const onRequestChanges = vi.fn().mockResolvedValue(undefined);
  render(
    <SpecConfirmationCard onApprove={onApprove} onRequestChanges={onRequestChanges} spec={spec} />,
  );
  return { onApprove, onRequestChanges };
}

describe('SpecConfirmationCard — what it renders', () => {
  it('renders the title, the outcome and every acceptance criterion verbatim', () => {
    renderCard();

    expect(screen.getByRole('heading', { name: SPEC.title })).toBeInTheDocument();
    expect(screen.getByText(SPEC.outcome)).toBeInTheDocument();
    for (const criterion of SPEC.acceptanceCriteria) {
      expect(screen.getByText(criterion.text)).toBeInTheDocument();
    }
  });

  it('asks for approval of the criteria, which is the thing a requester can judge', async () => {
    const user = userEvent.setup();
    const { onApprove } = renderCard();

    await user.click(screen.getByRole('button', { name: /yes, build this/i }));
    expect(onApprove).toHaveBeenCalledOnce();
  });

  it('lets the requester send it back rather than approving something wrong', async () => {
    const user = userEvent.setup();
    const { onRequestChanges } = renderCard();

    await user.click(screen.getByRole('button', { name: /not quite right/i }));
    await user.type(
      screen.getByLabelText(/what is not right/i),
      'it should only remember it for me',
    );
    await user.click(screen.getByRole('button', { name: /send this back/i }));

    expect(onRequestChanges).toHaveBeenCalledWith('it should only remember it for me');
  });
});

describe('SpecConfirmationCard — field omission (§7.4, §10b)', () => {
  it('renders nothing from engineer-only fields even when they are smuggled into the payload', () => {
    // `RequesterSpec` has no `technicalApproach`, `scope` or `risks` property — this cast is the
    // only way to build such an object, which is the guarantee under test. The API must omit these
    // for a requester; if one ever leaked through, the card must still not draw it.
    const contaminated = {
      ...SPEC,
      technicalApproach:
        'Add a `last_vertical_id` column to Users and read it in QuoteWizardController.Create.',
      scope: { files: ['src/Features/Quotes/QuoteWizardController.cs'], paths: ['src/Features/**'] },
      risks: ['Touches the Users table, which is shared with billing.'],
    } as unknown as RequesterSpec;

    renderCard(contaminated);

    expect(screen.queryByText(/last_vertical_id/)).not.toBeInTheDocument();
    expect(screen.queryByText(/QuoteWizardController/)).not.toBeInTheDocument();
    expect(screen.queryByText(/src\/Features/)).not.toBeInTheDocument();
    expect(screen.queryByText(/shared with billing/)).not.toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/technical|approach|\.cs\b|repository|branch|commit/i);
  });

  it('shows open questions, which are requester-facing, and glossary terms from the repo', () => {
    renderCard({
      ...SPEC,
      openQuestions: ['If no installer is assigned, leave the button alone for now?'],
      glossary: [{ term: 'vertical', definition: 'The kind of installation a quote is for.' }],
    });

    expect(
      screen.getByText('If no installer is assigned, leave the button alone for now?'),
    ).toBeInTheDocument();
    expect(screen.getByText('vertical')).toBeInTheDocument();
  });
});

describe('SpecConfirmationCard — once approved', () => {
  it('stops offering approval and records who approved it', () => {
    renderCard({
      ...SPEC,
      approvedAt: new Date(Date.now() - 60_000).toISOString(),
      approvedByName: 'Ayesha Rahman',
    });

    expect(screen.getByText('Approved')).toBeInTheDocument();
    expect(screen.getByText(/Ayesha Rahman/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /yes, build this/i })).not.toBeInTheDocument();
  });
});
