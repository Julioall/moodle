import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  AppHttpError,
  clearAdmissionCooldowns,
  createAppClient,
  getAdmissionCooldownRemainingMs,
  parseRetryAfter,
  setAdmissionIdentityScope,
  shouldRetryAppQuery,
} from '../integrations/http/api-client';

afterEach(() => {
  vi.useRealTimers();
  clearAdmissionCooldowns();
  setAdmissionIdentityScope(undefined);
});

describe('Retry-After parsing', () => {
  it('parses seconds and HTTP dates using the server Date header when available', () => {
    expect(parseRetryAfter('3', 1_000)).toBe(3_000);
    expect(parseRetryAfter('Wed, 21 Oct 2015 07:27:05 GMT', 0, 'Wed, 21 Oct 2015 07:27:00 GMT')).toBe(5_000);
    expect(parseRetryAfter('Wed, 21 Oct 2015 07:26:00 GMT', 0, 'Wed, 21 Oct 2015 07:27:00 GMT')).toBe(0);
  });

  it.each([null, '', 'soon', '-1', '1.5'])('ignores absent or invalid Retry-After value %s', (value) => {
    expect(parseRetryAfter(value, 1_000)).toBeUndefined();
  });
});

describe('app client admission cooldown', () => {
  it('preserves 429 origin, family, retry deadline and correlation and waits before the next same-family GET', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-07T00:00:00Z'));
    setAdmissionIdentityScope('owner-1');
    const fetcher = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ error: { code: 'connector_rate_limited', origin: 'connector', family: 'course_catalog' } }), {
        status: 429,
        headers: { 'Retry-After': '2', 'X-Correlation-ID': 'server-correlation' },
      }))
      .mockResolvedValueOnce(new Response('{"ok":true}', { status: 200 }));
    const client = createAppClient(fetcher, 10_000);

    await expect(client.get('/api/courses?page=1', { admissionFamily: 'course_catalog' })).rejects.toMatchObject({
      status: 429,
      origin: 'connector',
      family: 'course_catalog',
      retryAfterMs: 2_000,
      correlationId: 'server-correlation',
    } satisfies Partial<AppHttpError>);
    expect(getAdmissionCooldownRemainingMs('course_catalog')).toBe(2_000);

    const resumed = client.get('/api/courses?page=2', { admissionFamily: 'course_catalog' });
    await vi.advanceTimersByTimeAsync(1_999);
    expect(fetcher).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    await expect(resumed).resolves.toEqual({ ok: true });
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('does not delay unrelated families or carry a cooldown across owner changes', async () => {
    vi.useFakeTimers();
    setAdmissionIdentityScope('owner-1');
    const fetcher = vi.fn()
      .mockResolvedValueOnce(new Response('{"error":{"code":"connector_rate_limited","origin":"connector","family":"course_catalog"}}', { status: 429, headers: { 'Retry-After': '10' } }))
      .mockResolvedValueOnce(new Response('{"ok":true}', { status: 200 }))
      .mockResolvedValueOnce(new Response('{"ok":true}', { status: 200 }));
    const client = createAppClient(fetcher, 20_000);

    await expect(client.get('/api/courses', { admissionFamily: 'course_catalog' })).rejects.toMatchObject({ status: 429 });
    await expect(client.get('/api/courses/alias/1', { admissionFamily: 'course_details' })).resolves.toEqual({ ok: true });
    setAdmissionIdentityScope('owner-2');
    await expect(client.get('/api/courses', { admissionFamily: 'course_catalog' })).resolves.toEqual({ ok: true });
    expect(fetcher).toHaveBeenCalledTimes(3);
  });

  it('does not apply a late 429 from the previous owner to the new owner', async () => {
    setAdmissionIdentityScope('owner-1');
    let resolveResponse!: (response: Response) => void;
    const fetcher = vi.fn(() => new Promise<Response>((resolve) => { resolveResponse = resolve; }));
    const client = createAppClient(fetcher);
    const pending = client.get('/api/courses', { admissionFamily: 'course_catalog' });
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));

    setAdmissionIdentityScope('owner-2');
    resolveResponse(new Response('{"error":{"code":"connector_rate_limited","origin":"connector","family":"course_catalog"}}', {
      status: 429,
      headers: { 'Retry-After': '10' },
    }));

    await expect(pending).rejects.toMatchObject({ status: 429, family: 'course_catalog' });
    expect(getAdmissionCooldownRemainingMs('course_catalog')).toBe(0);
  });

  it('cancels a cooldown wait without sending the request', async () => {
    vi.useFakeTimers();
    const fetcher = vi.fn().mockResolvedValueOnce(new Response('{"error":{"code":"connector_rate_limited","origin":"connector","family":"course_catalog"}}', { status: 429, headers: { 'Retry-After': '10' } }));
    const client = createAppClient(fetcher, 20_000);
    await expect(client.get('/api/courses', { admissionFamily: 'course_catalog' })).rejects.toMatchObject({ status: 429 });
    const controller = new AbortController();
    const waiting = client.get('/api/courses?page=2', { admissionFamily: 'course_catalog', signal: controller.signal });

    controller.abort();
    await expect(waiting).rejects.toMatchObject({ status: 499 });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});

describe('query retry policy', () => {
  it('retries only bounded connector 429s and transient network/server errors', () => {
    const local429 = new AppHttpError(429, 'limited', undefined, undefined, Date.now() + 2_000, 'connector', 'course_catalog');
    const long429 = new AppHttpError(429, 'limited', undefined, undefined, Date.now() + 60_000, 'connector', 'course_catalog');
    const previewBusy = new AppHttpError(429, 'busy', undefined, { error: { code: 'preview_busy' } }, Date.now() + 1_000, 'preview');

    expect(shouldRetryAppQuery(0, local429)).toBe(true);
    expect(shouldRetryAppQuery(0, long429)).toBe(false);
    expect(shouldRetryAppQuery(0, previewBusy)).toBe(false);
    expect(shouldRetryAppQuery(0, new AppHttpError(403, 'forbidden'))).toBe(false);
    expect(shouldRetryAppQuery(0, new AppHttpError(500, 'server error'))).toBe(true);
    expect(shouldRetryAppQuery(0, new TypeError('network error'))).toBe(true);
    expect(shouldRetryAppQuery(1, new TypeError('network error'))).toBe(false);
  });
});
