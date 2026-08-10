import { useEffect, useState } from 'react';

export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() =>
    typeof window === 'undefined' ? false : window.matchMedia(query).matches,
  );

  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = () => {
      setMatches(list.matches);
    };
    onChange();
    list.addEventListener('change', onChange);
    return () => {
      list.removeEventListener('change', onChange);
    };
  }, [query]);

  return matches;
}

/** Matches Tailwind's `lg` breakpoint. Above it the three panes sit side by side (§12). */
export function useIsDesktop(): boolean {
  return useMediaQuery('(min-width: 64rem)');
}
