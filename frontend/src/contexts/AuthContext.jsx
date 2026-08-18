import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { authService } from '@/services/authService';
import { SESSION_EXPIRED_EVENT } from '@/services/apiClient';

const AuthContext = createContext(null);

export function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [status, setStatus] = useState('restoring');
  const queryClient = useQueryClient();

  // Attempt to resume a session from the stored refresh token before rendering
  // routes, so a reload does not bounce an authenticated user to the sign-in page.
  useEffect(() => {
    let cancelled = false;

    authService
      .restore()
      .then((restored) => {
        if (!cancelled) {
          setUser(restored);
          setStatus(restored ? 'authenticated' : 'anonymous');
        }
      })
      .catch(() => {
        if (!cancelled) {
          setUser(null);
          setStatus('anonymous');
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  // The API client cannot import React, so it announces an unrecoverable 401 as a
  // DOM event and this is where the app reacts to it.
  useEffect(() => {
    function onSessionExpired() {
      setUser(null);
      setStatus('anonymous');
      queryClient.clear();
    }

    globalThis.addEventListener(SESSION_EXPIRED_EVENT, onSessionExpired);
    return () => globalThis.removeEventListener(SESSION_EXPIRED_EVENT, onSessionExpired);
  }, [queryClient]);

  const login = useCallback(async (credentials) => {
    const signedIn = await authService.login(credentials);
    setUser(signedIn);
    setStatus('authenticated');
    return signedIn;
  }, []);

  const logout = useCallback(
    async (options) => {
      await authService.logout(options);
      setUser(null);
      setStatus('anonymous');
      // Clearing the cache prevents the next user on this machine from briefly
      // seeing the previous user's data from a stale query.
      queryClient.clear();
    },
    [queryClient],
  );

  const value = useMemo(() => {
    const permissions = new Set(user?.permissions ?? []);

    return {
      user,
      status,
      isAuthenticated: status === 'authenticated' && Boolean(user),
      isRestoring: status === 'restoring',
      login,
      logout,
      setUser,

      /**
       * Mirrors the backend permission check so the interface can hide controls the
       * user cannot use. This is a usability aid only — the API re-checks every
       * permission on every request, and hiding a button is not a security control.
       */
      can: (permission) => permissions.has(permission),
      canAny: (...required) => required.some((p) => permissions.has(p)),
      canAll: (...required) => required.every((p) => permissions.has(p)),
    };
  }, [user, status, login, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);

  if (!context) {
    throw new Error('useAuth must be used inside an AuthProvider.');
  }

  return context;
}
