import { describe, expect, it } from 'vitest';
import { REPEL_RADIUS, stepRepulsion } from './repulsion';

/*
  The scatter behaviour, verified as arithmetic.

  It cannot be watched in a headless run — there are no animation frames — and the
  ways this goes wrong are not exceptions but sensations: points that drift away and
  never return, or oscillate, or vanish because a division by zero put NaN in their
  position. Each of those is a statement about numbers, so each can be asserted.
*/

function point() {
  return { dx: 0, dy: 0, vx: 0, vy: 0 };
}

/** Runs n frames with the cursor held still. */
function settle(p, baseX, baseY, pointer, frames) {
  for (let i = 0; i < frames; i++) {
    stepRepulsion(p, baseX, baseY, pointer);
  }

  return p;
}

describe('stepRepulsion', () => {
  it('leaves a point alone when the cursor is away', () => {
    const p = settle(point(), 100, 100, null, 30);

    expect(p.dx).toBe(0);
    expect(p.dy).toBe(0);
  });

  it('pushes a point directly away from the cursor', () => {
    const p = point();

    // Cursor to the left of the point, so it should travel right and not vertically.
    settle(p, 100, 100, { x: 60, y: 100 }, 10);

    expect(p.dx).toBeGreaterThan(0);
    expect(Math.abs(p.dy)).toBeLessThan(0.001);
  });

  it('ignores a cursor beyond the radius', () => {
    const p = settle(point(), 100, 100, { x: 100 + REPEL_RADIUS + 10, y: 100 }, 10);

    expect(p.dx).toBe(0);
    expect(p.dy).toBe(0);
  });

  it('pushes harder the closer the cursor is', () => {
    const near = settle(point(), 100, 100, { x: 90, y: 100 }, 6);
    const far = settle(point(), 100, 100, { x: 100 - REPEL_RADIUS + 20, y: 100 }, 6);

    expect(Math.abs(near.dx)).toBeGreaterThan(Math.abs(far.dx));
  });

  it('springs back home once the cursor leaves', () => {
    const p = point();
    settle(p, 100, 100, { x: 70, y: 100 }, 20);

    const scattered = Math.abs(p.dx);
    expect(scattered).toBeGreaterThan(1);

    // The return has to actually finish. A point that settles a few pixels off home
    // leaves the sphere permanently dented where somebody once moved their mouse.
    settle(p, 100, 100, null, 240);

    expect(Math.abs(p.dx)).toBeLessThan(0.05);
    expect(Math.abs(p.vx)).toBeLessThan(0.05);
  });

  it('does not run away while the cursor is held still', () => {
    // Force and spring have to reach an equilibrium. If the push always wins, a
    // stationary cursor slowly empties the sphere.
    const p = settle(point(), 100, 100, { x: 95, y: 100 }, 600);

    expect(Number.isFinite(p.dx)).toBe(true);
    expect(Math.abs(p.dx)).toBeLessThan(REPEL_RADIUS * 3);
  });

  it('survives a cursor exactly on top of a point', () => {
    // The distance guard: without it this divides by zero, and the NaN spreads into
    // the displacement and never leaves.
    const p = settle(point(), 100, 100, { x: 100, y: 100 }, 5);

    expect(Number.isNaN(p.dx)).toBe(false);
    expect(Number.isNaN(p.dy)).toBe(false);
  });
});
