import { describe, expect, it } from 'vitest';
import type { VerificationArtifact } from '@/api/types';
import {
  artifactStateStyle,
  effectiveState,
  isDisplayablePreviewUrl,
  orderArtifacts,
  primaryActionFor,
} from '@/features/artifact/artifact-presentation';

const NOW = Date.parse('2026-08-10T12:00:00.000Z');
const at = (hours: number) => new Date(NOW + hours * 3_600_000).toISOString();

function preview(overrides: Record<string, unknown> = {}): VerificationArtifact {
  return {
    id: 'a',
    kind: 'hosted_preview',
    state: 'ready',
    audience: 'requester',
    label: 'Preview',
    primary: true,
    payload: { url: 'https://x.test', displayUrl: 'x.test', reachability: 'reachable' },
    ...overrides,
  } as VerificationArtifact;
}

describe('effectiveState', () => {
  it('leaves pending and failed alone — neither has an expiry to reason about', () => {
    expect(effectiveState(preview({ state: 'pending' }), NOW)).toBe('pending');
    expect(effectiveState(preview({ state: 'failed' }), NOW)).toBe('failed');
  });

  it('keeps a ready artifact with no expiry as ready', () => {
    expect(effectiveState(preview(), NOW)).toBe('ready');
  });

  it('promotes ready to expiring under an hour', () => {
    expect(effectiveState(preview({ expiresAt: at(0.5) }), NOW)).toBe('expiring');
    expect(effectiveState(preview({ expiresAt: at(1.5) }), NOW)).toBe('ready');
  });

  it('expires on the clock, not on the server saying so', () => {
    expect(effectiveState(preview({ state: 'ready', expiresAt: at(-0.01) }), NOW)).toBe('expired');
    expect(effectiveState(preview({ state: 'expiring', expiresAt: at(-72) }), NOW)).toBe('expired');
  });
});

describe('artifactStateStyle', () => {
  it('gives every state an icon and a text label', () => {
    for (const state of ['pending', 'ready', 'expiring', 'expired', 'failed'] as const) {
      const style = artifactStateStyle(state, 'hosted_preview');
      expect(style.label.length).toBeGreaterThan(0);
      expect(style.icon.length).toBeGreaterThan(0);
    }
  });

  it('says plainly that a `none` artifact is engineer-verified rather than implying parity', () => {
    expect(artifactStateStyle('ready', 'none').label).toBe('Checked by an engineer');
  });
});

describe('primaryActionFor', () => {
  it('makes Rebuild the primary action once expired, for every kind', () => {
    expect(primaryActionFor(preview(), 'expired').behaviour).toBe('rebuild');
  });

  it('offers no action while pending or failed', () => {
    expect(primaryActionFor(preview(), 'pending').behaviour).toBe('none');
    expect(primaryActionFor(preview(), 'failed').behaviour).toBe('none');
  });

  it('attaches a QR code to anything mobile-testable or installable', () => {
    expect(primaryActionFor(preview(), 'ready').qrValue).toBe('https://x.test');

    const testflight = primaryActionFor(
      {
        id: 'b',
        kind: 'distribution_channel',
        state: 'ready',
        audience: 'requester',
        label: 'TestFlight',
        primary: true,
        payload: {
          provider: 'testflight',
          channelName: 'TestFlight',
          openUrl: 'https://testflight.test/join',
          inviteRequired: false,
        },
      },
      'ready',
    );
    expect(testflight.qrValue).toBe('https://testflight.test/join');
  });

  it('does not attach a QR code to a desktop download nobody will scan', () => {
    const windowsBuild = primaryActionFor(
      {
        id: 'c',
        kind: 'build_artifact',
        state: 'ready',
        audience: 'requester',
        label: 'Installer',
        primary: true,
        payload: {
          platform: 'windows',
          filename: 'setup.exe',
          sizeBytes: 100,
          checksumAlgorithm: 'sha256',
          checksumShort: 'abc',
          downloadUrl: 'https://x.test/setup.exe',
        },
      },
      'ready',
    );
    expect(windowsBuild.qrValue).toBeUndefined();
  });
});

describe('orderArtifacts', () => {
  it('puts the primary artifact first so the first tab is the one that matters', () => {
    const ordered = orderArtifacts([
      preview({ id: 'secondary', primary: false }),
      preview({ id: 'primary', primary: true }),
    ]);
    expect(ordered.map((artifact) => artifact.id)).toEqual(['primary', 'secondary']);
  });
});

describe('isDisplayablePreviewUrl', () => {
  /**
   * The last gate before a URL somebody else chose becomes a button under Charter's own promise
   * that the preview is safe to click (§16.3, §27.7). The server refuses these before it stores one
   * and again before it renders one; this is the same structural rule in the browser, because a row
   * written before those checks existed is still a row the card has to handle.
   */
  it.each([
    'https://pr-142.preview.example.test/quotes/new',
    'http://myapp-pr-142.onrender.com',
    // A self-hoster's preview genuinely lives here, and the requester's browser is on that network.
    'http://10.0.4.12:3000/',
  ])('offers %s', (url) => {
    expect(isDisplayablePreviewUrl(url)).toBe(true);
  });

  it.each([
    'http://127.0.0.1:8080/',
    'http://localhost:3000/',
    'http://charter.localhost/',
    'http://[::1]:8080/',
    // Where every cloud provider parks instance metadata.
    'http://169.254.169.254/latest/meta-data/',
    'http://[fe80::1]/',
    // A link that reads as one host and authenticates to another.
    'https://admin:hunter2@preview.example.test/',
    'javascript:alert(1)',
    'file:///etc/passwd',
    'not a url at all',
    '',
  ])('withholds %s', (url) => {
    expect(isDisplayablePreviewUrl(url)).toBe(false);
  });
});

describe('primaryActionFor — a URL Charter cannot vouch for', () => {
  it('offers no button, no copy and no QR code', () => {
    const action = primaryActionFor(
      preview({ payload: { url: 'http://169.254.169.254/', displayUrl: '169.254.169.254', reachability: 'unknown' } }),
      'ready',
    );

    expect(action.behaviour).toBe('none');
    expect(action.href).toBeUndefined();
    expect(action.qrValue).toBeUndefined();
  });

  it('offers nothing for an empty URL either, which is what the API sends once it has withheld one', () => {
    const action = primaryActionFor(
      preview({ payload: { url: '', displayUrl: '', reachability: 'unknown' } }),
      'ready',
    );

    expect(action.behaviour).toBe('none');
  });
});
