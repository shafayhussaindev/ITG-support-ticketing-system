/*
  The scatter physics, on its own.

  Extracted from the canvas so it can be reasoned about and tested without a
  rendering context or an animation frame — which matters, because the failure mode
  of this kind of code is not a crash but a feel: points that never come back, or
  come back with a wobble, or shoot off the screen when the cursor sits still.

  One point, one step. The component calls this per point per frame.
*/

/** Pointer influence, in pixels. Points nearer than this are pushed away. */
export const REPEL_RADIUS = 150;

export const REPEL_STRENGTH = 26;

/**
 * Spring constant pulling a displaced point home, and the damping on its velocity.
 *
 * These two are chosen together. Damping below roughly 0.9 with this spring gives a
 * return that overshoots once and settles — the slight bounce that makes it read as
 * elastic rather than magnetic. Raise the spring or lower the damping much further
 * and it rings; go the other way and points crawl home.
 */
export const SPRING = 0.055;
export const DAMPING = 0.88;

/**
 * Advances one point's screen-space displacement by a frame.
 *
 * @param {{dx:number, dy:number, vx:number, vy:number}} point Mutated in place —
 *   this runs several hundred times a frame, and allocating a result object for each
 *   would hand the garbage collector a few hundred thousand short-lived objects a
 *   second.
 * @param {number} baseX Where the point would sit with no displacement.
 * @param {number} baseY
 * @param {{x:number, y:number}|null} pointer Null when the cursor is away.
 */
export function stepRepulsion(point, baseX, baseY, pointer) {
  if (pointer) {
    const offsetX = baseX + point.dx - pointer.x;
    const offsetY = baseY + point.dy - pointer.y;
    const distance = Math.hypot(offsetX, offsetY);

    // The lower bound guards the division below. Two points at exactly the cursor's
    // position would otherwise divide by zero and leave NaN in the displacement,
    // which propagates and quietly removes those points from the sphere forever.
    if (distance < REPEL_RADIUS && distance > 0.001) {
      // Squared falloff: firm directly under the cursor, and gone by the edge of the
      // radius rather than stopping abruptly at it.
      const force = (1 - distance / REPEL_RADIUS) ** 2 * REPEL_STRENGTH;
      point.vx += (offsetX / distance) * force;
      point.vy += (offsetY / distance) * force;
    }
  }

  point.vx += -point.dx * SPRING;
  point.vy += -point.dy * SPRING;
  point.vx *= DAMPING;
  point.vy *= DAMPING;
  point.dx += point.vx;
  point.dy += point.vy;
}
