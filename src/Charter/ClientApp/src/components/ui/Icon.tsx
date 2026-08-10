import type { SVGProps } from 'react';
import { cn } from '@/lib/cn';

/**
 * The icon set, hand-drawn on the same 24-unit grid and 2-unit round-capped stroke as
 * `assets/charter-mark.svg`, so icons and logo look like one drawing rather than a logo plus an
 * icon library. No icon dependency: this app ships inside someone else's container and every
 * kilobyte is theirs.
 *
 * Icons are decorative by default (`aria-hidden`). Every place a §27.7 state is drawn, the icon is
 * paired with a text label — pass/fail must never rely on colour alone, and it must not rely on a
 * glyph alone either.
 */

const PATHS = {
  check: <path d="M4.5 12.5 9.5 17.5 19.5 6.5" />,
  cross: (
    <>
      <path d="M6 6 18 18" />
      <path d="M18 6 6 18" />
    </>
  ),
  alert: (
    <>
      <path d="M12 3.5 22 20.5H2z" />
      <path d="M12 10v4.5" />
      <path d="M12 17.8v.2" />
    </>
  ),
  clock: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5.4l3.4 2" />
    </>
  ),
  hourglass: (
    <>
      <path d="M7 3h10" />
      <path d="M7 21h10" />
      <path d="M7 3c0 4.5 5 6.2 5 9s-5 4.5-5 9" />
      <path d="M17 3c0 4.5-5 6.2-5 9s5 4.5 5 9" />
    </>
  ),
  external: (
    <>
      <path d="M14 4h6v6" />
      <path d="M20 4 11 13" />
      <path d="M18.5 14.5V19a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 19V7A1.5 1.5 0 0 1 5 5.5h4.5" />
    </>
  ),
  copy: (
    <>
      <rect x="9" y="9" width="11.5" height="11.5" rx="2" />
      <path d="M6 15H5a1.5 1.5 0 0 1-1.5-1.5V5A1.5 1.5 0 0 1 5 3.5h8.5A1.5 1.5 0 0 1 15 5v1" />
    </>
  ),
  download: (
    <>
      <path d="M12 3.5v12" />
      <path d="m7 11 5 5 5-5" />
      <path d="M3.5 19.5h17" />
    </>
  ),
  arrowRight: (
    <>
      <path d="M4 12h15" />
      <path d="m13 6 6 6-6 6" />
    </>
  ),
  chevronRight: <path d="m9 5 7 7-7 7" />,
  chevronDown: <path d="m5 9 7 7 7-7" />,
  plus: (
    <>
      <path d="M12 5v14" />
      <path d="M5 12h14" />
    </>
  ),
  message: (
    <path d="M20.5 12.5a7.5 7.5 0 0 1-7.5 7.5 8 8 0 0 1-3.3-.7L4 21l1.4-4.4A7.5 7.5 0 0 1 13 5a7.5 7.5 0 0 1 7.5 7.5Z" />
  ),
  search: (
    <>
      <circle cx="11" cy="11" r="6.5" />
      <path d="m16 16 4.5 4.5" />
    </>
  ),
  wrench: (
    <path d="M20 5.5a5 5 0 0 1-6.6 6.4L6 19.3a2.2 2.2 0 1 1-3.1-3.1l7.4-7.4A5 5 0 0 1 16.8 2.2l-3 3 2 2 3-3c.7.4 1.2 1.2 1.2 1.3Z" />
  ),
  package: (
    <>
      <path d="M20.5 7.8v8.4a1.5 1.5 0 0 1-.8 1.3l-7 3.8a1.5 1.5 0 0 1-1.4 0l-7-3.8a1.5 1.5 0 0 1-.8-1.3V7.8" />
      <path d="m3.8 7 7.5-3.9a1.5 1.5 0 0 1 1.4 0L20.2 7 12 11.4Z" />
      <path d="M12 11.4V21" />
    </>
  ),
  eye: (
    <>
      <path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" />
      <circle cx="12" cy="12" r="3" />
    </>
  ),
  image: (
    <>
      <rect x="3" y="4.5" width="18" height="15" rx="2" />
      <circle cx="8.5" cy="10" r="1.6" />
      <path d="m4 17 5-4.5 4.5 4 2.5-2 4 3.5" />
    </>
  ),
  phone: (
    <>
      <rect x="6.5" y="2.5" width="11" height="19" rx="2.5" />
      <path d="M10.5 18.5h3" />
    </>
  ),
  server: (
    <>
      <rect x="3" y="4" width="18" height="7" rx="1.8" />
      <rect x="3" y="13" width="18" height="7" rx="1.8" />
      <path d="M7 7.5h.01" />
      <path d="M7 16.5h.01" />
    </>
  ),
  chip: (
    <>
      <rect x="7" y="7" width="10" height="10" rx="1.6" />
      <path d="M10 3.5v3M14 3.5v3M10 17.5v3M14 17.5v3M3.5 10h3M3.5 14h3M17.5 10h3M17.5 14h3" />
    </>
  ),
  list: (
    <>
      <path d="M9 6.5h11M9 12h11M9 17.5h11" />
      <path d="M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01" />
    </>
  ),
  spark: (
    <path d="M12 3.5 13.9 9l5.6 1.9-5.6 1.9L12 18.5 10.1 12.8 4.5 10.9 10.1 9Z" />
  ),
  refresh: (
    <>
      <path d="M20 11.5A8 8 0 0 0 6.3 6.3L3.5 9" />
      <path d="M3.5 4.5V9H8" />
      <path d="M4 12.5a8 8 0 0 0 13.7 5.2l2.8-2.7" />
      <path d="M20.5 19.5V15H16" />
    </>
  ),
  stop: <rect x="6.5" y="6.5" width="11" height="11" rx="2" />,
  sun: (
    <>
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2.5v2M12 19.5v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M2.5 12h2M19.5 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4" />
    </>
  ),
  moon: <path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5Z" />,
  monitor: (
    <>
      <rect x="3" y="4.5" width="18" height="12" rx="2" />
      <path d="M8.5 20.5h7M12 16.5v4" />
    </>
  ),
  user: (
    <>
      <circle cx="12" cy="8" r="3.8" />
      <path d="M4.5 20.5a7.5 7.5 0 0 1 15 0" />
    </>
  ),
  text: (
    <>
      <path d="M5 5.5h14" />
      <path d="M12 5.5v13" />
      <path d="M9 18.5h6" />
    </>
  ),
  layout: (
    <>
      <rect x="3.5" y="4.5" width="17" height="15" rx="2" />
      <path d="M3.5 9.5h17M9.5 9.5v10" />
    </>
  ),
  key: (
    <>
      <circle cx="8" cy="8" r="4.5" />
      <path d="m11.5 11.5 8 8" />
      <path d="m16 16 2-2M18.5 18.5l2-2" />
    </>
  ),
  qr: (
    <>
      <rect x="3.5" y="3.5" width="6.5" height="6.5" rx="1" />
      <rect x="14" y="3.5" width="6.5" height="6.5" rx="1" />
      <rect x="3.5" y="14" width="6.5" height="6.5" rx="1" />
      <path d="M14 14h3v3h-3zM20.5 14v3M17.5 20.5h3M14 20.5h.01" />
    </>
  ),
  file: (
    <>
      <path d="M13.5 3.5H7A1.5 1.5 0 0 0 5.5 5v14A1.5 1.5 0 0 0 7 20.5h10a1.5 1.5 0 0 0 1.5-1.5V8.5Z" />
      <path d="M13.5 3.5v5h5" />
    </>
  ),
  terminal: (
    <>
      <rect x="3" y="4.5" width="18" height="15" rx="2" />
      <path d="m7.5 10 2.5 2.5L7.5 15" />
      <path d="M13 15h4" />
    </>
  ),
  diff: (
    <>
      <path d="M6.5 3.5v9" />
      <circle cx="6.5" cy="16" r="2.5" />
      <path d="M17.5 20.5v-9" />
      <circle cx="17.5" cy="8" r="2.5" />
      <path d="M9 6.5h4.5A1.5 1.5 0 0 1 15 8" />
    </>
  ),
} as const;

export type IconName = keyof typeof PATHS;

export interface IconProps extends Omit<SVGProps<SVGSVGElement>, 'name'> {
  name: IconName;
  /** Visual size in px. Stroke width scales so small icons stay legible. */
  size?: number;
  title?: string;
}

export function Icon({ name, size = 16, className, title, ...rest }: IconProps) {
  return (
    <svg
      aria-hidden={title ? undefined : true}
      role={title ? 'img' : undefined}
      className={cn('shrink-0', className)}
      fill="none"
      height={size}
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={size <= 14 ? 2.2 : 1.9}
      viewBox="0 0 24 24"
      width={size}
      {...rest}
    >
      {title ? <title>{title}</title> : null}
      {PATHS[name]}
    </svg>
  );
}
