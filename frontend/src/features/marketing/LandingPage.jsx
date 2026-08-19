import { Link } from 'react-router-dom';
import { ParticleField } from '@/components/visual/ParticleField';
import { useMotion } from '@/motion/hooks';
import { DURATION, EASE, gsap } from '@/motion/motion';
import { useTheme } from '@/contexts/ThemeContext';
import s from './LandingPage.module.css';

const CAPABILITIES = [
  {
    title: 'Every request in one place',
    body:
      'Replaces work scattered across email, chat, calls and spreadsheets with a '
      + 'traceable record from first report through to resolution and closure.',
  },
  {
    title: 'Service levels that count properly',
    body:
      'Response and resolution targets measured in working minutes against a real '
      + 'calendar — weekends, public holidays and daylight-saving transitions included.',
  },
  {
    title: 'Nothing goes unowned',
    body:
      'Routing by category, capacity-weighted assignment, and an escalation ladder '
      + 'that fires before a target slips rather than after it.',
  },
  {
    title: 'Answerable after the fact',
    body:
      'Every status change, reassignment and permission edit is attributed to a '
      + 'person, a rule, or a background job, in an append-only log.',
  },
  {
    title: 'Built for the work you actually do',
    body:
      'Tickets link to the purchase order, style, shipment or inspection they concern '
      + '— references into your systems, never a second copy of them.',
  },
  {
    title: 'AI that only ever suggests',
    body:
      'Off by default. The priority matrix and SLA rules stay deterministic; a model '
      + 'can offer a second opinion and never overrides one.',
  },
];

const FIGURES = [
  { value: '7', label: 'roles, each with its own data scope' },
  { value: '55', label: 'permissions, none hardcoded' },
  { value: '100%', label: 'of changes attributable' },
];

export function LandingPage() {
  const { theme, toggle } = useTheme();

  const scope = useMotion(() => {
    const timeline = gsap.timeline({ defaults: { ease: EASE.out } });

    timeline
      .from('[data-hero-line]', {
        opacity: 0,
        y: 18,
        duration: DURATION.slow,
        stagger: 0.08,
      })
      .from('[data-hero-actions]', { opacity: 0, y: 12, duration: DURATION.base }, '-=0.2')
      .from('[data-figure]', {
        opacity: 0,
        y: 10,
        duration: DURATION.base,
        stagger: 0.06,
      }, '-=0.15')
      .from('[data-capability]', {
        opacity: 0,
        y: 14,
        duration: DURATION.base,
        stagger: 0.05,
      }, '-=0.1');
  }, []);

  return (
    <div className={s.page} ref={scope}>
      <div className={s.field}>
        <ParticleField className={s.canvas} />
        <div className={s.fieldMask} aria-hidden="true" />
      </div>

      <header className={s.topbar}>
        <div className={s.brand}>
          <span className={s.brandMark} aria-hidden="true">ST</span>
          Support Desk
        </div>

        <div className={s.topbarActions}>
          <button
            type="button"
            className={s.themeToggle}
            onClick={toggle}
            aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
          >
            {theme === 'dark' ? '☀' : '☾'}
          </button>

          <Link className={s.signIn} to="/login">Sign in</Link>
        </div>
      </header>

      <main className={s.hero}>
        <p className={s.eyebrow} data-hero-line>Internal IT · ERP · Customer · Supplier</p>

        <h1 className={s.headline} data-hero-line>
          Support that can be
          <span className={s.headlineAccent}> accounted for.</span>
        </h1>

        <p className={s.lede} data-hero-line>
          A ticketing system for teams who have to answer for their response times —
          with service levels measured against working hours, escalations that fire on
          their own, and a record of who changed what.
        </p>

        <div className={s.actions} data-hero-actions>
          <Link className={s.primaryAction} to="/login">Sign in to your desk</Link>
          <a className={s.secondaryAction} href="#capabilities">See what it does</a>
        </div>

        <dl className={s.figures}>
          {FIGURES.map((figure) => (
            <div key={figure.label} className={s.figure} data-figure>
              <dt className={s.figureValue}>{figure.value}</dt>
              <dd className={s.figureLabel}>{figure.label}</dd>
            </div>
          ))}
        </dl>
      </main>

      <section className={s.capabilities} id="capabilities">
        <h2 className={s.sectionTitle}>What it handles</h2>

        <div className={s.grid}>
          {CAPABILITIES.map((capability) => (
            <article key={capability.title} className={s.capability} data-capability>
              <h3 className={s.capabilityTitle}>{capability.title}</h3>
              <p className={s.capabilityBody}>{capability.body}</p>
            </article>
          ))}
        </div>
      </section>

      <footer className={s.footer}>
        <span>Support Desk</span>
        <Link to="/login">Sign in</Link>
      </footer>
    </div>
  );
}
