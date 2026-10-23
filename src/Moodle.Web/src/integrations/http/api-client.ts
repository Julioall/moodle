export type AppErrorBody = { error?: { code?: string; message?: string; origin?: AppErrorOrigin; family?: string }; message?: string };

export type AppErrorOrigin = 'connector' | 'preview' | 'moodle' | 'unknown';

export class AppHttpError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly correlationId?: string,
    public readonly body?: AppErrorBody,
    public readonly retryAtEpochMs?: number,
    public readonly origin: AppErrorOrigin = 'unknown',
    public readonly family?: string,
  ) {
    super(message); this.name = 'AppHttpError';
  }

  get retryAfterMs() {
    return this.retryAtEpochMs === undefined ? undefined : Math.max(0, this.retryAtEpochMs - Date.now());
  }
}

export type AppRequestOptions = RequestInit & { admissionFamily?: string };
export type AppClient = { get<T>(path: string, options?: AppRequestOptions): Promise<T>; request<T>(path: string, options?: AppRequestOptions): Promise<T> };

const MAX_TRACKED_COOLDOWNS = 128;
const MAX_AUTOMATIC_RETRY_AFTER_MS = 30_000;
const admissionCooldowns = new Map<string, number>();
let activeAdmissionIdentity = 'anonymous';

export function parseRetryAfter(value: string | null, now = Date.now(), serverDate?: string | null): number | undefined {
  if (!value?.trim()) return undefined;
  const normalized = value.trim();
  if (/^\d+$/.test(normalized)) {
    const delay = Number(normalized) * 1_000;
    return Number.isFinite(delay) ? delay : undefined;
  }

  if (!/^(Mon|Tue|Wed|Thu|Fri|Sat|Sun), \d{2} (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d{4} \d{2}:\d{2}:\d{2} GMT$/.test(normalized)) return undefined;
  const retryDate = Date.parse(normalized);
  if (!Number.isFinite(retryDate)) return undefined;
  const serverTime = serverDate ? Date.parse(serverDate) : Number.NaN;
  const referenceTime = Number.isFinite(serverTime) ? serverTime : now;
  return Math.max(0, retryDate - referenceTime);
}

export function setAdmissionIdentityScope(ownerId?: string) {
  const next = ownerId?.trim() || 'anonymous';
  if (next === activeAdmissionIdentity) return;
  activeAdmissionIdentity = next;
  admissionCooldowns.clear();
}

export function clearAdmissionCooldowns() {
  admissionCooldowns.clear();
}

export function getAdmissionCooldownRemainingMs(family: string, now = Date.now()) {
  const retryAt = admissionCooldowns.get(family);
  if (retryAt === undefined) return 0;
  const remaining = retryAt - now;
  if (remaining <= 0) admissionCooldowns.delete(family);
  return Math.max(0, remaining);
}

export function shouldRetryAppQuery(failureCount: number, error: unknown) {
  if (failureCount >= 1) return false;
  if (!(error instanceof AppHttpError)) return error instanceof TypeError;
  if (error.status === 408 || error.status >= 500) return true;
  return error.status === 429 && error.origin === 'connector' && Boolean(error.family) &&
    error.retryAfterMs !== undefined && error.retryAfterMs <= MAX_AUTOMATIC_RETRY_AFTER_MS;
}

export function appQueryRetryDelay(attemptIndex: number, error: unknown) {
  if (error instanceof AppHttpError && error.status === 429 && error.origin === 'connector' && error.family) {
    return Math.max(error.retryAfterMs ?? 0, getAdmissionCooldownRemainingMs(error.family));
  }
  return Math.min(1_000 * (2 ** attemptIndex), 30_000);
}

export function retryAwarePollingInterval(error: unknown, normalIntervalMs: number) {
  if (error instanceof AppHttpError && error.status === 429 && error.origin === 'connector' && error.retryAfterMs !== undefined) {
    if (error.retryAfterMs > 2_147_483_647) return false;
    return Math.max(normalIntervalMs, error.retryAfterMs);
  }
  return normalIntervalMs;
}

function isAdmissionFamily(value: unknown): value is string {
  return typeof value === 'string' && /^[a-z0-9_]{1,64}$/.test(value);
}

function errorOrigin(body: AppErrorBody | undefined): AppErrorOrigin {
  const origin = body?.error?.origin;
  if (origin === 'connector' || origin === 'preview' || origin === 'moodle' || origin === 'unknown') return origin;
  switch (body?.error?.code) {
    case 'connector_rate_limited': return 'connector';
    case 'preview_busy': return 'preview';
    case 'moodle_rate_limited': return 'moodle';
    default: return 'unknown';
  }
}

function recordAdmissionCooldown(family: string, retryAtEpochMs: number) {
  const current = admissionCooldowns.get(family) ?? 0;
  admissionCooldowns.delete(family);
  admissionCooldowns.set(family, Math.max(current, retryAtEpochMs));
  while (admissionCooldowns.size > MAX_TRACKED_COOLDOWNS) {
    const oldest = admissionCooldowns.keys().next().value as string | undefined;
    if (oldest === undefined) break;
    admissionCooldowns.delete(oldest);
  }
}

function abortableDelay(delayMs: number, signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException('Request was cancelled', 'AbortError'));
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', abort);
      resolve();
    }, delayMs);
    const abort = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      reject(new DOMException('Request was cancelled', 'AbortError'));
    };
    signal.addEventListener('abort', abort, { once: true });
  });
}

async function waitForAdmissionCooldown(family: string, signal: AbortSignal, timeoutMs: number, correlationId: string) {
  const identity = activeAdmissionIdentity;
  const deadline = Date.now() + timeoutMs;
  while (true) {
    if (signal.aborted) throw new DOMException('Request was cancelled', 'AbortError');
    if (identity !== activeAdmissionIdentity) throw new AppHttpError(499, 'App request was cancelled', correlationId);
    const retryAfterMs = getAdmissionCooldownRemainingMs(family);
    if (retryAfterMs === 0) return;
    if (Date.now() + retryAfterMs > deadline) {
      const retryAt = Date.now() + retryAfterMs;
      throw new AppHttpError(429, 'Muitas solicitações em pouco tempo. Aguarde e tente novamente.', correlationId, {
        error: { code: 'connector_rate_limited', origin: 'connector', family }
      }, retryAt, 'connector', family);
    }
    await abortableDelay(retryAfterMs, signal);
  }
}

function repairMojibake(value: unknown): unknown {
  if (typeof value === 'string' && /[ÃÂâ][\u0080-\u00bfÃÂâ]/.test(value)) {
    try {
      const bytes = Uint8Array.from(value, (character) => character.charCodeAt(0));
      return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    } catch { return value; }
  }
  if (Array.isArray(value)) return value.map(repairMojibake);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, repairMojibake(item)]));
  return value;
}

const mutatingMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const createCorrelationId = () => globalThis.crypto?.randomUUID?.() ?? `app-${Date.now()}-${Math.random().toString(16).slice(2)}`;

export function readCsrfToken(): string | undefined {
  const meta = typeof document !== 'undefined' ? document.querySelector<HTMLMetaElement>('meta[name="csrf-token"]')?.content : undefined;
  if (meta) return meta;
  const cookie = typeof document !== 'undefined' ? document.cookie.split('; ').find((item) => item.startsWith('XSRF-TOKEN=')) : undefined;
  return cookie ? decodeURIComponent(cookie.slice('XSRF-TOKEN='.length)) : undefined;
}

// Moodle reads can legitimately take longer than a browser-only API call,
// especially when a course catalogue is being assembled from remote data.
// Keep this aligned with MoodleApi:HttpTimeoutSeconds (30s by default).
export function createAppClient(fetchImpl: typeof fetch = fetch, timeoutMs = 30000): AppClient {
  let csrfToken: string | undefined;
  let csrfRequest: Promise<string> | undefined;

  async function getCsrfToken() {
    if (csrfToken) return csrfToken;
    if (!csrfRequest) {
      csrfRequest = (async () => {
        const csrfResponse = await fetchImpl('/api/csrf', { method: 'GET', credentials: 'same-origin', cache: 'no-store' });
        const csrfBody = await csrfResponse.json() as { token?: string };
        const token = csrfBody.token ?? readCsrfToken();
        if (!csrfResponse.ok || !token) throw new AppHttpError(csrfResponse.status || 400, 'CSRF token is required for this operation');
        csrfToken = token;
        return token;
      })().finally(() => { csrfRequest = undefined; });
    }
    return csrfRequest;
  }

  return {
    get: <T>(path: string, options?: AppRequestOptions) => request<T>(path, { ...options, method: 'GET' }),
    request: <T>(path: string, options: AppRequestOptions = {}) => request<T>(path, options),
  };

  async function request<T>(path: string, options: AppRequestOptions = {}): Promise<T> {
    const { admissionFamily: requestedFamily, ...fetchOptions } = options;
    const controller = new AbortController();
    const externalSignal = options.signal;
    const abortExternal = () => controller.abort(externalSignal?.reason);
    externalSignal?.addEventListener('abort', abortExternal, { once: true });
    const timer = setTimeout(() => controller.abort('timeout'), timeoutMs);
    const method = (options.method ?? 'GET').toUpperCase();
    const correlationId = createCorrelationId();
    const requestIdentity = activeAdmissionIdentity;
    const headers = new Headers(options.headers);
    headers.set('Accept', 'application/json');
    headers.set('X-Correlation-ID', correlationId);
    if (mutatingMethods.has(method)) {
      try { headers.set('X-CSRF-TOKEN', await getCsrfToken()); }
      catch (error) {
        if (error instanceof AppHttpError) throw new AppHttpError(error.status, error.message, correlationId, error.body);
        throw error;
      }
    }
    try {
      const admissionFamily = isAdmissionFamily(requestedFamily) ? requestedFamily : undefined;
      if (admissionFamily && (method === 'GET' || method === 'HEAD')) {
        await waitForAdmissionCooldown(admissionFamily, controller.signal, timeoutMs, correlationId);
      }
      const response = await fetchImpl(path, { ...fetchOptions, method, credentials: 'same-origin', signal: controller.signal, headers });
      const responseCorrelationId = response.headers.get('X-Correlation-ID') ?? correlationId;
      const text = await response.text();
      let body: AppErrorBody | undefined;
      if (text) { try { body = repairMojibake(JSON.parse(text)) as AppErrorBody; } catch { body = undefined; } }
      const errorMessage = typeof (body as { error?: unknown } | undefined)?.error === 'string'
        ? (body as { error: string }).error
        : body?.error?.message;
      if (!response.ok) {
        const retryAfterMs = parseRetryAfter(response.headers.get('Retry-After'), Date.now(), response.headers.get('Date'));
        const retryAtEpochMs = retryAfterMs === undefined ? undefined : Date.now() + retryAfterMs;
        const origin = errorOrigin(body);
        const family = isAdmissionFamily(body?.error?.family) ? body.error.family : admissionFamily;
        if (response.status === 429 && origin === 'connector' && family && retryAtEpochMs !== undefined && requestIdentity === activeAdmissionIdentity) {
          recordAdmissionCooldown(family, retryAtEpochMs);
        }
        throw new AppHttpError(
          response.status,
          errorMessage ?? body?.message ?? `App request failed (${response.status})`,
          responseCorrelationId,
          body,
          retryAtEpochMs,
          origin,
          family,
        );
      }
      return (body ?? {}) as T;
    } catch (error) {
      if (error instanceof AppHttpError) throw error;
      if (error instanceof DOMException && error.name === 'AbortError' || controller.signal.aborted) {
        if (externalSignal?.aborted) throw new AppHttpError(499, 'App request was cancelled', correlationId);
        throw new AppHttpError(408, 'App request timed out', correlationId);
      }
      throw error;
    } finally { clearTimeout(timer); externalSignal?.removeEventListener('abort', abortExternal); }
  }
}
