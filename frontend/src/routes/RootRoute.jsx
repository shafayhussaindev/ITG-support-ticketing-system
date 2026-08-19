import { Suspense, lazy } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { LoadingState } from '@/components/ui';

/*
  Split out for the same reason the dashboard is: somebody who uses this every day
  signs in and is redirected, and should never pay to download a marketing page they
  will not see. The visitor who does see it waits one small request, on the one page
  where a moment is affordable.
*/
const LandingPage = lazy(() =>
  import('@/features/marketing/LandingPage').then((m) => ({ default: m.LandingPage })),
);

/**
 * What sits at the root.
 *
 * A visitor gets the landing page; somebody already signed in goes straight to their
 * dashboard. Showing a marketing page to a person who uses this every day would be
 * an obstacle between them and their queue.
 *
 * The restoring state matters: on a page reload the session is recovered from the
 * stored refresh token, and rendering the landing page during that window would flash
 * a signed-out page at somebody who is signed in.
 */
export function RootRoute() {
  const { isAuthenticated, isRestoring } = useAuth();

  if (isRestoring) {
    return <LoadingState label="Checking your session" />;
  }

  if (isAuthenticated) {
    return <Navigate to="/dashboard" replace />;
  }

  return (
    <Suspense fallback={<LoadingState label="Loading" />}>
      <LandingPage />
    </Suspense>
  );
}
