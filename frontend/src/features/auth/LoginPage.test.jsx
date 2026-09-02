import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LoginPage } from './LoginPage';
import { renderWithProviders } from '@/test/renderWithProviders';
import { ApiError } from '@/services/apiClient';
import { authService } from '@/services/authService';

vi.mock('@/services/authService', () => ({
  authService: {
    login: vi.fn(),
    restore: vi.fn().mockResolvedValue(null),
    logout: vi.fn(),
    me: vi.fn(),
  },
}));

async function renderLogin() {
  renderWithProviders(<LoginPage />, { route: '/login' });
  // AuthProvider resolves its restore attempt before the form is shown.
  return screen.findByRole('button', { name: /sign in/i });
}

describe('LoginPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authService.restore.mockResolvedValue(null);
  });

  it('rejects an invalid email without calling the API', async () => {
    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'not-an-email');
    await user.type(screen.getByLabelText(/password/i), 'somepassword');
    await user.click(submit);

    expect(await screen.findByText(/valid email address/i)).toBeInTheDocument();
    expect(authService.login).not.toHaveBeenCalled();
  });

  it('requires a password', async () => {
    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'agent@itg.test');
    await user.click(submit);

    expect(await screen.findByText(/enter your password/i)).toBeInTheDocument();
    expect(authService.login).not.toHaveBeenCalled();
  });

  it('submits valid credentials to the API', async () => {
    authService.login.mockResolvedValue({ fullName: 'Ayesha Malik', firstName: 'Ayesha' });

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'agent@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'CorrectPassword1!');
    await user.click(submit);

    await waitFor(() =>
      expect(authService.login).toHaveBeenCalledWith(
        expect.objectContaining({ email: 'agent@itg.test', password: 'CorrectPassword1!' }),
      ),
    );
  });

  it('shows one generic message for bad credentials', async () => {
    // The backend does not distinguish an unknown email from a wrong password, and
    // the UI must not undo that by inferring more than the server said.
    authService.login.mockRejectedValue(
      new ApiError({ status: 401, code: 'invalid_credentials', detail: 'Email or password is incorrect.' }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'nobody@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'WrongPassword1!');
    await user.click(submit);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/email or password is incorrect/i);
    expect(alert).not.toHaveTextContent(/no account/i);
  });

  it('puts a server-side validation refusal on the field, not on the password', async () => {
    // The server validates more strictly than the form. When it refuses the email,
    // saying "email or password is incorrect" sends somebody off to reset a password
    // that was never the problem. This is exactly what happened on the first deploy.
    authService.login.mockRejectedValue(
      new ApiError({
        status: 400,
        code: 'validation_failed',
        detail: 'Correct the highlighted fields and try again.',
        fieldErrors: { Email: ["'Email' is not a valid email address."] },
      }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'someone@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'AnyPassword1!');
    await user.click(submit);

    // Two alerts are correct here: the form-level summary and the field's own
    // message. Neither may claim the password was wrong.
    const alerts = await screen.findAllByRole('alert');
    for (const alert of alerts) {
      expect(alert).not.toHaveTextContent(/password is incorrect/i);
    }
    expect(screen.getByLabelText(/email/i)).toHaveAccessibleDescription(/not a valid email address/i);
  });

  it('does not blame the password for a failure that never reached the credential check', async () => {
    // A 404 from a wrong API address, a 502 from a proxy: none of these mean the
    // password was wrong, and that message is the one answer guaranteed to mislead.
    authService.login.mockRejectedValue(
      new ApiError({ status: 404, code: 'http_404', detail: 'Not found.', correlationId: 'abc-123' }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'someone@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'AnyPassword1!');
    await user.click(submit);

    const alert = await screen.findByRole('alert');
    expect(alert).not.toHaveTextContent(/password is incorrect/i);
    expect(alert).toHaveTextContent(/HTTP 404/);
    expect(alert).toHaveTextContent(/abc-123/);
  });

  it('reveals the verification code field only when the server asks for it', async () => {
    authService.login.mockRejectedValue(
      new ApiError({ status: 401, code: 'two_factor_required', detail: 'A code is required.' }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    expect(screen.queryByLabelText(/verification code/i)).not.toBeInTheDocument();

    await user.type(screen.getByLabelText(/email/i), 'superadmin@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'CorrectPassword1!');
    await user.click(submit);

    expect(await screen.findByLabelText(/verification code/i)).toBeInTheDocument();
  });

  it('explains a lockout rather than reporting it as a wrong password', async () => {
    authService.login.mockRejectedValue(
      new ApiError({
        status: 423,
        code: 'account_locked',
        detail: 'This account is locked until 2026-08-18 14:00:00Z.',
      }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'locked@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'AnyPassword1!');
    await user.click(submit);

    expect(await screen.findByRole('alert')).toHaveTextContent(/locked/i);
  });

  it('tells the user the server is unreachable instead of blaming their password', async () => {
    authService.login.mockRejectedValue(
      new ApiError({ status: 0, code: 'network_error', detail: 'Cannot reach the server.' }),
    );

    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'agent@itg.test');
    await user.type(screen.getByLabelText(/password/i), 'CorrectPassword1!');
    await user.click(submit);

    expect(await screen.findByRole('alert')).toHaveTextContent(/cannot reach the server/i);
  });

  it('marks the password field as invalid for assistive technology', async () => {
    const user = userEvent.setup();
    const submit = await renderLogin();

    await user.type(screen.getByLabelText(/email/i), 'agent@itg.test');
    await user.click(submit);

    await waitFor(() =>
      expect(screen.getByLabelText(/password/i)).toHaveAttribute('aria-invalid', 'true'),
    );
  });
});
