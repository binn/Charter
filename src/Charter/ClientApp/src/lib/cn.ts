type ClassValue = string | number | false | null | undefined | ClassValue[];

/**
 * Joins class names. Hand-rolled rather than pulling in `clsx` + `tailwind-merge`: components here
 * compose classes by variant lookup rather than by overriding one another, so conflict resolution
 * would never fire, and two dependencies to save six lines is a bad trade in an app that ships
 * inside someone else's container.
 */
export function cn(...values: ClassValue[]): string {
  const out: string[] = [];

  for (const value of values) {
    if (!value && value !== 0) {
      continue;
    }
    if (Array.isArray(value)) {
      const nested = cn(...value);
      if (nested) {
        out.push(nested);
      }
      continue;
    }
    out.push(String(value));
  }

  return out.join(' ');
}
