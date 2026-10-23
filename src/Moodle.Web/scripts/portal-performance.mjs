import { randomUUID } from 'node:crypto';

const baseUrl = new URL(process.env.PORTAL_PERF_URL ?? 'http://127.0.0.1:8787');
if (!['127.0.0.1', 'localhost', '::1'].includes(baseUrl.hostname)) {
  throw new Error('Portal performance test only runs against a local host.');
}

const cookieJar = new Map();
const rememberCookies = (response) => {
  const raw = response.headers.get('set-cookie');
  if (!raw) return;
  for (const item of raw.split(/,(?=[^;]+=[^;]+)/)) {
    const [pair] = item.split(';', 1);
    const [name] = pair.split('=', 1);
    if (name) cookieJar.set(name, pair);
  }
};
const cookieHeader = () => [...cookieJar.values()].join('; ');

const call = async (method, path, body, csrf = false) => {
  const headers = { accept: 'application/json' };
  const cookie = cookieHeader();
  if (cookie) headers.cookie = cookie;
  if (body !== undefined) {
    headers['content-type'] = 'application/json';
  }
  if (csrf) {
    const csrfResponse = await fetch(new URL('/api/csrf', baseUrl), { headers: { cookie: cookieHeader() } });
    rememberCookies(csrfResponse);
    const csrfBody = await csrfResponse.json();
    headers['x-csrf-token'] = csrfBody.token;
    headers.cookie = cookieHeader();
  }
  const started = performance.now();
  const response = await fetch(new URL(path, baseUrl), {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const elapsedMs = performance.now() - started;
  rememberCookies(response);
  const text = await response.text();
  let json;
  try { json = text ? JSON.parse(text) : undefined; } catch { /* status is enough for this probe */ }
  return { status: response.status, elapsedMs, json, text };
};

const waitFor = async (label, path, predicate, timeoutMs = 15_000) => {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await call('GET', path);
    if (last.status === 200 && last.json && predicate(last.json)) return last;
    await new Promise(resolve => globalThis.setTimeout(resolve, 250));
  }
  throw new Error(`${label} did not become ready: ${last?.status} ${last?.text}`);
};

const email = `portal-perf-${randomUUID()}@example.test`;
const password = 'PortalPerfLocal!2026';
const registered = await call('POST', '/api/account/register', { name: 'Portal performance', email, password }, true);
if (registered.status !== 200) throw new Error(`register failed: ${registered.status} ${registered.text}`);

const account = await call('GET', '/api/account/me');
const permissionGroup = await call('POST', '/api/permission-groups', {
  name: `Portal perf ${randomUUID()}`,
  description: 'Permissões temporárias de medição local.',
  permissions: ['connections.manage', 'courses.view', 'students.view', 'dashboard.view', 'grading.view'],
}, true);
if (permissionGroup.status !== 200 || !permissionGroup.json?.group?.id) {
  throw new Error(`permission group failed: ${permissionGroup.status} ${permissionGroup.text}`);
}
const membership = await call('POST', `/api/permission-groups/${encodeURIComponent(permissionGroup.json.group.id)}/members`, { userId: account.json.id }, true);
if (membership.status !== 200) throw new Error(`permission membership failed: ${membership.status} ${membership.text}`);
const login = await call('POST', '/api/account/login', { email, password }, true);
if (login.status !== 200) throw new Error(`login failed: ${login.status} ${login.text}`);

const connection = await call('POST', '/api/connections', {
  moodleAlias: `perf-${randomUUID().slice(0, 8)}`,
  moodleBaseUrl: 'https://moodle.local',
  moodleUsername: 'demo',
  moodlePassword: 'demo-password',
  isDefault: true,
  canWrite: false,
}, true);
if (connection.status !== 200 || !connection.json?.connectionRef) {
  throw new Error(`connection failed: ${connection.status} ${connection.text}`);
}
const connectionRef = connection.json.connectionRef;
const courseId = '101';
const coursePath = `/api/courses/${encodeURIComponent(connectionRef)}/${courseId}`;
const activitiesPath = `${coursePath}/activities`;
const pendingPath = `/api/pending?connectionRef=${encodeURIComponent(connectionRef)}&courseId=${courseId}`;
const submissionsPath = `/api/submissions?connectionRef=${encodeURIComponent(connectionRef)}&courseId=${courseId}&assignmentId=5001&status=awaiting_grading&page=1&pageSize=25`;

await waitFor('courses snapshot', `/api/courses?connectionRef=${encodeURIComponent(connectionRef)}`, payload => payload.meta?.source === 'snapshot');
const coldActivities = await call('GET', activitiesPath);
const coldPending = await call('GET', pendingPath);
const coldSubmissions = await call('GET', submissionsPath);
await waitFor('activities snapshot', activitiesPath, payload => payload.meta?.source === 'snapshot' && payload.meta?.complete === true);
await waitFor('submissions snapshot', submissionsPath, payload => payload.meta?.source === 'snapshot' && payload.meta?.complete === true);
await waitFor('pending snapshot', pendingPath, payload => payload.meta?.source === 'snapshot' && payload.meta?.complete === true);

console.log(JSON.stringify({
  endpoint: 'corrections-preparation',
  initialResponses: {
    activitiesMs: Number(coldActivities.elapsedMs.toFixed(1)),
    pendingMs: Number(coldPending.elapsedMs.toFixed(1)),
    submissionsMs: Number(coldSubmissions.elapsedMs.toFixed(1)),
  },
  statuses: [coldActivities.status, coldPending.status, coldSubmissions.status],
}));

const endpoints = [
  ['courses', `/api/courses?connectionRef=${encodeURIComponent(connectionRef)}`],
  ['activities', activitiesPath],
  ['pending', pendingPath],
  ['submissions', submissionsPath],
  ['dashboard-summary', `/api/dashboard/summary?connectionRef=${encodeURIComponent(connectionRef)}`],
  ['dashboard-access', `/api/dashboard/access?connectionRef=${encodeURIComponent(connectionRef)}`],
  ['dashboard-pending', `/api/dashboard/pending?connectionRef=${encodeURIComponent(connectionRef)}`],
  ['dashboard-overview', `/api/dashboard?connectionRef=${encodeURIComponent(connectionRef)}`],
  ['course-overview', `/api/dashboard?connectionRef=${encodeURIComponent(connectionRef)}&courseId=101`],
];

const percentile = (values, p) => values[Math.min(values.length - 1, Math.floor(values.length * p))];
for (const [label, path] of endpoints) {
  const cold = await call('GET', path);
  const samples = [];
  for (let index = 0; index < 5; index += 1) samples.push((await call('GET', path)).elapsedMs);
  samples.sort((a, b) => a - b);
  console.log(JSON.stringify({
    endpoint: label,
    coldMs: Number(cold.elapsedMs.toFixed(1)),
    status: cold.status,
    warmMs: {
      min: Number(samples[0].toFixed(1)),
      median: Number(percentile(samples, 0.5).toFixed(1)),
      p95: Number(percentile(samples, 0.95).toFixed(1)),
      max: Number(samples.at(-1).toFixed(1)),
    },
    snapshotCount: label === 'dashboard-access' ? cold.json?.data?.snapshots?.length ?? 0 : undefined,
  }));
}
