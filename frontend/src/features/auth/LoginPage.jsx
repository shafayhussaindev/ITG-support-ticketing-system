import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Field, LoadingState } from '@/components/ui';
import { ParticleSphere } from '@/components/visual/ParticleSphere';
import { useMotion } from '@/motion/hooks';
import { DURATION, EASE, gsap } from '@/motion/motion';
import s from './LoginPage.module.css';

const schema = z.object({
  email: z.email('Enter a valid email address.').max(256),
  password: z.string().min(1, 'Enter your password.').max(128),
  twoFactorCode: z
    .string()
    .regex(/^\d{6}$/, 'Enter the six-digit code from your authenticator app.')
    .optional()
    .or(z.literal('')),
});

export function LoginPage() {
  /*
    The one screen where a considered entrance is worth the milliseconds: it is the
    first thing anybody sees, and there is no work being interrupted. A timeline
    rather than parallel tweens so the panel settles before the form follows it in.
  */
  const scope = useMotion(() => {
    const timeline = gsap.timeline({ defaults: { ease: EASE.out } });

    timeline
      .from('[data-login-aside]', { opacity: 0, x: -16, duration: DURATION.slow })
      .from('[data-login-point]', {
        opacity: 0, x: -10, duration: DURATION.base, stagger: 0.06,
      }, '-=0.2')
      .from('[data-login-card]', { opacity: 0, y: 14, duration: DURATION.slow }, '-=0.35')
      .from('[data-login-field]', {
        opacity: 0, y: 8, duration: DURATION.base, stagger: 0.05,
      }, '-=0.25');
  }, []);

  const { login, isAuthenticated, isRestoring } = useAuth();
  const { theme, toggle } = useTheme();
  const toast = useToast();
  const navigate = useNavigate();
  const location = useLocation();

  // Shown only once the server says this account has MFA enabled, so the field
  // does not confuse the majority of users who do not.
  const [needsTwoFactor, setNeedsTwoFactor] = useState(false);
  const [formError, setFormError] = useState(null);

  const {
    register,
    handleSubmit,
    setFocus,
    formState: { errors, isSubmitting },
  } = useForm({
    resolver: zodResolver(schema),
    defaultValues: { email: '', password: '', twoFactorCode: '' },
  });

  if (isRestoring) {
    return <LoadingState label="Restoring your session" />;
  }

  if (isAuthenticated) {
    return <Navigate to={location.state?.from ?? '/dashboard'} replace />;
  }

  async function onSubmit(values) {
    setFormError(null);

    try {
      const user = await login(values);
      toast.success(`Welcome back, ${user.firstName ?? user.fullName}`);

      // Sends the user where they were originally heading before being bounced here.
      navigate(location.state?.from ?? '/dashboard', { replace: true });
    } catch (error) {
      switch (error.code) {
        case 'two_factor_required':
          setNeedsTwoFactor(true);
          setFormError({
            tone: 'warning',
            message: 'This account uses two-factor authentication. Enter the code from your authenticator app.',
          });
          setTimeout(() => setFocus('twoFactorCode'), 0);
          break;

        case 'two_factor_invalid':
          setFormError({ tone: 'error', message: 'That verification code is not valid. Try the current one.' });
          break;

        case 'account_locked':
          setFormError({
            tone: 'error',
            message: error.detail ?? 'This account is temporarily locked after too many failed attempts.',
          });
          break;

        case 'account_inactive':
          setFormError({ tone: 'error', message: 'This account has been deactivated. Contact your administrator.' });
          break;

        case 'rate_limited':
        case 'http_429':
          setFormError({ tone: 'warning', message: 'Too many sign-in attempts. Wait a minute and try again.' });
          break;

        case 'network_error':
          setFormError({ tone: 'error', message: 'Cannot reach the server. Check that the API is running.' });
          break;

        default:
          // Deliberately generic: the server does not distinguish an unknown email
          // from a wrong password, and neither should this screen.
          setFormError({ tone: 'error', message: 'Email or password is incorrect.' });
      }
    }
  }

  return (
    <div className={s.page} ref={scope}>
      <aside className={s.aside} data-login-aside>
        {/* Behind the copy, not around it: the cloud is atmosphere, and the words
            still have to be the first thing read. */}
        <ParticleSphere className={s.asideField} density={26} maxPoints={420} repel={false} />
        <div className={s.asideVeil} aria-hidden="true" />

        <div className={s.asideBrand}>
          <span className={s.asideMark} aria-hidden="true">
            ST
          </span>
          Support Desk
        </div>

        <div>
          <p className={s.asideHeading}>One place for every support request.</p>
          <p className={s.asideCopy}>
            Replaces requests scattered across email, chat, calls and spreadsheets with a
            traceable record from first report through to resolution and closure.
          </p>

          <ul className={s.asideList}>
            <li data-login-point>
              <span aria-hidden="true">▸</span> SLA tracked against business hours and holidays
            </li>
            <li data-login-point>
              <span aria-hidden="true">▸</span> Every change attributed to a person, a rule, or AI
            </li>
            <li data-login-point>
              <span aria-hidden="true">▸</span> Internal notes never visible to requesters
            </li>
          </ul>
        </div>

        <p className={s.asideFoot}>Tickets, service levels, reporting and a knowledge base.</p>
      </aside>

      <div className={s.panel} data-login-card>
        <form className={s.form} onSubmit={handleSubmit(onSubmit)} noValidate>
          <div data-login-field>
            <h1 className={s.title}>Sign in</h1>
            <p className={s.subtitle}>Use your work email address.</p>
          </div>

          {formError ? (
            <div
              className={`${s.alert} ${formError.tone === 'warning' ? s.alertWarning : ''}`}
              role="alert"
            >
              {formError.message}
            </div>
          ) : null}

          <div data-login-field>
            <Field
              label="Email"
              type="email"
              autoComplete="username"
              placeholder="you@company.com"
              required
              autoFocus
              error={errors.email?.message}
              {...register('email')}
            />
          </div>

          <div data-login-field>
            <Field
              label="Password"
              type="password"
              autoComplete="current-password"
              placeholder="••••••••••••"
              required
              error={errors.password?.message}
              {...register('password')}
            />
          </div>

          {needsTwoFactor ? (
            <Field
              label="Verification code"
              inputMode="numeric"
              autoComplete="one-time-code"
              placeholder="123456"
              maxLength={6}
              hint="Six digits from your authenticator app."
              error={errors.twoFactorCode?.message}
              {...register('twoFactorCode')}
            />
          ) : null}

          <div data-login-field>
            <Button type="submit" size="lg" fullWidth loading={isSubmitting}>
              {isSubmitting ? 'Signing in' : 'Sign in'}
            </Button>
          </div>

          <div className={s.meta}>
            <span>Forgotten your password? Contact your administrator.</span>
            <button type="button" className={s.themeToggle} onClick={toggle}>
              {theme === 'dark' ? 'Light mode' : 'Dark mode'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
