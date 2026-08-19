import { Link } from 'react-router-dom';
import { ParticleSphere } from '@/components/visual/ParticleSphere';
import { useMotion } from '@/motion/hooks';
import { DURATION, EASE, gsap } from '@/motion/motion';
import { useTheme } from '@/contexts/ThemeContext';
import s from './LandingPage.module.css';

/**
 * The entry page.
 *
 * One sphere, one sentence, one button. Everything a visitor needs to do here is
 * sign in, so anything else on the screen is something between them and that — and
 * the product itself is behind the button, which is a better argument for it than a
 * page of claims would be.
 */
export function LandingPage() {
  const { theme, toggle } = useTheme();

  const scope = useMotion(() => {
    const timeline = gsap.timeline({ defaults: { ease: EASE.out } });

    // The sphere first and slowly, so it reads as arriving rather than appearing;
    // then the words, then the button last, which is where the eye should finish.
    timeline
      .from('[data-sphere]', { opacity: 0, scale: 0.92, duration: 1.1, ease: 'power2.out' })
      .from('[data-brand]', { opacity: 0, y: -8, duration: DURATION.base }, '-=0.85')
      .from('[data-line]', { opacity: 0, y: 14, duration: DURATION.slow, stagger: 0.1 }, '-=0.65')
      .from('[data-cta]', { opacity: 0, y: 12, scale: 0.97, duration: DURATION.slow }, '-=0.25')
      .from('[data-foot]', { opacity: 0, duration: DURATION.base }, '-=0.3');
  }, []);

  return (
    <div className={s.page} ref={scope}>
      <div className={s.sphere} data-sphere>
        <ParticleSphere className={s.canvas} />
      </div>

      <header className={s.brand} data-brand>
        <span className={s.brandMark} aria-hidden="true">ST</span>
        Support Desk
      </header>

      <button
        type="button"
        className={s.themeToggle}
        onClick={toggle}
        aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
      >
        {theme === 'dark' ? '☀' : '☾'}
      </button>

      <main className={s.centre}>
        <h1 className={s.headline} data-line>
          Support that can be accounted for.
        </h1>

        <p className={s.lede} data-line>
          Every request tracked against real service levels, escalated before it slips,
          and answerable long after it closes.
        </p>

        <div data-cta>
          <Link className={s.cta} to="/login">
            Get started
            <span className={s.ctaArrow} aria-hidden="true">→</span>
          </Link>
        </div>
      </main>

      <footer className={s.foot} data-foot>
        Internal IT · ERP · Customer · Supplier
      </footer>
    </div>
  );
}
