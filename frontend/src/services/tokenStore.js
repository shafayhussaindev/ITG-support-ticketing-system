const REFRESH_KEY = 'sts.refreshToken';

/*
  Token storage.

  The access token is held in memory only. It never reaches localStorage, so a
  cross-site scripting bug cannot read it out of persistent storage, and it is gone
  the moment the tab closes.

  The refresh token does go to localStorage, because the API returns it in the
  response body and the session has to survive a page reload. That is a real
  trade-off: a successful XSS could exfiltrate it. Two things limit the damage —
  refresh tokens are single-use, so a stolen one is detected the moment either party
  uses it after the other, and detection revokes the whole family. The stronger fix
  is to move the refresh token into an HttpOnly cookie, which is a backend change
  tracked as a hardening item.
*/

let accessToken = null;
let accessTokenExpiresAt = null;

const listeners = new Set();

function notify() {
  for (const listener of listeners) {
    listener();
  }
}

export const tokenStore = {
  getAccessToken: () => accessToken,

  /** True when the access token is missing or within 30 seconds of expiry. */
  isAccessTokenStale() {
    if (!accessToken || !accessTokenExpiresAt) {
      return true;
    }
    return Date.now() >= accessTokenExpiresAt - 30_000;
  },

  getRefreshToken() {
    try {
      return localStorage.getItem(REFRESH_KEY);
    } catch {
      // Private browsing modes can throw on storage access.
      return null;
    }
  },

  setTokens({ accessToken: next, accessTokenExpiresAtUtc, refreshToken }) {
    accessToken = next ?? null;
    accessTokenExpiresAt = accessTokenExpiresAtUtc
      ? new Date(accessTokenExpiresAtUtc).getTime()
      : null;

    try {
      if (refreshToken) {
        localStorage.setItem(REFRESH_KEY, refreshToken);
      }
    } catch {
      // Non-fatal: the session simply will not survive a reload.
    }

    notify();
  },

  clear() {
    accessToken = null;
    accessTokenExpiresAt = null;

    try {
      localStorage.removeItem(REFRESH_KEY);
    } catch {
      // Ignore.
    }

    notify();
  },

  subscribe(listener) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};
