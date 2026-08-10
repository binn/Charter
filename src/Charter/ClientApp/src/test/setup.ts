import '@testing-library/jest-dom/vitest';
import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

// jsdom has no matchMedia, and `useIsDesktop` reads it on first render. Default to the desktop
// breakpoint so tests exercise the expanded layout; individual tests override it.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation((query: string) => ({
    matches: true,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// jsdom does not implement scrollIntoView, which the conversation and pane 2 both call.
Element.prototype.scrollIntoView = vi.fn();

afterEach(() => {
  cleanup();
});
