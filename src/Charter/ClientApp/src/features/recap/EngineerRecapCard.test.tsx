import { describe, expect, it } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { derateRecap, heroRecap } from '@/api/mock/fixtures-session';
import { EngineerRecapCard } from '@/features/recap/EngineerRecapCard';
import { PaneSelectionProvider } from '@/features/panes/PaneSelectionProvider';

/** With a Developer pane present, the recap's file paths are controls that open it (§12). */
function renderLinked(recap: Parameters<typeof EngineerRecapCard>[0]['recap']) {
  return render(
    <PaneSelectionProvider>
      <EngineerRecapCard recap={recap} />
    </PaneSelectionProvider>,
  );
}

const NOW = Date.parse('2026-06-07T12:00:00.000Z');

describe('engineer recap (§14)', () => {
  it('lists files risk-first, in the order the server sent them', () => {
    renderLinked(heroRecap(NOW));

    const heading = screen.getByText('Files, riskiest first');
    const list = heading.parentElement?.querySelector('ul') as HTMLElement;
    const paths = within(list)
      .getAllByRole('button')
      .map((button) => button.textContent ?? '');

    // Auth and the migration above the Razor markup and the tests — §14's whole point. Alphabetical
    // order would put the migration first by accident and the tests second, which teaches nothing.
    expect(paths[0]).toContain('CurrentUserAccessor.cs');
    expect(paths[1]).toContain('AddUserQuotePreference.cs');
    expect(paths[paths.length - 1]).toContain('NewQuoteDefaultsTests.cs');
  });

  it('explains why a file ranks where it does', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    expect(screen.getByText(/Database migration · Additive/)).toBeInTheDocument();
    expect(screen.getByText(/Resolves the signed-in user/)).toBeInTheDocument();
  });

  it('separates "the spec said X and it did Y" from "the spec was silent"', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    expect(screen.getByText(/The spec said:/)).toBeInTheDocument();
    expect(screen.getByText('The spec did not cover this.')).toBeInTheDocument();
  });

  it('shows what could not be verified and where to start reading', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    expect(screen.getByText('What it could not verify')).toBeInTheDocument();
    expect(screen.getByText(/two tabs/)).toBeInTheDocument();
    expect(screen.getByText('Suggested reading order')).toBeInTheDocument();
  });

  it('never renders a verdict, and says so', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    // §14: "It must never say 'looks good.'"
    expect(screen.queryByText(/looks good/i)).toBeNull();
    expect(screen.getByText(/orientation aid/)).toBeInTheDocument();
    expect(screen.getByText(/Read the diff\./)).toBeInTheDocument();
  });

  it('leads with the fact that nobody approved an auto-dispatched spec (§7.5)', () => {
    render(<EngineerRecapCard recap={derateRecap(NOW)} />);

    expect(
      screen.getByText('Nobody approved this specification before it was built.'),
    ).toBeInTheDocument();
    // And carries the spec in full rather than a summary of it.
    expect(screen.getByText('The specification, in full')).toBeInTheDocument();
  });

  it('does not cry auto-dispatch on a session that went through the spend gate', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    expect(screen.queryByText('Nobody approved this specification before it was built.')).toBeNull();
    expect(screen.queryByText('The specification, in full')).toBeNull();
  });

  it('points at the provider, where the review actually happens (§14)', () => {
    render(<EngineerRecapCard recap={heroRecap(NOW)} />);

    const link = screen.getByRole('link', { name: 'pull request' });
    expect(link).toHaveAttribute(
      'href',
      'https://github.com/northbeam/quote-tool/pull/142#issuecomment-1',
    );
  });

  it('says so when the provider has nowhere to post it', () => {
    render(<EngineerRecapCard recap={derateRecap(NOW)} />);

    expect(screen.getByText(/this view is the only copy/)).toBeInTheDocument();
  });

  it('opens pane 3 from a file in the recap, when there is a pane 3 to open', async () => {
    const user = userEvent.setup();
    renderLinked(heroRecap(NOW));

    // The same path is a control in three places — the deviation, the file list and the reading
    // order — and all three go to the same file.
    const controls = screen.getAllByRole('button', { name: /CurrentUserAccessor\.cs/ });
    expect(controls.length).toBeGreaterThan(1);
    await user.click(controls[0] as HTMLElement);

    // The selection is announced, which is the observable half of the linkage from here.
    await waitFor(() => {
      expect(
        screen.getByText(/Showing changes to .*CurrentUserAccessor\.cs/),
      ).toBeInTheDocument();
    });
  });

  it('renders file paths as text when there is no Developer pane to link into', () => {
    // A session that failed before writing anything has a recap and no diff. Rendering dead
    // buttons there would be worse than rendering text.
    render(<EngineerRecapCard recap={derateRecap(NOW)} />);

    expect(
      screen.queryByRole('button', { name: /DerateCalculator\.cs/ }),
    ).toBeNull();
    expect(screen.getAllByText(/DerateCalculator\.cs/).length).toBeGreaterThan(0);
  });
});
