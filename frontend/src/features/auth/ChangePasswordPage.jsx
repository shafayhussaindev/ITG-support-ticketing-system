import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { authService } from '@/services/authService';
import { useAuth } from '@/contexts/AuthContext';
import { Button, Card, CardBody, CardHeader } from '@/components/ui';
import s from './ChangePasswordPage.module.css';

const MINIMUM_LENGTH = 12;

/**
 * Rough strength feedback, shown as guidance rather than as a gate.
 *
 * Only length is enforced server-side. Composition rules push people towards
 * `Password1!` and away from the long passphrases that actually resist guessing, so
 * this reports what the password has going for it without refusing anything the
 * server would accept.
 */
function assess(password) {
  if (!password) return null;

  const notes = [];

  if (password.length >= 20) notes.push('long enough to be hard to guess');
  else if (password.length >= MINIMUM_LENGTH) notes.push('long enough');
  else return { tone: 'short', text: `${MINIMUM_LENGTH - password.length} more characters needed` };

  if (/\s/.test(password)) notes.push('a passphrase — good');
  if (new Set(password).size < password.length / 3) notes.push('quite repetitive');

  return { tone: 'ok', text: notes.join(', ') };
}

/**
 * The password change form.
 *
 * Used in two places: forced, when the account is still on an administrator-issued
 * password, and voluntary, from the profile. The forced variant explains why it is
 * blocking rather than simply blocking.
 */
export function ChangePasswordForm({ forced = false, onDone }) {
  const { logout } = useAuth();

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [done, setDone] = useState(false);

  const change = useMutation({
    mutationFn: () => authService.changePassword({ currentPassword, newPassword }),
    onSuccess: () => {
      setDone(true);
      onDone?.();
    },
  });

  const strength = assess(newPassword);
  const mismatch = confirm.length > 0 && confirm !== newPassword;
  const canSubmit = currentPassword && newPassword.length >= MINIMUM_LENGTH && !mismatch
    && confirm === newPassword;

  if (done) {
    return (
      <div className={s.doneBox}>
        <p className={s.doneText}>
          Password changed. Every session has been signed out, including this one —
          sign in again with the new password.
        </p>
        <Button onClick={() => logout()}>Go to sign in</Button>
      </div>
    );
  }

  return (
    <form
      className={s.form}
      onSubmit={(event) => {
        event.preventDefault();
        if (canSubmit) change.mutate();
      }}
    >
      <label className={s.field}>
        <span className={s.label}>Current password</span>
        <input
          className={s.input}
          type="password"
          autoComplete="current-password"
          required
          value={currentPassword}
          onChange={(e) => setCurrentPassword(e.target.value)}
        />
        {forced ? (
          <span className={s.hint}>The temporary one your administrator gave you.</span>
        ) : null}
      </label>

      <label className={s.field}>
        <span className={s.label}>New password</span>
        <input
          className={s.input}
          type="password"
          autoComplete="new-password"
          required
          minLength={MINIMUM_LENGTH}
          value={newPassword}
          onChange={(e) => setNewPassword(e.target.value)}
        />
        <span className={`${s.hint} ${strength?.tone === 'short' ? s.warn : ''}`}>
          {strength?.text
            ?? `At least ${MINIMUM_LENGTH} characters. A short phrase you can remember beats a short jumble you cannot.`}
        </span>
      </label>

      <label className={s.field}>
        <span className={s.label}>Confirm new password</span>
        <input
          className={s.input}
          type="password"
          autoComplete="new-password"
          required
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
        />
        {mismatch ? <span className={`${s.hint} ${s.warn}`}>These do not match.</span> : null}
      </label>

      {change.isError ? (
        <p className={s.error}>{change.error.detail ?? change.error.message}</p>
      ) : null}

      <Button type="submit" fullWidth loading={change.isPending} disabled={!canSubmit}>
        Change password
      </Button>
    </form>
  );
}

/**
 * The full-page block shown while an account is still on an issued password.
 *
 * It stands in front of the application rather than inside it, because the API will
 * refuse everything else anyway — showing the normal shell with every panel erroring
 * would be a worse way to deliver the same news.
 */
export function ChangePasswordPage() {
  const { user, logout } = useAuth();

  return (
    <div className={s.page}>
      <Card className={s.card}>
        <CardHeader
          title="Set your own password"
          subtitle={`Signed in as ${user?.email ?? 'your account'}`}
        />
        <CardBody>
          <p className={s.intro}>
            This account is using a password an administrator issued. Until you replace
            it, the rest of the system is closed — that password has been seen by
            somebody other than you.
          </p>

          <ChangePasswordForm forced />

          <button type="button" className={s.signOut} onClick={() => logout()}>
            Sign out instead
          </button>
        </CardBody>
      </Card>
    </div>
  );
}
