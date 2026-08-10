import { useMemo } from 'react';
import qrcode from 'qrcode-generator';
import { cn } from '@/lib/cn';

export interface QrCodeProps {
  value: string;
  /** Rendered size in px. The SVG is resolution-independent; this is layout only. */
  size?: number;
  /** Describes what scanning the code does. Required — a bare "QR code" tells nobody anything. */
  label: string;
  className?: string;
}

/**
 * §27.7: "QR code for anything installable or mobile-testable. Highest-value small feature in this
 * component: it removes the 'email myself the link' step entirely."
 *
 * Drawn as one SVG path. The plate is deliberately **not** themed: a QR code is dark-on-light by
 * specification, and while some scanners cope with an inverted code, plenty do not. So it stays a
 * white plate with the mark's ink even in dark mode — it reads as a physical card, which is what it
 * is. The quiet zone is drawn explicitly; scanners need it.
 *
 * `qrcode-generator` (MIT, zero dependencies) does the encoding. Hand-rolling Reed–Solomon to save
 * 12 kB would be a bad trade against a code that silently fails to scan.
 */
export function QrCode({ value, size = 132, label, className }: QrCodeProps) {
  const { path, extent } = useMemo(() => {
    // Type 0 selects the smallest version that fits; 'M' is the usual 15% recovery level and
    // survives a phone camera at an angle.
    const qr = qrcode(0, 'M');
    qr.addData(value);
    qr.make();

    const count = qr.getModuleCount();
    const quiet = 4;
    const commands: string[] = [];

    for (let row = 0; row < count; row += 1) {
      let runStart = -1;
      for (let col = 0; col <= count; col += 1) {
        const dark = col < count && qr.isDark(row, col);
        if (dark && runStart === -1) {
          runStart = col;
        } else if (!dark && runStart !== -1) {
          commands.push(`M${runStart + quiet} ${row + quiet}h${col - runStart}v1h-${col - runStart}z`);
          runStart = -1;
        }
      }
    }

    return { path: commands.join(''), extent: count + quiet * 2 };
  }, [value]);

  return (
    <svg
      className={cn('border-line rounded-[0.375rem] border', className)}
      height={size}
      role="img"
      shapeRendering="crispEdges"
      viewBox={`0 0 ${extent} ${extent}`}
      width={size}
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>{label}</title>
      <rect fill="#ffffff" height={extent} rx="1" width={extent} x="0" y="0" />
      <path d={path} fill="#14171B" />
    </svg>
  );
}
