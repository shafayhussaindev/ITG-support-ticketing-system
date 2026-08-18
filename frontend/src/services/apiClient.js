import { tokenStore } from './tokenStore';

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5180/api/v1';

/**
 * A failed API call, carrying the machine-readable code from the server's
 * RFC 7807 Problem Details body rather than a parsed message string.
 */
export class ApiError extends Error {
  constructor({ status, code, title, detail, fieldErrors, correlationId }) {
    super(detail || title || 'The request failed.');
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.title = title;
    this.detail = detail;
    /** Map of field name to messages, populated for validation failures. */
    this.fieldErrors = fieldErrors ?? null;
    this.correlationId = correlationId ?? null;
  }

  get isValidation() {
    return this.status === 400 && Boolean(this.fieldErrors);
  }

  get isNetwork() {
    return this.status === 0;
  }
}

/**
 * Emitted when the session cannot be recovered. AuthContext listens and sends the
 * user to the sign-in page. Using an event rather than a direct import keeps this
 * module free of React and therefore trivially testable.
 */
export const SESSION_EXPIRED_EVENT = 'sts:session-expired';

function correlationId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

async function parseProblem(response) {
  let body = null;

  try {
    const text = await response.text();
    body = text ? JSON.parse(text) : null;
  } catch {
    body = null;
  }

  return new ApiError({
    status: response.status,
    code: body?.code ?? body?.type ?? `http_${response.status}`,
    title: body?.title,
    detail: body?.detail ?? defaultDetailFor(response.status),
    fieldErrors: body?.errors ?? null,
    correlationId: body?.correlationId ?? response.headers.get('X-Correlation-Id'),
  });
}

function defaultDetailFor(status) {
  switch (status) {
    case 401:
      return 'Your session has ended. Please sign in again.';
    case 403:
      return 'You do not have permission to do that.';
    case 404:
      return 'That item could not be found.';
    case 409:
      return 'Someone else changed this while you were working. Reload and try again.';
    case 429:
      return 'Too many requests. Wait a moment and try again.';
    default:
      return 'Something went wrong. Please try again.';
  }
}

/*
  Single-flight refresh.

  Several queries can fail with 401 in the same tick — a dashboard might fire five
  requests at once. Without this, each would independently try to refresh, and
  because refresh tokens are single-use the second attempt would be treated as
  token reuse and revoke the entire session. Sharing one in-flight promise means
  exactly one rotation happens and every caller awaits the same result.
*/
let refreshInFlight = null;

async function refreshSession() {
  const refreshToken = tokenStore.getRefreshToken();

  if (!refreshToken) {
    throw new ApiError({ status: 401, code: 'no_refresh_token', detail: 'No session to restore.' });
  }

  const response = await fetch(`${BASE_URL}/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
  });

  if (!response.ok) {
    throw await parseProblem(response);
  }

  const payload = await response.json();
  tokenStore.setTokens(payload);
  return payload;
}

function ensureRefresh() {
  refreshInFlight ??= refreshSession().finally(() => {
    refreshInFlight = null;
  });

  return refreshInFlight;
}

/**
 * Issues an API request, attaching the bearer token and recovering from a single
 * expired-token 401 by rotating the refresh token and retrying once.
 */
export async function apiRequest(path, options = {}) {
  const { method = 'GET', body, signal, anonymous = false, retryOnUnauthorized = true } = options;

  const headers = {
    Accept: 'application/json',
    'X-Correlation-Id': correlationId(),
  };

  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  if (!anonymous) {
    const token = tokenStore.getAccessToken();
    if (token) {
      headers.Authorization = `Bearer ${token}`;
    }
  }

  let response;

  try {
    response = await fetch(`${BASE_URL}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch (error) {
    if (error.name === 'AbortError') {
      throw error;
    }

    // Distinguished from an HTTP error so the UI can say "you appear to be offline"
    // rather than "the server rejected the request".
    throw new ApiError({
      status: 0,
      code: 'network_error',
      detail: 'Cannot reach the server. Check your connection and try again.',
    });
  }

  if (response.status === 401 && !anonymous && retryOnUnauthorized) {
    try {
      await ensureRefresh();
    } catch {
      tokenStore.clear();
      globalThis.dispatchEvent?.(new CustomEvent(SESSION_EXPIRED_EVENT));
      throw await parseProblem(response);
    }

    return apiRequest(path, { ...options, retryOnUnauthorized: false });
  }

  if (!response.ok) {
    throw await parseProblem(response);
  }

  if (response.status === 204) {
    return null;
  }

  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

export const api = {
  get: (path, options) => apiRequest(path, { ...options, method: 'GET' }),
  post: (path, body, options) => apiRequest(path, { ...options, method: 'POST', body }),
  put: (path, body, options) => apiRequest(path, { ...options, method: 'PUT', body }),
  patch: (path, body, options) => apiRequest(path, { ...options, method: 'PATCH', body }),
  delete: (path, options) => apiRequest(path, { ...options, method: 'DELETE' }),
};
