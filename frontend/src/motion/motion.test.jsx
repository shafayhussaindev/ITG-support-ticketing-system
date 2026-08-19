import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useCountUp } from './hooks';
import { prefersReducedMotion } from './motion';

/**
 * The point of these is the fail-safe, not the animation.
 *
 * A dashboard that shows "0 open tickets" because a tween did not run is worse than
 * one with no motion at all — and the environment where frames never arrive is real:
 * a background tab, a locked screen, a machine under load. The rule these enforce is
 * that the true figure is what React renders, and motion only ever decorates it.
 */

function Figure({ value, suffix = '' }) {
  const ref = useCountUp(typeof value === 'number' ? value : null, {
    format: (n) => `${Math.round(n)}${suffix}`,
  });

  return (
    <span data-testid="figure" ref={typeof value === 'number' ? ref : undefined}>
      {typeof value === 'number' ? `${value}${suffix}` : value}
    </span>
  );
}

describe('prefersReducedMotion', () => {
  const original = window.matchMedia;

  afterEach(() => {
    window.matchMedia = original;
  });

  it('reports the system preference', () => {
    window.matchMedia = vi.fn().mockReturnValue({ matches: true });
    expect(prefersReducedMotion()).toBe(true);

    window.matchMedia = vi.fn().mockReturnValue({ matches: false });
    expect(prefersReducedMotion()).toBe(false);
  });

  it('does not throw when the browser cannot answer', () => {
    window.matchMedia = undefined;
    expect(prefersReducedMotion()).toBe(false);
  });
});

describe('useCountUp', () => {
  beforeEach(() => {
    window.matchMedia = vi.fn().mockReturnValue({ matches: true });
  });

  it('writes the real figure when motion is reduced', () => {
    render(<Figure value={42} />);
    expect(screen.getByTestId('figure').textContent).toBe('42');
  });

  it('keeps the suffix', () => {
    render(<Figure value={100} suffix="%" />);
    expect(screen.getByTestId('figure').textContent).toBe('100%');
  });

  it('leaves a non-numeric value to the component', () => {
    // Counting up to "no data yet" is meaningless, so the ref is not attached and
    // whatever the component rendered stands — here, the em dash it chose.
    render(<Figure value="—" />);
    expect(screen.getByTestId('figure').textContent).toBe('—');
  });

  it('renders the true figure before any effect runs', () => {
    // The guarantee that matters: whatever happens to the animation afterwards, the
    // markup React produced already carries the correct number.
    const { container } = render(<Figure value={7} />);
    expect(container.textContent).toContain('7');
  });
});
