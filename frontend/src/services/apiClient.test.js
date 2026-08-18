import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, SESSION_EXPIRED_EVENT } from './apiClient';
import { tokenStore } from './tokenStore';

function jsonResponse(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function problemResponse(body, status) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

describe('apiClient', () => {
  beforeEach(() => {
    tokenStore.clear();
    vi.restoreAllMocks();
  });

  it('attaches the bearer token and a correlation id', async () => {
    tokenStore.setTokens({
      accessToken: 'token-abc',
      accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      refreshToken: 'refresh-abc',
    });

    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
    vi.stubGlobal('fetch', fetchMock);

    await api.get('/auth/me');

    const [, init] = fetchMock.mock.calls[0];
    expect(init.headers.Authorization).toBe('Bearer token-abc');
    expect(init.headers['X-Correlation-Id']).toBeTruthy();
  });

  it('turns a Problem Details body into an ApiError carrying the code', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        problemResponse(
          {
            code: 'invalid_credentials',
            title: 'Authentication failed.',
            detail: 'Email or password is incorrect.',
            correlationId: 'abc-123',
          },
          401,
        ),
      ),
    );

    await expect(api.post('/auth/login', {}, { anonymous: true })).rejects.toMatchObject({
      name: 'ApiError',
      status: 401,
      code: 'invalid_credentials',
      correlationId: 'abc-123',
    });
  });

  it('exposes field errors from a validation failure', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        problemResponse(
          { code: 'validation_failed', errors: { Email: ['Not a valid email address.'] } },
          400,
        ),
      ),
    );

    try {
      await api.post('/auth/login', {}, { anonymous: true });
      expect.unreachable('should have thrown');
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError);
      expect(error.isValidation).toBe(true);
      expect(error.fieldErrors.Email).toContain('Not a valid email address.');
    }
  });

  it('refreshes once and retries when the access token has expired', async () => {
    tokenStore.setTokens({
      accessToken: 'stale',
      accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      refreshToken: 'refresh-1',
    });

    const fetchMock = vi
      .fn()
      // The original request is rejected.
      .mockResolvedValueOnce(problemResponse({ code: 'unauthorized' }, 401))
      // The refresh succeeds.
      .mockResolvedValueOnce(
        jsonResponse({
          accessToken: 'fresh',
          accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
          refreshToken: 'refresh-2',
        }),
      )
      // The retry succeeds.
      .mockResolvedValueOnce(jsonResponse({ ok: true }));

    vi.stubGlobal('fetch', fetchMock);

    const result = await api.get('/auth/me');

    expect(result).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(tokenStore.getAccessToken()).toBe('fresh');

    // The retry must carry the new token, not the one that was just rejected.
    const [, retryInit] = fetchMock.mock.calls[2];
    expect(retryInit.headers.Authorization).toBe('Bearer fresh');
  });

  it('rotates only once when several requests fail at the same time', async () => {
    // Refresh tokens are single-use. Two concurrent rotations would look like token
    // reuse to the server and revoke the entire session.
    tokenStore.setTokens({
      accessToken: 'stale',
      accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      refreshToken: 'refresh-1',
    });

    let refreshCalls = 0;

    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation((url) => {
        if (String(url).endsWith('/auth/refresh')) {
          refreshCalls += 1;
          return Promise.resolve(
            jsonResponse({
              accessToken: 'fresh',
              accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
              refreshToken: 'refresh-2',
            }),
          );
        }

        return Promise.resolve(
          tokenStore.getAccessToken() === 'fresh'
            ? jsonResponse({ ok: true })
            : problemResponse({ code: 'unauthorized' }, 401),
        );
      }),
    );

    await Promise.all([api.get('/a'), api.get('/b'), api.get('/c')]);

    expect(refreshCalls).toBe(1);
  });

  it('clears the session and announces expiry when the refresh itself fails', async () => {
    tokenStore.setTokens({
      accessToken: 'stale',
      accessTokenExpiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
      refreshToken: 'refresh-dead',
    });

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(problemResponse({ code: 'refresh_token_reused' }, 401)),
    );

    const onExpired = vi.fn();
    globalThis.addEventListener(SESSION_EXPIRED_EVENT, onExpired);

    await expect(api.get('/auth/me')).rejects.toBeInstanceOf(ApiError);

    expect(onExpired).toHaveBeenCalledTimes(1);
    expect(tokenStore.getAccessToken()).toBeNull();
    expect(tokenStore.getRefreshToken()).toBeNull();

    globalThis.removeEventListener(SESSION_EXPIRED_EVENT, onExpired);
  });

  it('reports an unreachable server distinctly from an HTTP error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    try {
      await api.get('/health', { anonymous: true });
      expect.unreachable('should have thrown');
    } catch (error) {
      expect(error.isNetwork).toBe(true);
      expect(error.code).toBe('network_error');
    }
  });
});
