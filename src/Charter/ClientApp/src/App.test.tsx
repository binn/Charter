import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import App from '@/App';

/**
 * A smoke test over the real composition — router, providers, shell, mock API — because every
 * component below is only as correct as the tree that mounts it.
 *
 * It also pins the one thing on the page that is a legal obligation rather than a design choice:
 * the AGPL §13 source link (§24). If that ever stops rendering, this fails.
 */
describe('App', () => {
  it('mounts, loads the viewer, and lands on the requests list', async () => {
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Requests' })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('renders the AGPL section 13 source link for this instance', async () => {
    render(<App />);

    const link = await screen.findByRole('link', { name: /source code for this instance/i });
    expect(link).toHaveAttribute('href', expect.stringContaining('github.com'));
    expect(screen.getByText(/AGPL-3\.0-only/)).toBeInTheDocument();
  });

  it('offers a skip link before anything else in the tab order', async () => {
    render(<App />);
    await screen.findByRole('heading', { name: 'Requests' });

    expect(screen.getByRole('link', { name: /skip to content/i })).toHaveAttribute(
      'href',
      '#main',
    );
  });
});
