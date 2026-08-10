import { useState } from 'react';
import type {
  BuildArtifactPayload,
  CapturePayload,
  DistributionChannelPayload,
  EphemeralInstancePayload,
  HilReportPayload,
  HostedPreviewPayload,
  NoVerificationPayload,
  TestReportPayload,
  VerificationArtifact,
} from '@/api/types';
import { CopyButton } from '@/components/ui/CopyButton';
import { Disclosure } from '@/components/ui/Disclosure';
import { Icon, type IconName } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { QrCode } from '@/components/ui/QrCode';
import { SegmentedControl } from '@/components/ui/SegmentedControl';
import { formatBytes, formatDuration } from '@/lib/format';
import { cn } from '@/lib/cn';

/**
 * The polymorphic body of the verification artifact card (§27.7).
 *
 * One component per `kind`, dispatched through an exhaustive switch — the discriminated union in
 * `api/types.ts` means adding a kind server-side without adding a body here fails the build, which
 * is the point of typing it that way.
 */
export function ArtifactBody({ artifact }: { artifact: VerificationArtifact }) {
  switch (artifact.kind) {
    case 'hosted_preview':
      return <HostedPreviewBody payload={artifact.payload} />;
    case 'build_artifact':
      return <BuildArtifactBody payload={artifact.payload} />;
    case 'distribution_channel':
      return <DistributionChannelBody payload={artifact.payload} />;
    case 'capture':
      return <CaptureBody payload={artifact.payload} />;
    case 'ephemeral_instance':
      return <EphemeralInstanceBody payload={artifact.payload} />;
    case 'test_report':
      return <TestReportBody payload={artifact.payload} />;
    case 'hil_report':
      return <HilReportBody payload={artifact.payload} />;
    case 'none':
      return <NoVerificationBody payload={artifact.payload} />;
  }
}

/* -------------------------------------------------------------------------- */

const REACHABILITY: Record<
  HostedPreviewPayload['reachability'],
  { label: string; icon: IconName; className: string }
> = {
  checking: { label: 'Checking it is up', icon: 'hourglass', className: 'text-ink-muted' },
  reachable: { label: 'Responding', icon: 'check', className: 'text-ok' },
  unreachable: { label: 'Not responding', icon: 'alert', className: 'text-danger' },
  unknown: { label: 'Not checked', icon: 'clock', className: 'text-ink-muted' },
};

function HostedPreviewBody({ payload }: { payload: HostedPreviewPayload }) {
  const reach = REACHABILITY[payload.reachability];

  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0 flex-1 space-y-3">
        <div className="border-line bg-sunken flex items-center gap-2 rounded-control border px-3 py-2">
          <Icon className="text-ink-subtle" name="eye" size={15} />
          <span className="text-small text-ink min-w-0 flex-1 truncate font-mono" title={payload.url}>
            {payload.displayUrl || payload.url}
          </span>
          <CopyButton label="Copy preview link" size="sm" value={payload.url} variant="ghost" />
        </div>

        {/* Reachability is an icon plus words, never a bare coloured dot. */}
        <p className={cn('text-small inline-flex items-center gap-1.5', reach.className)}>
          <Icon name={reach.icon} size={14} />
          {reach.label}
        </p>
      </div>

      <div className="flex shrink-0 flex-col items-center gap-1.5">
        <QrCode label="Scan to open this preview on your phone" size={112} value={payload.url} />
        <p className="text-tiny text-ink-subtle">Scan to open on your phone</p>
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */

const PLATFORM_ICONS: Record<BuildArtifactPayload['platform'], IconName> = {
  android: 'phone',
  ios: 'phone',
  windows: 'monitor',
  macos: 'monitor',
  linux: 'monitor',
  embedded: 'chip',
  other: 'package',
};

const PLATFORM_LABELS: Record<BuildArtifactPayload['platform'], string> = {
  android: 'Android',
  ios: 'iPhone or iPad',
  windows: 'Windows',
  macos: 'Mac',
  linux: 'Linux',
  embedded: 'Device firmware',
  other: 'File',
};

function BuildArtifactBody({ payload }: { payload: BuildArtifactPayload }) {
  const installable = payload.platform === 'android' || payload.platform === 'ios';

  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0 flex-1 space-y-3">
        <div className="flex items-start gap-3">
          <span className="border-line bg-sunken text-ink-muted grid size-9 shrink-0 place-items-center rounded-control border">
            <Icon name={PLATFORM_ICONS[payload.platform]} size={18} />
          </span>
          <div className="min-w-0">
            <p className="text-ink truncate font-medium" title={payload.filename}>
              {payload.filename}
            </p>
            <p className="text-small text-ink-muted">
              {PLATFORM_LABELS[payload.platform]} &middot; {formatBytes(payload.sizeBytes)}
              <span aria-hidden="true"> &middot; </span>
              <span className="font-mono" title={`${payload.checksumAlgorithm} checksum`}>
                {payload.checksumShort}
              </span>
            </p>
          </div>
        </div>

        {payload.installInstructionsMd ? (
          <Disclosure summary="How to install this">
            <div className="text-small text-ink-muted">
              <Markdown>{payload.installInstructionsMd}</Markdown>
            </div>
          </Disclosure>
        ) : null}
      </div>

      {installable ? (
        <div className="flex shrink-0 flex-col items-center gap-1.5">
          <QrCode label="Scan to download this build on your phone" size={112} value={payload.downloadUrl} />
          <p className="text-tiny text-ink-subtle">Scan to install on your phone</p>
        </div>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function DistributionChannelBody({ payload }: { payload: DistributionChannelPayload }) {
  return (
    <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0 flex-1 space-y-3">
        <div className="flex items-start gap-3">
          <span className="border-line bg-sunken text-ink-muted grid size-9 shrink-0 place-items-center rounded-control border">
            <Icon name="phone" size={18} />
          </span>
          <div className="min-w-0">
            <p className="text-ink font-medium">
              {payload.channelName}
              {payload.buildNumber ? (
                <>
                  <span aria-hidden="true" className="text-ink-subtle"> &middot; </span>
                  <span className="text-ink-muted font-normal">Build {payload.buildNumber}</span>
                </>
              ) : null}
            </p>
            <p className="text-small text-ink-muted">Install it on the device you actually use.</p>
          </div>
        </div>

        {payload.inviteRequired ? (
          <p className="border-warn-line bg-warn-soft text-warn text-small flex items-start gap-2 rounded-control border px-3 py-2">
            <Icon className="mt-0.5" name="alert" size={14} />
            <span>{payload.inviteNote ?? 'You need an invite before this will open. Ask and we will send one.'}</span>
          </p>
        ) : null}
      </div>

      <div className="flex shrink-0 flex-col items-center gap-1.5">
        <QrCode label="Scan to open this build on your device" size={112} value={payload.openUrl} />
        <p className="text-tiny text-ink-subtle">Scan with the device</p>
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function CaptureBody({ payload }: { payload: CapturePayload }) {
  const [index, setIndex] = useState(0);
  const [view, setView] = useState<'after' | 'before'>('after');
  const item = payload.items[index];

  if (!item) {
    return <p className="text-ink-muted text-small">No screenshots were captured for this run.</p>;
  }

  const showing = view === 'before' && item.baselineUrl ? item.baselineUrl : item.url;

  return (
    <div className="space-y-3">
      {item.baselineUrl ? (
        <SegmentedControl
          label="Compare with how it looked before"
          onChange={setView}
          segments={[
            { value: 'before', label: 'Before' },
            { value: 'after', label: 'After' },
          ]}
          size="sm"
          value={view}
        />
      ) : null}

      <figure className="space-y-2">
        <div className="border-line bg-sunken overflow-hidden rounded-control border">
          {item.mediaType === 'video' ? (
            <video
              className="w-full"
              controls
              poster={item.posterUrl ?? undefined}
              preload="metadata"
              src={item.url}
            >
              <track kind="captions" />
            </video>
          ) : (
            <img alt={item.caption} className="block w-full" loading="lazy" src={showing} />
          )}
        </div>
        <figcaption className="text-small text-ink-muted">
          {item.caption}
          {view === 'before' && item.baselineUrl ? ' (how it looked before)' : ''}
        </figcaption>
      </figure>

      {payload.items.length > 1 ? (
        <div className="flex flex-wrap items-center gap-2">
          {payload.items.map((candidate, candidateIndex) => (
            <button
              aria-current={candidateIndex === index}
              className={cn(
                'text-tiny rounded-control border px-2.5 py-1 font-medium transition-colors',
                candidateIndex === index
                  ? 'border-accent-line bg-accent-soft text-accent-soft-ink'
                  : 'border-line text-ink-muted hover:border-line-strong hover:text-ink',
              )}
              key={candidate.id}
              onClick={() => {
                setIndex(candidateIndex);
                setView('after');
              }}
              type="button"
            >
              {candidateIndex + 1} of {payload.items.length}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function EphemeralInstanceBody({ payload }: { payload: EphemeralInstancePayload }) {
  return (
    <div className="space-y-3">
      <div className="border-line bg-sunken flex items-center gap-2 rounded-control border px-3 py-2">
        <Icon className="text-ink-subtle" name="server" size={15} />
        <span className="text-small text-ink min-w-0 flex-1 truncate font-mono">
          {payload.connectString}
        </span>
        <CopyButton label="Copy connect string" size="sm" value={payload.connectString} variant="ghost" />
      </div>

      <dl className="text-small grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt className="text-ink-subtle">Protocol</dt>
        <dd className="text-ink font-mono">{payload.protocol}</dd>
        {payload.region ? (
          <>
            <dt className="text-ink-subtle">Region</dt>
            <dd className="text-ink font-mono">{payload.region}</dd>
          </>
        ) : null}
      </dl>

      {payload.note ? <p className="text-small text-ink-muted">{payload.note}</p> : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function TestReportBody({ payload }: { payload: TestReportPayload }) {
  const total = payload.passed + payload.failed + payload.skipped;
  const passedPct = total === 0 ? 0 : (payload.passed / total) * 100;
  const failedPct = total === 0 ? 0 : (payload.failed / total) * 100;
  const allPassed = payload.failed === 0;

  return (
    <div className="space-y-3">
      {/* Icon + words carry the verdict. The bar is a third channel, not the only one. */}
      <p
        className={cn(
          'inline-flex items-center gap-2 font-medium',
          allPassed ? 'text-ok' : 'text-danger',
        )}
      >
        <Icon name={allPassed ? 'check' : 'cross'} size={16} />
        {allPassed ? 'Everything passed' : `${payload.failed} of ${total} checks failed`}
      </p>

      <div
        aria-label={`${payload.passed} passed, ${payload.failed} failed, ${payload.skipped} skipped`}
        className="border-line bg-sunken flex h-2.5 overflow-hidden rounded-full border"
        role="img"
      >
        <span
          className="bg-ok h-full"
          style={{ width: `${passedPct}%` }}
        />
        <span
          className="bg-danger h-full"
          style={{ width: `${failedPct}%` }}
        />
      </div>

      <dl className="text-small flex flex-wrap gap-x-5 gap-y-1">
        <div className="flex gap-1.5">
          <dt className="text-ink-subtle">Passed</dt>
          <dd className="text-ink font-medium">{payload.passed}</dd>
        </div>
        <div className="flex gap-1.5">
          <dt className="text-ink-subtle">Failed</dt>
          <dd className="text-ink font-medium">{payload.failed}</dd>
        </div>
        <div className="flex gap-1.5">
          <dt className="text-ink-subtle">Skipped</dt>
          <dd className="text-ink font-medium">{payload.skipped}</dd>
        </div>
        <div className="flex gap-1.5">
          <dt className="text-ink-subtle">Took</dt>
          <dd className="text-ink font-medium">{formatDuration(payload.durationMs)}</dd>
        </div>
      </dl>

      {payload.failures.length > 0 ? (
        <Disclosure summary={`What failed (${payload.failures.length})`}>
          <ul className="space-y-2">
            {payload.failures.map((failure) => (
              <li className="border-danger-line bg-danger-soft rounded-control border px-3 py-2" key={failure.id}>
                <p className="text-small text-ink font-medium">{failure.name}</p>
                {failure.suite ? (
                  <p className="text-tiny text-ink-muted">{failure.suite}</p>
                ) : null}
                <pre className="text-tiny text-ink-muted mt-1.5 overflow-x-auto font-mono whitespace-pre-wrap">
                  {failure.assertion}
                </pre>
              </li>
            ))}
          </ul>
        </Disclosure>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function HilReportBody({ payload }: { payload: HilReportPayload }) {
  const passed = payload.outcome === 'pass';

  return (
    <div className="space-y-3">
      <p
        className={cn(
          'inline-flex items-center gap-2 font-medium',
          passed ? 'text-ok' : 'text-danger',
        )}
      >
        <Icon name={passed ? 'check' : 'cross'} size={16} />
        {passed ? 'The run passed' : 'The run failed'}
      </p>

      <dl className="text-small grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt className="text-ink-subtle">Device</dt>
        <dd className="text-ink">
          {payload.deviceLabel} <span className="text-ink-subtle font-mono">{payload.deviceId}</span>
        </dd>
        <dt className="text-ink-subtle">Ran for</dt>
        <dd className="text-ink">{formatDuration(payload.runDurationMs)}</dd>
      </dl>

      {payload.traces.length > 0 ? (
        <ul className="space-y-2">
          {payload.traces.map((trace) => (
            <li className="border-line bg-sunken rounded-control border px-3 py-2" key={trace.id}>
              <p className="text-small text-ink font-medium">{trace.label}</p>
              {trace.summary ? (
                <p className="text-small text-ink-muted mt-0.5">{trace.summary}</p>
              ) : null}
              {trace.imageUrl ? (
                <img
                  alt={trace.label}
                  className="mt-2 block w-full rounded-[0.375rem]"
                  loading="lazy"
                  src={trace.imageUrl}
                />
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function NoVerificationBody({ payload }: { payload: NoVerificationPayload }) {
  return (
    <div className="text-ink-muted flex gap-3">
      <Icon className="text-ink-subtle mt-0.5" name="user" size={18} />
      <div className="text-small">
        <Markdown>{payload.explanation}</Markdown>
      </div>
    </div>
  );
}
