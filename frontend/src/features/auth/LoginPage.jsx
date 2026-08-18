import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Field, LoadingState } from '@/components/ui';
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
    <div className={s.page}>
      <aside className={s.aside}>
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
            <li>
              <span aria-hidden="true">▸</span> SLA tracked against business hours and holidays
            </li>
            <li>
              <span aria-hidden="true">▸</span> Every change attributed to a person, a rule, or AI
            </li>
            <li>
              <span aria-hidden="true">▸</span> Internal notes never visible to requesters
            </li>
          </ul>
        </div>

        <p className={s.asideFoot}>Phase 1 build — authentication and master data</p>
      </aside>

      <div className={s.panel}>
        <form className={s.form} onSubmit={handleSubmit(onSubmit)} noValidate>
          <div>
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

          <Field
            label="Password"
            type="password"
            autoComplete="current-password"
            placeholder="••••••••••••"
            required
            error={errors.password?.message}
            {...register('password')}
          />

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

          <Button type="submit" size="lg" fullWidth loading={isSubmitting}>
            {isSubmitting ? 'Signing in' : 'Sign in'}
          </Button>

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
