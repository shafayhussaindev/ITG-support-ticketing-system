import { useEffect, useRef } from 'react';

/*
  A rotating three-dimensional point cloud, drawn to a canvas.

  Written rather than pulled in: a WebGL library is around 150KB gzipped, and this
  runs on the sign-in screen — the one page every user waits for, and the reason the
  dashboard's charting library is lazy-loaded in the first place. Real perspective
  projection over a few hundred points costs about six kilobytes and holds 60fps on a
  laptop with integrated graphics, which a support desk is far more likely to have
  than a discrete GPU.

  The depth is genuine: points live at (x, y, z), rotate about two axes, and are
  projected through a focal length. Distance drives radius, opacity and which
  neighbours get connected, so the cloud reads as a volume rather than as scattered
  dots on a flat plane.
*/

const FOCAL_LENGTH = 620;
const DEPTH = 520;

/** Below this distance in projected space, two points are joined by a line. */
const LINK_DISTANCE = 118;

/** Beyond this many points the connection pass starts to cost more than it earns. */
const MAX_POINTS = 150;

function createPoints(count, spread) {
  return Array.from({ length: count }, () => {
    // Distributed through a spherical volume rather than on its surface: a shell
    // reads as a ball, and the interior points are what give it depth as it turns.
    const theta = Math.random() * Math.PI * 2;
    const phi = Math.acos(2 * Math.random() - 1);
    const radius = spread * Math.cbrt(Math.random());

    return {
      x: radius * Math.sin(phi) * Math.cos(theta),
      y: radius * Math.sin(phi) * Math.sin(theta),
      z: radius * Math.cos(phi),

      // A little independent drift, so the cloud is never quite a rigid body.
      dx: (Math.random() - 0.5) * 0.05,
      dy: (Math.random() - 0.5) * 0.05,
      dz: (Math.random() - 0.5) * 0.05,
    };
  });
}

/**
 * @param {object} props
 * @param {number} [props.density] Points per 100,000 pixels of canvas area.
 * @param {boolean} [props.interactive] Whether the cloud leans towards the pointer.
 */
export function ParticleField({ density = 5.5, interactive = true, className }) {
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

    // Where the cloud is currently pointing, and where it is heading. Easing between
    // the two is what stops the pointer from yanking it about.
    const rotation = { x: 0, y: 0 };
    const target = { x: 0, y: 0 };

    /** Reads the palette from CSS so the field follows the theme rather than fighting it. */
    function palette() {
      const styles = getComputedStyle(canvas);
      return {
        point: styles.getPropertyValue('--particle-point').trim() || '15, 110, 119',
        link: styles.getPropertyValue('--particle-link').trim() || '15, 110, 119',
      };
    }

    let colours = palette();

    function resize() {
      const rect = canvas.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) return;

      // Capped at 2: beyond that the pixel count quadruples for a difference nobody
      // can see, and a 3x phone display would be doing nine times the work.
      const ratio = Math.min(window.devicePixelRatio || 1, 2);

      width = rect.width;
      height = rect.height;
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      context.setTransform(ratio, 0, 0, ratio, 0, 0);

      const count = Math.min(
        MAX_POINTS,
        Math.max(45, Math.round((width * height) / 100_000 * density)),
      );

      points = createPoints(count, Math.min(width, height) * 0.46);
      colours = palette();
    }

    function project(point) {
      const cosY = Math.cos(rotation.y);
      const sinY = Math.sin(rotation.y);
      const cosX = Math.cos(rotation.x);
      const sinX = Math.sin(rotation.x);

      // Yaw, then pitch. Two axes is enough to read as a rotating volume; a third
      // makes the motion busy without making it more legible.
      const x1 = point.x * cosY - point.z * sinY;
      const z1 = point.x * sinY + point.z * cosY;
      const y1 = point.y * cosX - z1 * sinX;
      const z2 = point.y * sinX + z1 * cosX;

      const scale = FOCAL_LENGTH / (FOCAL_LENGTH + z2 + DEPTH);

      return {
        x: width / 2 + x1 * scale,
        y: height / 2 + y1 * scale,
        scale,
        depth: z2,
      };
    }

    function draw() {
      context.clearRect(0, 0, width, height);

      // Eased towards the target rather than snapped to it, and the drift continues
      // underneath, so the cloud keeps its own life while following the pointer.
      rotation.y += (target.y - rotation.y) * 0.045 + 0.0016;
      rotation.x += (target.x - rotation.x) * 0.045;

      const projected = points.map((point) => {
        point.x += point.dx;
        point.y += point.dy;
        point.z += point.dz;

        // Turn them back before they wander out of the volume.
        const limit = Math.min(width, height) * 0.5;
        if (Math.abs(point.x) > limit) point.dx *= -1;
        if (Math.abs(point.y) > limit) point.dy *= -1;
        if (Math.abs(point.z) > limit) point.dz *= -1;

        return project(point);
      });

      // Connections first, so points sit on top of the web rather than under it.
      context.lineWidth = 1;

      for (let i = 0; i < projected.length; i++) {
        for (let j = i + 1; j < projected.length; j++) {
          const a = projected[i];
          const b = projected[j];
          const dx = a.x - b.x;
          const dy = a.y - b.y;
          const distance = Math.hypot(dx, dy);

          if (distance > LINK_DISTANCE) continue;

          // Fades with both separation and depth, which is what keeps the far side
          // of the cloud from reading as a solid mat of lines.
          const nearness = 1 - distance / LINK_DISTANCE;
          const depthFade = (a.scale + b.scale) / 2;
          const alpha = nearness * depthFade * 0.32;

          context.strokeStyle = `rgba(${colours.link}, ${alpha.toFixed(3)})`;
          context.beginPath();
          context.moveTo(a.x, a.y);
          context.lineTo(b.x, b.y);
          context.stroke();
        }
      }

      // Painter's algorithm: back to front, so nearer points overlap farther ones.
      projected
        .slice()
        .sort((a, b) => b.depth - a.depth)
        .forEach((p) => {
          const radius = Math.max(0.6, p.scale * 2.1);
          const alpha = Math.min(0.85, p.scale * 0.85);

          context.fillStyle = `rgba(${colours.point}, ${alpha.toFixed(3)})`;
          context.beginPath();
          context.arc(p.x, p.y, radius, 0, Math.PI * 2);
          context.fill();
        });
    }

    function loop() {
      draw();
      frame = requestAnimationFrame(loop);
    }

    function onPointerMove(event) {
      if (!interactive) return;

      const rect = canvas.getBoundingClientRect();
      const nx = (event.clientX - rect.left) / rect.width - 0.5;
      const ny = (event.clientY - rect.top) / rect.height - 0.5;

      // Deliberately shallow. A cloud that swings to face the cursor is a toy; a
      // slight lean is the parallax cue that sells the depth.
      target.y = nx * 0.55;
      target.x = ny * 0.35;
    }

    /**
     * Browsers stop servicing requestAnimationFrame in a hidden tab, so a loop left
     * running there is a callback that never fires — and on some platforms a wakeup
     * that drains battery for a picture nobody is looking at.
     */
    function onVisibilityChange() {
      cancelAnimationFrame(frame);

      if (document.visibilityState === 'visible' && !reduced) {
        frame = requestAnimationFrame(loop);
      }
    }

    function handleResize() {
      resize();

      // Redrawn immediately rather than waiting for the next frame: a dragged window
      // edge would otherwise leave a stretched canvas behind it, and under reduced
      // motion there is no next frame at all.
      draw();
    }

    /*
      The container is observed, not the canvas.

      Observing an element and then changing its size inside the callback is the
      pattern ResizeObserver's loop guard exists to stop, and the browser's remedy is
      to drop the notification. Writing canvas.width in a callback watching that same
      canvas therefore silently stops working after the first change — leaving the
      backing store at its original resolution while the CSS box grows, which renders
      as a stretched, blurred cloud on any window resize or device rotation.
    */
    const observer = new ResizeObserver(handleResize);
    observer.observe(canvas.parentElement ?? canvas);

    // A window listener as well: a canvas positioned against the viewport rather
    // than against its parent can change size without the parent's box moving at all.
    window.addEventListener('resize', handleResize);

    resize();

    // Painted once, synchronously, before any frame is requested. The first
    // requestAnimationFrame is a frame away, which is one frame of empty canvas on
    // load — and if the page is hidden it is not a frame away but indefinite, so a
    // visitor arriving from a background tab would find nothing there at all.
    draw();

    if (!reduced) {
      frame = requestAnimationFrame(loop);
      if (interactive) window.addEventListener('pointermove', onPointerMove, { passive: true });
      document.addEventListener('visibilitychange', onVisibilityChange);
    }

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener('resize', handleResize);
      window.removeEventListener('pointermove', onPointerMove);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [density, interactive]);

  return <canvas ref={canvasRef} className={className} aria-hidden="true" />;
}
