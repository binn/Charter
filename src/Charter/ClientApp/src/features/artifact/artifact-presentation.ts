import type { ArtifactKind, ArtifactState, VerificationArtifact } from '@/api/types';
import type { IconName } from '@/components/ui/Icon';
import type { Tone } from '@/components/ui/StatusPill';
import { expiryOf } from '@/lib/format';

/**
 * Presentation rules for the verification artifact card (§27.7), kept out of the component so they
 * can be unit-tested and read as a table.
 *
 * The rule that shapes all of this: **pass/fail must never rely on colour alone.** Every state
 * below carries an icon *and* a text label, and the colour is the third channel, not the first.
 */

export interface ArtifactStateStyle {
  label: string;
  icon: IconName;
  tone: Tone;
}

/**
 * The state actually rendered, which is not always the state the server sent.
 *
 * A card can sit open on a monitor for hours, and someone can arrive from a three-day-old
 * notification. §27.7 calls expired previews "the number one source of confusion" precisely because
 * the server's view goes stale: so the client re-derives expiry from `expiresAt` on every tick.
 * `ready` becomes `expiring` when it drops under an hour, and `expiring`/`ready` become `expired`
 * the moment the clock runs out — without a refetch and without a dead link in between.
 */
export function effectiveState(artifact: VerificationArtifact, now: number): ArtifactState {
  if (artifact.state === 'pending' || artifact.state === 'failed') {
    return artifact.state;
  }

  const expiry = expiryOf(artifact.expiresAt, now);
  if (!expiry) {
    return artifact.state;
  }
  if (expiry.status === 'expired') {
    return 'expired';
  }
  if (expiry.status === 'expiring') {
    return 'expiring';
  }
  return artifact.state === 'expired' ? 'expired' : 'ready';
}

export function artifactStateStyle(
  state: ArtifactState,
  kind: ArtifactKind,
): ArtifactStateStyle {
  if (kind === 'none' && (state === 'ready' || state === 'expiring')) {
    // §27.4: do not pretend parity. This one is honestly engineer-verified.
    return { label: 'Checked by an engineer', icon: 'user', tone: 'neutral' };
  }

  switch (state) {
    case 'pending':
      return { label: 'Building this now', icon: 'hourglass', tone: 'active' };
    case 'ready':
    case 'expiring':
      return { label: 'Ready to try', icon: 'check', tone: 'good' };
    case 'expired':
      return { label: 'Cleaned up', icon: 'clock', tone: 'neutral' };
    case 'failed':
      return { label: 'Nothing to try this time', icon: 'alert', tone: 'bad' };
  }
}

/** Host names that only ever mean "the machine this is running on". */
const LOCAL_HOST_SUFFIXES = ['.localhost', '.local', '.internal', '.home.arpa'];

/**
 * Whether a preview URL is one Charter can honestly offer as a button.
 *
 * The server refuses these before it stores one and again before it renders one (§16.3); this is the
 * same structural rule at the last possible moment, because the card's own copy — *"Nothing you do
 * here touches the real one"* — is a promise made on Charter's authority to the person least able to
 * evaluate the link. A requester must never be handed a `http://127.0.0.1:8080` or a
 * `http://169.254.169.254/…` under that sentence, whatever the API sent.
 *
 * Structural only, and deliberately permissive about private ranges: a self-hoster whose preview
 * genuinely lives at `http://10.0.4.12:3000` has a working link, and the browser cannot resolve a
 * name anyway. What it rejects is what is never legitimate.
 */
export function isDisplayablePreviewUrl(url: string | undefined | null): boolean {
  if (!url) {
    return false;
  }

  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return false;
  }
  // Credentials in a link are how a link is made to look like somewhere it is not.
  if (parsed.username !== '' || parsed.password !== '') {
    return false;
  }

  const host = parsed.hostname.toLowerCase().replace(/^\[|]$/g, '');
  if (host === '' || host === 'localhost') {
    return false;
  }
  if (LOCAL_HOST_SUFFIXES.some((suffix) => host.endsWith(suffix))) {
    return false;
  }

  const ipv4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  if (ipv4) {
    const [first, second] = [Number(ipv4[1]), Number(ipv4[2])];
    // Loopback, "this network", link-local (where every cloud metadata service lives), multicast.
    if (first === 0 || first === 127 || first >= 224 || (first === 169 && second === 254)) {
      return false;
    }
    return true;
  }

  if (host.includes(':')) {
    // IPv6 literal: loopback, unspecified, link-local, multicast.
    if (host === '::1' || host === '::') {
      return false;
    }
    if (/^fe[89ab]/.test(host) || host.startsWith('ff')) {
      return false;
    }
  }

  return true;
}

export interface PrimaryAction {
  label: string;
  /** `link` opens `href`; `copy` writes `value` to the clipboard; `rebuild` calls the API. */
  behaviour: 'link' | 'copy' | 'rebuild' | 'none';
  href?: string;
  value?: string;
  icon: IconName;
  /** Anything installable or mobile-testable gets a QR code — §27.7's "highest-value small feature". */
  qrValue?: string;
  download?: boolean;
}

const NO_ACTION: PrimaryAction = { label: '', behaviour: 'none', icon: 'eye' };

export function primaryActionFor(
  artifact: VerificationArtifact,
  state: ArtifactState,
): PrimaryAction {
  if (state === 'expired') {
    // §27.7: expiry is a designed state, and the way out of it is to build it again.
    return { label: 'Build it again', behaviour: 'rebuild', icon: 'refresh' };
  }
  if (state === 'pending' || state === 'failed') {
    return NO_ACTION;
  }

  switch (artifact.kind) {
    case 'hosted_preview':
      // No link, no copy, no QR code for a URL Charter cannot vouch for. The body says so in words;
      // offering the button anyway would be the card asserting the one thing it does not know.
      return isDisplayablePreviewUrl(artifact.payload.url)
        ? {
            label: 'Open preview',
            behaviour: 'link',
            href: artifact.payload.url,
            icon: 'arrowRight',
            qrValue: artifact.payload.url,
          }
        : NO_ACTION;
    case 'build_artifact':
      return {
        label: 'Download',
        behaviour: 'link',
        href: artifact.payload.downloadUrl,
        icon: 'download',
        download: true,
        // Sideloadable builds are exactly the case where "email myself the link" happens.
        ...(artifact.payload.platform === 'android' || artifact.payload.platform === 'ios'
          ? { qrValue: artifact.payload.downloadUrl }
          : {}),
      };
    case 'distribution_channel':
      return {
        label: `Open in ${artifact.payload.channelName}`,
        behaviour: 'link',
        href: artifact.payload.openUrl,
        icon: 'phone',
        qrValue: artifact.payload.openUrl,
      };
    case 'capture': {
      const first = artifact.payload.items[0];
      return first
        ? { label: 'View full size', behaviour: 'link', href: first.url, icon: 'eye' }
        : NO_ACTION;
    }
    case 'ephemeral_instance':
      return {
        label: 'Copy connect string',
        behaviour: 'copy',
        value: artifact.payload.connectString,
        icon: 'copy',
      };
    case 'test_report':
      return artifact.payload.reportUrl
        ? {
            label: 'View full report',
            behaviour: 'link',
            href: artifact.payload.reportUrl,
            icon: 'external',
          }
        : NO_ACTION;
    case 'hil_report':
      return artifact.payload.reportUrl
        ? { label: 'View run', behaviour: 'link', href: artifact.payload.reportUrl, icon: 'external' }
        : NO_ACTION;
    case 'none':
      return NO_ACTION;
  }
}

const KIND_ICONS: Record<ArtifactKind, IconName> = {
  hosted_preview: 'eye',
  build_artifact: 'package',
  distribution_channel: 'phone',
  capture: 'image',
  ephemeral_instance: 'server',
  test_report: 'list',
  hil_report: 'chip',
  none: 'user',
};

export function artifactIcon(kind: ArtifactKind): IconName {
  return KIND_ICONS[kind];
}

/** §27.7: multiple artifacts render as tabs in one card, **primary first**. */
export function orderArtifacts(artifacts: VerificationArtifact[]): VerificationArtifact[] {
  return [...artifacts].sort((a, b) => Number(b.primary) - Number(a.primary));
}
