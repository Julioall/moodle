import { request } from 'node:http';
import { randomUUID } from 'node:crypto';
import { Buffer } from 'node:buffer';

const baseUrl = new URL(process.env.APP_API_SMOKE_URL ?? 'http://127.0.0.1:8787');
if (!['127.0.0.1', 'localhost', '::1'].includes(baseUrl.hostname)) {
  throw new Error('App API smoke only runs against a local host.');
}

const call = (method, path, body, cookie, headers = {}) => new Promise((resolve, reject) => {
  const payload = body ? JSON.stringify(body) : undefined;
  const req = request(new URL(path, baseUrl), {
    method,
    headers: {
      ...(payload ? { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) } : {}),
      ...(cookie ? { cookie } : {}),
      ...headers,
    },
  }, response => {
    let text = '';
    response.setEncoding('utf8');
    response.on('data', chunk => { text += chunk; });
    response.on('end', () => resolve({ status: response.statusCode ?? 0, body: text, cookie: response.headers['set-cookie']?.[0]?.split(';')[0] }));
  });
  req.on('error', reject);
  if (payload) req.write(payload);
  req.end();
});

const email = `app-smoke-${randomUUID()}@example.test`;
const password = 'AppLocalSmoke!2026';
const csrf = await call('GET', '/api/csrf');
const csrfToken = JSON.parse(csrf.body).token;
if (csrf.status !== 200 || !csrf.cookie || !csrfToken) throw new Error(`CSRF bootstrap failed: ${csrf.status} ${csrf.body}`);

const registered = await call(
  'POST',
  '/api/account/register',
  { name: 'App API Smoke', email, password },
  csrf.cookie,
  { 'x-csrf-token': csrfToken },
);
if (registered.status !== 200 || !registered.cookie) throw new Error(`Register failed: ${registered.status} ${registered.body}`);

const sessionCookie = `${csrf.cookie}; ${registered.cookie}`;
const session = await call('GET', '/api/session', undefined, sessionCookie);
const sessionBody = JSON.parse(session.body);
if (session.status !== 200 || sessionBody.data?.authenticated !== true) throw new Error(`Session failed: ${session.status} ${session.body}`);

const dashboard = await call('GET', '/api/dashboard', undefined, sessionCookie);
const dashboardBody = JSON.parse(dashboard.body);
if (dashboard.status !== 200 || !dashboardBody.data?.summary || !dashboardBody.meta?.generatedAt) {
  throw new Error(`Dashboard failed: ${dashboard.status} ${dashboard.body}`);
}

const refreshedCsrf = await call('GET', '/api/csrf', undefined, sessionCookie);
const refreshedCsrfToken = JSON.parse(refreshedCsrf.body).token;
const plannerCookie = refreshedCsrf.cookie ? `${sessionCookie}; ${refreshedCsrf.cookie}` : sessionCookie;
if (refreshedCsrf.status !== 200 || !refreshedCsrfToken) {
  throw new Error(`Planner CSRF bootstrap failed: ${refreshedCsrf.status} ${refreshedCsrf.body}`);
}

const now = new Date();
const task = await call('POST', '/api/tasks', {
  title: 'Task counter smoke',
  status: 'todo',
  priority: 'medium',
  dueAt: now.toISOString(),
}, plannerCookie, { 'x-csrf-token': refreshedCsrfToken });
if (task.status !== 201) throw new Error(`Task creation failed: ${task.status} ${task.body}`);

const event = await call('POST', '/api/agenda', {
  title: 'Event counter smoke',
  startAt: now.toISOString(),
  endAt: new Date(now.getTime() + 60 * 60 * 1000).toISOString(),
  type: 'manual',
}, plannerCookie, { 'x-csrf-token': refreshedCsrfToken });
if (event.status !== 201) throw new Error(`Event creation failed: ${event.status} ${event.body}`);

const summary = await call('GET', '/api/dashboard/summary', undefined, plannerCookie);
const summaryBody = JSON.parse(summary.body);
if (summary.status !== 200 || summaryBody.data?.summary?.todayTasks !== 1 || summaryBody.data?.summary?.todayEvents !== 1) {
  throw new Error(`Planner counters failed: ${summary.status} ${summary.body}`);
}

for (const path of ['/api/connections', '/api/courses', '/api/pending']) {
  const response = await call('GET', path, undefined, sessionCookie);
  const body = JSON.parse(response.body);
  if (response.status !== 200 || !Array.isArray(body.data) || !body.meta?.generatedAt) {
    throw new Error(`${path} failed: ${response.status} ${response.body}`);
  }
  console.log(`PASS ${path} ${response.status}`);
}

const schools = await call('GET', '/api/schools', undefined, sessionCookie);
const schoolsBody = JSON.parse(schools.body);
if (schools.status !== 200 || !Array.isArray(schoolsBody.data) || !schoolsBody.meta?.generatedAt) {
  throw new Error(`/api/schools without Moodle connection failed: ${schools.status} ${schools.body}`);
}
console.log(`PASS /api/schools without Moodle connection ${schools.status}`);

const conversations = await call('GET', '/api/messages/conversations', undefined, sessionCookie);
const conversationsBody = JSON.parse(conversations.body);
if (conversations.status !== 200 || !Array.isArray(conversationsBody.data?.items) || !conversationsBody.meta?.generatedAt) {
  throw new Error(`/api/messages/conversations without Moodle connection failed: ${conversations.status} ${conversations.body}`);
}
console.log(`PASS /api/messages/conversations without Moodle connection ${conversations.status}`);

const courses = await call('GET', '/api/courses', undefined, sessionCookie);
const coursesBody = JSON.parse(courses.body);
if (coursesBody.data?.[0]) {
  const course = coursesBody.data[0];
  const students = await call(
    'GET',
    `/api/courses/${encodeURIComponent(course.connectionRef)}/${encodeURIComponent(course.courseId)}/students`,
    undefined,
    sessionCookie,
  );
  const studentsBody = JSON.parse(students.body);
  if (students.status !== 200 || !Array.isArray(studentsBody.data) || !studentsBody.meta?.generatedAt) {
    throw new Error(`/api/courses/{connectionRef}/{courseId}/students failed: ${students.status} ${students.body}`);
  }
  console.log(`PASS scoped students ${students.status}`);
} else {
  console.log('PASS scoped students skipped (local account has no Moodle course)');
}

console.log(`PASS register ${registered.status}`);
console.log(`PASS session ${session.status}`);
console.log(`PASS dashboard ${dashboard.status}`);
console.log('PASS planner counters without Moodle connection');
