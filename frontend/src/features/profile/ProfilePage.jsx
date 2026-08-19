import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { authService } from '@/services/authService';
import { ChangePasswordForm } from '@/features/auth/ChangePasswordPage';
import { ChangeEmailForm } from '@/features/auth/ChangeEmailForm';
import { useAuth } from '@/contexts/AuthContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useToast } from '@/contexts/ToastContext';
import {
  Badge,
  Button,
  Card,
  CardBody,
  CardHeader,
  ConfirmDialog,
  ErrorState,
  LoadingState,
} from '@/components/ui';
import s from './ProfilePage.module.css';

export function ProfilePage() {
  const { user: cachedUser, logout } = useAuth();
  const { theme, setTheme } = useTheme();
  const toast = useToast();
  const navigate = useNavigate();

  const [confirmRevokeAll, setConfirmRevokeAll] = useState(false);
  const [changingPassword, setChangingPassword] = useState(false);
  const [changingEmail, setChangingEmail] = useState(false);
  const [revoking, setRevoking] = useState(false);

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: authService.me,
    placeholderData: cachedUser,
  });

  const user = data ?? cachedUser;

  if (isLoading && !user) {
    return <LoadingState label="Loading your profile" />;
  }

  if (isError && !user) {
    return <ErrorState error={error} onRetry={refetch} title="Could not load your profile" />;
  }

  async function revokeAllSessions() {
    setRevoking(true);

    try {
      // A real call: the server revokes every refresh-token family for this user,
      // which signs them out of every other browser and device as well as this one.
      await logout({ allSessions: true });
      toast.success('All sessions ended', 'You have been signed out everywhere.');
      navigate('/login', { replace: true });
    } catch {
      toast.error('Could not end your sessions', 'Try again, or contact your administrator.');
    } finally {
      setRevoking(false);
      setConfirmRevokeAll(false);
    }
  }

  return (
    <div className={s.grid}>
      <Card>
        <CardHeader title="Profile" subtitle="Managed by your administrator" />
        <CardBody>
          <dl className={s.dl}>
            <dt className={s.dt}>Full name</dt>
            <dd className={s.dd}>{user?.fullName}</dd>

            <dt className={s.dt}>Email</dt>
            <dd className={s.dd}>{user?.email}</dd>

            <dt className={s.dt}>Job title</dt>
            <dd className={s.dd}>{user?.jobTitle ?? '—'}</dd>

            <dt className={s.dt}>Organization</dt>
            <dd className={s.dd}>{user?.organizationName}</dd>

            <dt className={s.dt}>Department</dt>
            <dd className={s.dd}>{user?.departmentName ?? '—'}</dd>

            <dt className={s.dt}>Office</dt>
            <dd className={s.dd}>{user?.officeName ?? '—'}</dd>

            <dt className={s.dt}>Roles</dt>
            <dd className={s.dd}>
              <div className={s.badges}>
                {user?.roles?.map((role) => (
                  <Badge key={role} tone="primary">
                    {role}
                  </Badge>
                ))}
              </div>
            </dd>
          </dl>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="Security" />
        <CardBody>
          <div className={s.row}>
            <div>
              <p className={s.rowTitle}>Two-factor authentication</p>
              <p className={s.rowNote}>
                {user?.twoFactorEnabled
                  ? 'Enabled. A code from your authenticator app is required at every sign-in.'
                  : 'Not enabled. Enrolment arrives with the account settings screen in a later phase.'}
              </p>
            </div>
            <Badge tone={user?.twoFactorEnabled ? 'success' : 'neutral'}>
              {user?.twoFactorEnabled ? 'On' : 'Off'}
            </Badge>
          </div>

          <div className={s.row}>
            <div>
              <p className={s.rowTitle}>Email address</p>
              <p className={s.rowNote}>
                This is what you sign in with, so changing it needs your password and
                signs out every session. It is not verified — no mail is sent — so
                check it carefully.
              </p>
            </div>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setChangingEmail((open) => !open)}
            >
              {changingEmail ? 'Cancel' : 'Change email'}
            </Button>
          </div>

          {changingEmail ? (
            <div className={s.passwordForm}>
              <ChangeEmailForm currentEmail={user?.email} />
            </div>
          ) : null}

          <div className={s.row}>
            <div>
              <p className={s.rowTitle}>Password</p>
              <p className={s.rowNote}>
                Changing it signs out every session, this one included — if the reason
                is that somebody else knows it, leaving their session alive would
                defeat the exercise.
              </p>
            </div>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setChangingPassword((open) => !open)}
            >
              {changingPassword ? 'Cancel' : 'Change password'}
            </Button>
          </div>

          {changingPassword ? (
            <div className={s.passwordForm}>
              <ChangePasswordForm />
            </div>
          ) : null}

          <div className={s.row}>
            <div>
              <p className={s.rowTitle}>Active sessions</p>
              <p className={s.rowNote}>
                Signing out everywhere revokes every refresh token issued to this account,
                on every device. Use it if you think someone else has your password.
              </p>
            </div>
            <Button variant="danger" size="sm" onClick={() => setConfirmRevokeAll(true)}>
              Sign out everywhere
            </Button>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="Appearance" subtitle="Stored in this browser only" />
        <CardBody>
          <fieldset className={s.fieldset}>
            <legend className={s.legend}>Theme</legend>

            {[
              { value: 'light', label: 'Light' },
              { value: 'dark', label: 'Dark' },
            ].map((option) => (
              <label key={option.value} className={s.radio}>
                <input
                  type="radio"
                  name="theme"
                  value={option.value}
                  checked={theme === option.value}
                  onChange={() => setTheme(option.value)}
                />
                <span>{option.label}</span>
              </label>
            ))}
          </fieldset>

          <p className={s.rowNote}>
            Times are stored in UTC on the server and shown in {user?.timeZoneId}.
          </p>
        </CardBody>
      </Card>

      <ConfirmDialog
        open={confirmRevokeAll}
        title="Sign out of every device?"
        message="Every active session for your account will be ended immediately, including this one."
        confirmLabel="Sign out everywhere"
        variant="danger"
        loading={revoking}
        onConfirm={revokeAllSessions}
        onCancel={() => setConfirmRevokeAll(false)}
      />
    </div>
  );
}
