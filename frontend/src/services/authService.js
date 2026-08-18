import { api } from './apiClient';
import { tokenStore } from './tokenStore';

export const authService = {
  async login({ email, password, twoFactorCode }) {
    const payload = await api.post(
      '/auth/login',
      { email, password, twoFactorCode: twoFactorCode || null },
      { anonymous: true },
    );

    tokenStore.setTokens(payload);
    return payload.user;
  },

  /** Restores a session on page load using the stored refresh token. */
  async restore() {
    const refreshToken = tokenStore.getRefreshToken();

    if (!refreshToken) {
      return null;
    }

    const payload = await api.post('/auth/refresh', { refreshToken }, { anonymous: true });
    tokenStore.setTokens(payload);
    return payload.user;
  },

  async logout({ allSessions = false } = {}) {
    try {
      await api.post('/auth/logout', {
        refreshToken: tokenStore.getRefreshToken(),
        allSessions,
      });
    } catch {
      // A failed revoke must not trap the user in a signed-in state. The local
      // tokens are discarded regardless, and the server-side token expires anyway.
    } finally {
      tokenStore.clear();
    }
  },

  me: () => api.get('/auth/me'),
};
