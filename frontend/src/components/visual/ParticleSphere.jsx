import { useEffect, useRef } from 'react';
import { REPEL_RADIUS, stepRepulsion } from './repulsion';

/*
  A rotating sphere of points that scatters away from the pointer and springs back.

  Hand-written rather than pulled in. A WebGL library is around 150KB gzipped and this
  sits on the entry page, which is the one page a first-time visitor waits for. Real
  perspective projection over a few hundred points costs about seven kilobytes and
  holds 60fps on integrated graphics.

  Two coordinate systems are in play, and keeping them separate is what makes the
  interaction feel right:

    - Each point has a fixed home on the sphere in 3D. That is what rotates, and what
      the perspective projection turns into a screen position.
    - On top of that sits a 2D displacement with its own velocity, driven by a spring
      back to zero and pushed outward by the pointer.

  Repelling in 2D rather than in 3D is deliberate. The cursor is a screen-space object;
  pushing points along the vector away from it on screen is what a person expects to
  see. Pushing them along a 3D ray from the camera would send half of them away from
  the viewer instead, which reads as the sphere denting rather than scattering.
*/

/*
  Focal length, in the same units as the sphere's radius.

  It has to be comfortably larger than the radius or the projection is nearly
  orthographic — every point comes back at almost the same scale, the depth cue
  disappears, and the sphere renders as a flat disc of identical dots. An earlier
  version added a separate camera distance on top of this, which pushed every scale
  into a narrow band around 0.5 and made the whole cloud sub-pixel and nearly
  transparent. Radius times four keeps near points about 1.3x and far ones about 0.8x.
*/
const FOCAL_RATIO = 4;

/** Opacity buckets. Points are drawn in batches so the canvas state changes rarely. */
const ALPHA_STEPS = 10;

/**
 * Points spread evenly over a sphere, via the golden angle.
 *
 * Uniform random spherical coordinates cluster at the poles, which on a slowly
 * rotating sphere is the one artefact the eye picks out immediately. The Fibonacci
 * arrangement has no seams and no clumps.
 */
function createPoints(count, radius) {
  const golden = Math.PI * (3 - Math.sqrt(5));

  return Array.from({ length: count }, (_, i) => {
    const y = 1 - (i / (count - 1)) * 2;
    const ring = Math.sqrt(Math.max(0, 1 - y * y));
    const theta = golden * i;

    return {
      // Home position, never mutated — the sphere it belongs to.
      hx: Math.cos(theta) * ring * radius,
      hy: y * radius,
      hz: Math.sin(theta) * ring * radius,

      // Screen-space displacement and its velocity.
      dx: 0,
      dy: 0,
      vx: 0,
      vy: 0,
    };
  });
}

/**
 * @param {object} props
 * @param {number} [props.density] Points per 100,000 pixels of canvas.
 * @param {boolean} [props.repel] Whether the pointer scatters the points.
 * @param {number} [props.maxPoints] Ceiling, for a small ambient instance.
 */
export function ParticleSphere({
  className,
  density = 92,
  repel = true,
  maxPoints = 900,
}) {
  const canvasRef = useRef(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return undefined;

    const context = canvas.getContext('2d', { alpha: true });
    if (!context) return undefined;

    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

    let points = [];
    let width = 0;
    let height = 0;
    let frame = 0;
    let rotation = 0;
    let radius = 0;
    let focal = 0;

    // Null until the pointer is over the canvas, so points rest undisturbed rather
    // than scattering from a phantom cursor parked at the origin.
    let pointer = null;

    let colour = '15, 110, 119';

    function readPalette() {
      colour = getComputedStyle(canvas).getPropertyValue('--particle-point').trim()
        || '15, 110, 119';
    }

    function resize() {
      const rect = canvas.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) return;

      // Capped at 2. Beyond that the pixel count quadruples for a difference nobody
      // can see, and a 3x phone panel would be doing nine times the work.
      const ratio = Math.min(window.devicePixelRatio || 1, 2);

      width = rect.width;
      height = rect.height;
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      context.setTransform(ratio, 0, 0, ratio, 0, 0);

      const count = Math.min(
        maxPoints,
        Math.max(220, Math.round((width * height) / 100_000 * density)),
      );

      // Sized against the smaller axis so the sphere stays whole on a phone held
      // upright as well as on a wide monitor.
      radius = Math.min(width, height) * 0.3;
      focal = radius * FOCAL_RATIO;

      points = createPoints(count, radius);
      readPalette();
    }

    function step() {
      rotation += 0.0022;

      const cos = Math.cos(rotation);
      const sin = Math.sin(rotation);
      const centreX = width / 2;
      const centreY = height / 2;

      const buckets = Array.from({ length: ALPHA_STEPS }, () => []);

      for (const point of points) {
        // Yaw only. A second axis on a sphere adds nothing legible, because a sphere
        // rotated about two axes looks exactly like one rotated about one.
        const x = point.hx * cos - point.hz * sin;
        const z = point.hx * sin + point.hz * cos;
        const scale = focal / (focal + z);

        const baseX = centreX + x * scale;
        const baseY = centreY + point.hy * scale;

        // Push away from the cursor, spring home, damp. Kept in its own module so
        // the behaviour can be asserted as arithmetic — it cannot be watched in a
        // headless run, and its failures are feelings rather than exceptions.
        stepRepulsion(point, baseX, baseY, pointer);

        // Opacity from depth directly rather than from the projected scale, so the
        // ramp is identical at every viewport size instead of drifting with it.
        const depth = (z + radius) / (2 * radius);
        const alpha = 0.95 - depth * 0.72;
        const bucket = Math.min(ALPHA_STEPS - 1, Math.max(0, Math.floor(alpha * ALPHA_STEPS)));

        buckets[bucket].push(
          baseX + point.dx,
          baseY + point.dy,
          Math.max(0.7, scale * 1.75),
        );
      }

      return buckets;
    }

    function draw() {
      context.clearRect(0, 0, width, height);

      const buckets = step();

      // One path and one fill per opacity band rather than per point. Setting
      // fillStyle several hundred times a frame is the expensive part of drawing a
      // point cloud on a canvas, not the arcs themselves.
      buckets.forEach((bucket, index) => {
        if (bucket.length === 0) return;

        const alpha = ((index + 0.5) / ALPHA_STEPS).toFixed(3);
        context.fillStyle = `rgba(${colour}, ${alpha})`;
        context.beginPath();

        for (let i = 0; i < bucket.length; i += 3) {
          const x = bucket[i];
          const y = bucket[i + 1];
          const r = bucket[i + 2];

          context.moveTo(x + r, y);
          context.arc(x, y, r, 0, Math.PI * 2);
        }

        context.fill();
      });
    }

    function loop() {
      draw();
      frame = requestAnimationFrame(loop);
    }

    function onPointerMove(event) {
      const rect = canvas.getBoundingClientRect();
      const x = event.clientX - rect.left;
      const y = event.clientY - rect.top;

      // Tracked a little beyond the edges, so points near the border still feel a
      // cursor that has just left rather than snapping back the instant it does.
      pointer = (x < -REPEL_RADIUS || y < -REPEL_RADIUS
        || x > rect.width + REPEL_RADIUS || y > rect.height + REPEL_RADIUS)
        ? null
        : { x, y };
    }

    function onPointerLeave() {
      pointer = null;
    }

    /**
     * Browsers stop servicing requestAnimationFrame in a hidden tab. A loop left
     * scheduled there is a callback that never fires, and on some platforms a wakeup
     * that costs battery for a picture nobody is looking at.
     */
    function onVisibilityChange() {
      cancelAnimationFrame(frame);

      if (document.visibilityState === 'visible' && !reduced) {
        frame = requestAnimationFrame(loop);
      }
    }

    function handleResize() {
      resize();

      // Redrawn at once rather than on the next frame: a dragged window edge would
      // otherwise trail a stretched canvas, and under reduced motion there is no
      // next frame to wait for.
      draw();
    }

    /*
      The container is observed, not the canvas.

      Resizing an element inside a ResizeObserver that watches that same element is
      the pattern the loop guard exists to stop, and the browser's remedy is to drop
      the notification — so writing canvas.width here would work once and then
      silently stop, leaving the backing store stale and the sphere stretched.
    */
    const observer = new ResizeObserver(handleResize);
    observer.observe(canvas.parentElement ?? canvas);
    window.addEventListener('resize', handleResize);

    resize();

    // Painted synchronously before any frame is requested: the first animation frame
    // is 16ms away on a visible page and never on a hidden one, and either way an
    // empty canvas is not what should greet a visitor.
    draw();

    if (!reduced) {
      frame = requestAnimationFrame(loop);
      document.addEventListener('visibilitychange', onVisibilityChange);

      if (repel) {
        window.addEventListener('pointermove', onPointerMove, { passive: true });
        window.addEventListener('pointerleave', onPointerLeave);
        window.addEventListener('blur', onPointerLeave);
      }
    }

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener('resize', handleResize);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerleave', onPointerLeave);
      window.removeEventListener('blur', onPointerLeave);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [density, repel, maxPoints]);

  return <canvas ref={canvasRef} className={className} aria-hidden="true" />;
}
