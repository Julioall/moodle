import { request } from 'node:http';
import { randomUUID } from 'node:crypto';
import { Buffer } from 'node:buffer';

const baseUrl = new URL(process.env.APP_API_SMOKE_URL ?? 'http://127.0.0.1:8787');
if (!['127.0.0.1', 'localhost', '::1'].includes(baseUrl.hostname)) {
  throw new Error('App E2E smoke only runs against a local host.');
}

let cookie = '';

const call = (method, path, body, headers = {}) => new Promise((resolve, reject) => {
  const payload = body === undefined ? undefined : JSON.stringify(body);
  const requestHeaders = {
    accept: 'application/json',
    ...(payload ? { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) } : {}),
    ...(cookie ? { cookie } : {}),
    ...headers,
  };
  const req = request(new URL(path, baseUrl), { method, headers: requestHeaders }, response => {
    const chunks = [];
    response.on('data', chunk => { chunks.push(chunk); });
    response.on('end', () => {
      const bytes = Buffer.concat(chunks);
      const text = bytes.toString('utf8');
      const setCookies = response.headers['set-cookie'] ?? [];
      if (setCookies.length > 0) {
        const nextCookies = setCookies.map(value => value.split(';', 1)[0]);
        const existing = cookie ? cookie.split('; ').filter(Boolean) : [];
        const merged = new Map(existing.map(value => value.split('=', 1)[0]).map((key, index) => [key, existing[index]]));
        nextCookies.forEach(value => merged.set(value.split('=', 1)[0], value));
        cookie = [...merged.values()].join('; ');
      }
      let json;
      if (text) {
        try { json = JSON.parse(text); } catch { json = undefined; }
      }
      resolve({ status: response.statusCode ?? 0, text, json, bytes });
    });
  });
  req.on('error', reject);
  if (payload) req.write(payload);
  req.end();
});

const expectStatus = (response, expected, label) => {
  if (response.status !== expected) {
    throw new Error(`${label} failed: HTTP ${response.status}`);
  }
  console.log(`PASS ${label} ${response.status}`);
};

const expectJson = (response, expected, label) => {
  expectStatus(response, expected, label);
  if (!response.json) throw new Error(`${label} did not return JSON.`);
  return response.json;
};

const waitFor = async (label, path, predicate, timeoutMs = 15_000) => {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await call('GET', path);
    if (last.status === 200 && last.json && predicate(last.json)) {
      console.log(`PASS ${label} 200`);
      return last.json;
    }
    await new Promise(resolve => globalThis.setTimeout(resolve, 250));
  }
  throw new Error(`${label} did not become ready: ${last?.status} ${last?.text}`);
};

if (process.env.APP_E2E_REPORTS_ONLY === '1') {
  const loginCsrf = expectJson(await call('GET', '/api/csrf'), 200, 'report login CSRF');
  expectStatus(await call('POST', '/api/account/login', {
    email: process.env.APP_E2E_REPORTS_EMAIL,
    password: process.env.APP_E2E_REPORTS_PASSWORD,
  }, { 'x-csrf-token': loginCsrf.token }), 200, 'report login');
  const csrf = expectJson(await call('GET', '/api/csrf'), 200, 'report CSRF');
  expectJson(await call('GET', '/api/reports/catalog'), 200, 'report catalog');
  expectJson(await call('GET', '/api/reports/templates'), 200, 'private templates');
  const input = {
    templateId: process.env.APP_E2E_REPORTS_TEMPLATE_ID,
    expectedRevision: 2,
    connectionRef: 'synthetic',
    courseIds: ['1'],
  };
  expectStatus(await call('POST', '/api/reports/preview', input), 400, 'preview rejects missing CSRF');
  const preview = expectJson(await call('POST', '/api/reports/preview', input,
    { 'x-csrf-token': csrf.token }), 200, 'bounded live preview');
  if (!preview.rows?.length || preview.rows.length > 20) throw new Error('Invalid preview row budget.');
  expectJson(await call('GET', '/api/reports/jobs'), 200, 'private report history');
  const artifact = await call('GET', `/api/reports/jobs/${process.env.APP_E2E_REPORTS_JOB_ID}/download`);
  expectStatus(artifact, 200, 'private XLSX download');
  if (artifact.bytes.length < 100 || artifact.bytes.readUInt32LE(0) !== 0x04034b50) {
    throw new Error('Download is not a binary XLSX container.');
  }
  console.log('Report HTTP smoke passed (login → catalog → templates → CSRF → live preview → history → binary download).');
  process.exit(0);
}

const email = `app-e2e-${randomUUID()}@example.test`;
const password = 'AppE2EFlow!2026';
const moodleAlias = process.env.APP_E2E_MOODLE_ALIAS ?? 'local-e2e';
const moodleBaseUrl = process.env.APP_E2E_MOODLE_URL ?? 'https://moodle.local';
const moodleUsername = process.env.APP_E2E_MOODLE_USERNAME ?? 'demo';
const moodlePassword = process.env.APP_E2E_MOODLE_PASSWORD ?? 'demo-password';

const registrationCsrf = expectJson(await call('GET', '/api/csrf'), 200, 'registration CSRF');
if (!registrationCsrf.token) throw new Error('registration CSRF token was not issued.');
expectJson(
  await call('POST', '/api/account/register', { name: 'App E2E Smoke', email, password }, { 'x-csrf-token': registrationCsrf.token }),
  200,
  'register',
);
const session = expectJson(await call('GET', '/api/session'), 200, 'session');
if (session.data?.authenticated !== true) throw new Error('session is not authenticated.');

const csrf = expectJson(await call('GET', '/api/csrf'), 200, 'csrf');
if (!csrf.token) throw new Error('CSRF token was not issued.');

const account = expectJson(await call('GET', '/api/account/me'), 200, 'account');
const permissionPayload = {
  name: `E2E ${randomUUID()}`,
  description: 'Permissões temporárias para o smoke test ponta a ponta.',
  permissions: ['connections.manage', 'courses.view', 'students.view', 'dashboard.view', 'grading.view', 'grading.manage'],
};
const missingCsrf = await call('POST', '/api/permission-groups', permissionPayload);
expectStatus(missingCsrf, 400, 'reject missing CSRF');
if (missingCsrf.json?.error?.code !== 'csrf_invalid') {
  throw new Error(`missing CSRF contract is invalid: ${missingCsrf.text}`);
}

const permissionGroup = expectJson(await call('POST', '/api/permission-groups', permissionPayload, { 'x-csrf-token': csrf.token }), 200, 'create E2E permissions');
const groupId = permissionGroup.group?.id;
if (!groupId || !account.id) throw new Error('E2E permission group was not created.');
expectJson(await call('POST', `/api/permission-groups/${encodeURIComponent(groupId)}/members`, { userId: account.id }, { 'x-csrf-token': csrf.token }), 200, 'assign E2E permissions');
const loginCsrf = expectJson(await call('GET', '/api/csrf'), 200, 'login CSRF');
expectJson(await call('POST', '/api/account/login', { email, password }, { 'x-csrf-token': loginCsrf.token }), 200, 'refresh session permissions');
const connectionCsrf = expectJson(await call('GET', '/api/csrf'), 200, 'connection CSRF');

const connectionPayload = {
  moodleAlias,
  moodleBaseUrl,
  moodleUsername,
  moodlePassword,
  isDefault: true,
  canWrite: false,
};
const connection = expectJson(await call('POST', '/api/connections', connectionPayload, { 'x-csrf-token': connectionCsrf.token }), 200, 'connect Moodle');
if (!connection.connectionRef || !connection.alias || connection.status !== 'active' || connection.apiKey || connection.password || connection.token) {
  throw new Error(`connection contract is invalid or contains a secret: ${JSON.stringify(connection)}`);
}

const connections = expectJson(await call('GET', '/api/connections'), 200, 'connections');
const connectionRef = connections.data?.[0]?.connectionRef;
if (!connectionRef) throw new Error('connectionRef was not returned.');

const courses = expectJson(await call('GET', `/api/courses?connectionRef=${encodeURIComponent(connectionRef)}`), 200, 'courses');
const course = courses.data?.[0];
if (!course?.courseId) throw new Error('stub course was not returned.');

const coursePath = `/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(course.courseId)}`;
expectJson(await call('GET', coursePath), 200, 'course detail');
const submissionsPath = `/api/submissions?connectionRef=${encodeURIComponent(connectionRef)}&courseId=${encodeURIComponent(course.courseId)}&assignmentId=5001&status=awaiting_grading&page=1&pageSize=25`;
const preparingSubmissions = expectJson(await call('GET', submissionsPath), 200, 'queue submissions snapshot');
if (preparingSubmissions.meta?.source !== 'background' && preparingSubmissions.meta?.source !== 'snapshot') throw new Error('submission preparation metadata is invalid.');
const submissions = await waitFor('submissions snapshot', submissionsPath, payload => payload.meta?.source === 'snapshot' && payload.meta?.complete === true);
if (submissions.data?.total !== 1 || submissions.data?.submissions?.[0]?.needsGrading !== true || submissions.data.submissions[0]?.fileCount !== 1) throw new Error('submission snapshot contract is invalid.');
const submission = submissions.data.submissions[0];
const detail = expectJson(await call('GET', `/api/submissions/${encodeURIComponent(course.courseId)}/5001/${encodeURIComponent(submission.userId)}?connectionRef=${encodeURIComponent(connectionRef)}`), 200, 'submission detail');
if (detail.meta?.source !== 'snapshot' || detail.data?.files?.length !== 1) throw new Error('submission detail must use the local snapshot and preserve files.');

const activities = await waitFor('activities snapshot', `${coursePath}/activities`, payload => payload.meta?.source === 'snapshot' && payload.data?.length === 3);
if (activities.data?.length !== 3 || activities.meta?.total !== 3) throw new Error('activity pagination contract is invalid.');

const students = await waitFor('students snapshot', `${coursePath}/students`, payload => payload.meta?.source === 'snapshot' && payload.data?.length === 3);
const student = students.data?.[0];
if (!student?.studentId || students.data.length !== 3) throw new Error('stub students were not returned.');
expectJson(await call('GET', `${coursePath}/students/${encodeURIComponent(student.studentId)}`), 200, 'student profile');

const pendingPath = `/api/pending?connectionRef=${encodeURIComponent(connectionRef)}&courseId=${encodeURIComponent(course.courseId)}&periodDays=30`;
const pending = await waitFor('pending snapshot', pendingPath, payload => payload.meta?.source === 'snapshot' && payload.meta?.complete === true);
if (pending.meta?.total !== 2 || pending.data?.some(item => item.type !== 'pending_submission')) throw new Error('pending contract is invalid.');

const dashboard = await waitFor('dashboard', `/api/dashboard?connectionRef=${encodeURIComponent(connectionRef)}&courseId=${encodeURIComponent(course.courseId)}`, payload => payload.data?.summary?.activeCourses === 1 && payload.data?.summary?.pendingDeliveries === 2);
if (dashboard.data?.summary?.activeCourses !== 1 || dashboard.data?.summary?.pendingDeliveries !== 2) throw new Error('dashboard stub indicators are invalid.');

console.log('App E2E smoke passed (login → Moodle → snapshots → activities → submissions → correction detail → pending → dashboard)');
