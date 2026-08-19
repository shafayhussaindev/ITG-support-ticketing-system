import gsap from 'gsap';

/*
  Shared motion vocabulary.

  Every animation in the application comes through here so the whole interface moves
  in one accent rather than each screen inventing its own. Durations match the CSS
  tokens, because a GSAP tween and a CSS transition sitting next to each other at
  different speeds is more noticeable than either being slightly wrong.

  Motion in a tool people use all day has to justify itself. The rule applied
  throughout: animate something appearing, changing value, or moving between states —
  never animate for its own sake, and never make anyone wait on it.
*/

export const DURATION = {
  instant: 0.09,
  fast: 0.16,
  base: 0.24,
  slow: 0.38,
};

export const EASE = {
  out: 'power2.out',
  inOut: 'power2.inOut',
  spring: 'back.out(1.4)',
};

/**
 * Whether the person has asked their system for less movement.
 *
 * Honoured everywhere below. For some people motion is not decoration but a
 * migraine or a bout of vertigo, and the setting exists to be respected rather than
 * detected and ignored.
 */
export function prefersReducedMotion() {
  return typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

/**
 * Whether the page is currently being painted.
 *
 * Browsers stop servicing requestAnimationFrame in a hidden tab, and GSAP is driven
 * by it. An entrance started while hidden writes its opening state — opacity zero —
 * and then never advances, so the content is invisible when the tab is next opened.
 *
 * The rule this enables is simply: do not animate what nobody is looking at. Skipping
 * leaves every element in its natural, visible state, which is the outcome wanted in
 * a tab the reader has not yet arrived at anyway.
 */
export function isHidden() {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/** True when motion should be skipped entirely, for either reason. */
export function shouldSkipMotion() {
  return prefersReducedMotion() || isHidden();
}

/**
 * Runs a GSAP build inside a context, returning the cleanup a React effect needs.
 *
 * gsap.context scopes selectors to one element and reverts every tween it created in
 * a single call, which is what keeps a fast route change from leaving half-finished
 * animations mutating detached nodes.
 *
 * With reduced motion requested, the build is skipped entirely and elements are left
 * in their final, untransformed state — so nothing is left invisible because its
 * entrance never ran.
 */
export function animate(scope, build) {
  if (!scope || shouldSkipMotion()) {
    return () => {};
  }

  const context = gsap.context(build, scope);
  return () => context.revert();
}

/** Fade and lift, for a panel or card arriving. */
export function reveal(targets, options = {}) {
  return gsap.from(targets, {
    opacity: 0,
    y: options.distance ?? 8,
    duration: options.duration ?? DURATION.base,
    ease: options.ease ?? EASE.out,
    stagger: options.stagger ?? 0,
    delay: options.delay ?? 0,
    clearProps: 'opacity,transform',
  });
}

/**
 * The same, staggered across a list.
 *
 * The stagger is capped: forty rows at 30ms each would take more than a second to
 * finish, and the last row would arrive long after the reader had started reading the
 * first. Past the cap every remaining row appears together.
 */
export function revealList(targets, options = {}) {
  const step = options.step ?? 0.028;
  const cap = options.cap ?? 12;

  return gsap.from(targets, {
    opacity: 0,
    y: options.distance ?? 6,
    duration: options.duration ?? DURATION.base,
    ease: EASE.out,
    stagger: {
      each: step,
      from: 'start',
      amount: Math.min(step * cap, step * (targets?.length ?? cap)),
    },
    clearProps: 'opacity,transform',
  });
}

/**
 * Counts a number up to its value.
 *
 * Only for figures a person is meant to compare — a KPI, a total. Applied to a
 * ticket number or an identifier it would be nonsense, so callers opt in.
 */
export function countTo(element, value, options = {}) {
  const state = { current: options.from ?? 0 };
  const decimals = options.decimals ?? 0;
  const format = options.format ?? ((n) => n.toFixed(decimals));

  return gsap.to(state, {
    current: value,
    duration: options.duration ?? DURATION.slow,
    ease: 'power1.out',
    onUpdate: () => {
      if (element) {
        element.textContent = format(state.current);
      }
    },
    onComplete: options.onComplete,
  });
}

/** A short attention pulse, for a value that changed under the reader's eyes. */
export function pulse(target) {
  if (!target || shouldSkipMotion()) return null;

  return gsap.fromTo(
    target,
    { backgroundColor: 'var(--c-primary-soft)' },
    { backgroundColor: 'transparent', duration: 0.9, ease: 'power2.out' },
  );
}

export { gsap };
