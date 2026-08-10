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

// Nor scrollTo, which TanStack Virtual calls when pane 2 jumps to an event (§12's linkage).
Element.prototype.scrollTo = vi.fn();

// TanStack Virtual measures its scroll container with a ResizeObserver, which jsdom has no
// implementation of. The stub observes nothing, so every element reports a zero-height viewport —
// which is exactly why the pane-2 tests use a short fixture: at that size the whole list falls
// inside the virtualizer's overscan and lands in the DOM. The separate virtualization test uses the
// full 12,480-event fixture and asserts the opposite, that almost none of it is rendered.
class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

Object.defineProperty(window, 'ResizeObserver', {
  writable: true,
  value: ResizeObserverStub,
});

// jsdom performs no layout, so every element measures zero — and a virtualizer told its viewport is
// zero pixels tall correctly concludes it has nothing to draw, rendering an empty list. TanStack
// Virtual measures its scroll container with `offsetWidth`/`offsetHeight` specifically, so those are
// what have to report a plausible box for pane 2 to render a window the linkage tests can inspect.
Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
  configurable: true,
  get: () => 600,
});

Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
  configurable: true,
  get: () => 900,
});

afterEach(() => {
  cleanup();
});
