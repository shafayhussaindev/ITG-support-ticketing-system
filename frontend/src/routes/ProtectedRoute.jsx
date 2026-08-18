import { Navigate, useLocation } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { EmptyState, LoadingState } from '@/components/ui';
import { ChangePasswordPage } from '@/features/auth/ChangePasswordPage';

/**
 * Gate for authenticated routes.
 *
 * While a session is being restored from the stored refresh token this renders a
 * loading state rather than redirecting — otherwise every page reload would bounce
 * an authenticated user to the sign-in screen before the refresh completes.
 *
 * The optional permission check hides a route a user cannot use. It is convenience,
 * not security: the API applies the same check independently, so a user who edits
 * the URL still receives a 403 or 404 from the server.
 *
 * An account still on an administrator-issued password is shown the change screen in
 * place of whatever it asked for. The API refuses everything else regardless; rendering
 * the usual shell with every panel erroring would be a worse way to say the same thing.
 */
export function ProtectedRoute({ children, permission }) {
  const { isAuthenticated, isRestoring, can, user } = useAuth();
  const location = useLocation();

  if (isRestoring) {
    return <LoadingState label="Checking your session" />;
  }

  if (!isAuthenticated) {
    // Remember where they were going so sign-in can send them back there.
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  if (user?.mustChangePassword) {
    return <ChangePasswordPage />;
  }

  if (permission && !can(permission)) {
    return (
      <EmptyState
        icon="⊘"
        title="You do not have access to this area"
        message={`This page requires the "${permission}" permission. If you believe you should have it, ask your administrator.`}
      />
    );
  }

  return children;
}
