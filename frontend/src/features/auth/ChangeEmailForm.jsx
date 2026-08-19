import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { authService } from '@/services/authService';
import { useAuth } from '@/contexts/AuthContext';
import { Button } from '@/components/ui';
import s from './ChangePasswordPage.module.css';

/**
 * Changes the address the account signs in with.
 *
 * Shares the password form's styling deliberately — this is the same kind of act, and
 * presenting it as an ordinary profile edit would understate what it does.
 */
export function ChangeEmailForm({ currentEmail }) {
  const { logout } = useAuth();

  const [newEmail, setNewEmail] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  const [done, setDone] = useState(null);

  const change = useMutation({
    mutationFn: () => authService.changeEmail({ currentPassword, newEmail: newEmail.trim() }),
    onSuccess: (result) => setDone(result),
  });

  const unchanged = newEmail.trim().toLowerCase() === (currentEmail ?? '').toLowerCase();
  const canSubmit = newEmail.trim().length > 3 && currentPassword && !unchanged;

  if (done) {
    return (
      <div className={s.doneBox}>
        <p className={s.doneText}>{done.message}</p>
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
        <span className={s.label}>New email address</span>
        <input
          className={s.input}
          type="email"
          autoComplete="email"
          required
          value={newEmail}
          placeholder={currentEmail}
          onChange={(e) => setNewEmail(e.target.value)}
        />
        {unchanged && newEmail ? (
          <span className={`${s.hint} ${s.warn}`}>That is already your address.</span>
        ) : (
          <span className={s.hint}>
            Nothing is sent to confirm it, so a typo locks you out of your own account.
          </span>
        )}
      </label>

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
        <span className={s.hint}>
          Asked for because this changes how you sign in, not just what is displayed.
        </span>
      </label>

      {change.isError ? (
        <p className={s.error}>{change.error.detail ?? change.error.message}</p>
      ) : null}

      <Button type="submit" loading={change.isPending} disabled={!canSubmit}>
        Change email address
      </Button>
    </form>
  );
}
