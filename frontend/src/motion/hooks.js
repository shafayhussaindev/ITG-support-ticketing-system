import { useEffect, useLayoutEffect, useRef } from 'react';
import { DURATION, EASE, animate, countTo, gsap, reveal, revealList, shouldSkipMotion } from './motion';

/**
 * Runs a GSAP build against a scope element when its dependencies change.
 *
 * useLayoutEffect rather than useEffect: the tween sets the starting opacity, and
 * under useEffect the browser gets one frame to paint the final state first. That
 * frame is a visible flash of fully-formed content collapsing back to nothing.
 */
export function useMotion(build, dependencies = []) {
  const scope = useRef(null);

  useLayoutEffect(
    () => animate(scope.current, build),
    // The build closure is recreated every render; the caller's dependency list is
    // the honest signal for when the animation should run again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    dependencies,
  );

  return scope;
}

/** Fades and lifts the scope's children matching a selector, once they exist. */
export function useReveal(selector, dependencies = [], options = {}) {
  return useMotion(() => {
    const targets = gsap.utils.toArray(selector);
    if (targets.length > 0) reveal(targets, options);
  }, dependencies);
}

/** The same, staggered — for tables, lists and card grids. */
export function useRevealList(selector, dependencies = [], options = {}) {
  return useMotion(() => {
    const targets = gsap.utils.toArray(selector);
    if (targets.length > 0) revealList(targets, options);
  }, dependencies);
}

/**
 * Counts an element's text up to a numeric value.
 *
 * The caller renders the <em>true</em> figure, and this walks it backwards to the
 * starting point before painting, then counts forward. The obvious arrangement —
 * render zero and let the animation supply the real number — means any failure of the
 * animation leaves a dashboard confidently reporting nought open tickets when there
 * are five. Reversing it makes the worst case a figure that simply appears, which is
 * what happens under reduced motion anyway.
 *
 * useLayoutEffect for the same reason: setting the start value after paint would show
 * one frame of the final number before it jumped back to zero to count up.
 */
export function useCountUp(value, options = {}) {
  const ref = useRef(null);
  const previous = useRef(null);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return undefined;

    const format = options.format ?? ((n) => String(Math.round(n)));

    // A non-numeric value means the caller detached the ref and is rendering the
    // text itself — "no data yet", "4.8 / 5". Nothing to count to; just forget where
    // the last run finished so a later numeric value starts from zero again.
    if (typeof value !== 'number' || Number.isNaN(value)) {
      previous.current = null;
      return undefined;
    }

    if (shouldSkipMotion()) {
      element.textContent = format(value);
      previous.current = value;
      return undefined;
    }

    // Counting from the previous figure rather than from zero on an update: a
    // dashboard that refreshes every two minutes should not replay its whole
    // entrance each time, and the delta is the interesting part anyway.
    const first = previous.current === null;
    const from = previous.current ?? 0;

    // A tween with nothing to travel may never fire onUpdate, which would leave the
    // element showing whatever React last rendered. StrictMode makes this the common
    // case, not the rare one: it mounts, runs the effect, tears it down and runs it
    // again, so the second run frequently starts where the first was heading.
    if (from === value) {
      element.textContent = format(value);
      return undefined;
    }

    const tween = countTo(element, value, {
      from,
      format,
      duration: first ? DURATION.slow : DURATION.base,

      // Recorded on completion rather than up front. An interrupted run must leave
      // the next one counting from where the display actually is — claiming to have
      // arrived at a value the reader never saw is how a figure gets stuck.
      onComplete: () => {
        previous.current = value;
      },
    });

    return () => tween.kill();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  return ref;
}

/**
 * Slides a marker to the active navigation item.
 *
 * A shared element moving between positions reads as one thing relocating, which is
 * what the navigation actually is. Two separate items fading in and out reads as two
 * things, and the reader has to work out they are related.
 */
export function useActiveIndicator(activeKey) {
  const listRef = useRef(null);
  const markerRef = useRef(null);
  const settled = useRef(false);

  useLayoutEffect(() => {
    const list = listRef.current;
    const marker = markerRef.current;
    if (!list || !marker) return;

    const active = list.querySelector('[data-active="true"]');

    if (!active) {
      gsap.set(marker, { opacity: 0 });
      settled.current = false;
      return;
    }

    const top = active.offsetTop;
    const height = active.offsetHeight;

    // The first placement jumps; every later one travels. Animating the initial
    // position would slide the marker in from the top of the sidebar on every page
    // load, which is motion that describes nothing.
    if (!settled.current || shouldSkipMotion()) {
      gsap.set(marker, { y: top, height, opacity: 1 });
      settled.current = true;
      return;
    }

    gsap.to(marker, {
      y: top,
      height,
      opacity: 1,
      duration: DURATION.base,
      ease: EASE.out,
    });
  }, [activeKey]);

  return { listRef, markerRef };
}
