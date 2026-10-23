// Browser-only synthetic fixture. Never use this server as proof of backend,
// PostgreSQL, Moodle permissions, budgets, XLSX generation or worker execution.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../dist');
const at = '2026-10-02T12:00:00Z';
let enabled = true;
const scope = { connectionId: '11111111-1111-4111-8111-111111111111', connectionRef: 'synthetic', alias: 'Moodle sintético', host: 'example.invalid', status: 'active', isDefault: true, capabilities: [] };
const catalog = [
  ['student.id', 'ID do estudante', 'Aluno', 'Text'], ['student.name', 'Nome do estudante', 'Aluno', 'Text'],
  ['course.id', 'ID do curso', 'Curso', 'Text'], ['course.name', 'Nome do curso / UC', 'Curso', 'Text'], ['course.category_path', 'Categoria do curso', 'Curso', 'Text'],
  ['enrollment.status', 'Estado da matrícula', 'Matrícula', 'Enum'],
  ['access.course_at', 'Último acesso ao curso', 'Acessos', 'DateTime'], ['access.platform_at', 'Último acesso à plataforma', 'Acessos', 'DateTime'], ['access.days_since_course', 'Dias desde o acesso ao curso', 'Acessos', 'Number'], ['access.never_course', 'Nunca acessou o curso', 'Acessos', 'Enum'],
  ['grade.value', 'Nota total', 'Notas', 'Number'],
].map(([fieldId, label, group, type]) => ({ fieldId, label, group, type, readPolicy: /^(access|grade|enrollment)\./.test(fieldId) ? 'live_required' : 'snapshot_preferred', requirements: [] }));
const courses = Array.from({ length: 112 }, (_, index) => ({ connectionRef: scope.connectionRef, courseId: String(index + 1), fullName: `UC sintética ${String(index + 1).padStart(3, '0')}`, categoryId: index < 110 ? 1 : 2, categoryName: index < 110 ? 'Root > Catálogo Alfa' : 'Root > Catálogo Beta', startDate: '2025-01-01T00:00:00Z', endDate: '2027-12-31T00:00:00Z', visible: true }));
const definition = { schemaVersion: 1, sourceId: 'student_course', columns: [{ fieldId: 'student.name' }, { fieldId: 'course.name' }, { fieldId: 'access.course_at' }, { fieldId: 'grade.value' }] };
const models = [{ id: '22222222-2222-4222-8222-222222222222', name: 'Modelo sintético', description: 'Campos de exemplo para verificar a interface.', definition, revision: 1, createdAt: at, updatedAt: at }];
const jobs = [{ id: '33333333-3333-4333-8333-333333333333', reportType: 'grades', scopeType: 'courses', connectionRef: scope.connectionRef, courseIds: ['1', '2'], courses: [{ name: 'Curso sintético 1' }, { name: 'Curso sintético 2' }], status: 'completed', progressPercent: 100, totalCourses: 2, processedCourses: 2, fileName: 'fixture.xlsx', contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', fileSizeBytes: 2048, requestedAt: at, updatedAt: at, downloadUrl: '/api/reports/jobs/33333333-3333-4333-8333-333333333333/download', selectionSnapshot: { schemaVersion: 1, connectionId: scope.connectionId, connectionRef: scope.connectionRef, courseIds: ['1', '2'] } }];
const counts = { previews: 0, generations: 0 };
function page(data, query) { const number = Number(query.get('page') ?? 1); const size = Number(query.get('pageSize') ?? 20); const part = data.slice((number - 1) * size, number * size); return { data: part, meta: { page: number, pageSize: size, returned: part.length, total: data.length, hasMore: number * size < data.length, complete: true, generatedAt: at } }; }
const server = createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost:5173');
  const send = (body, status = 200) => { response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(body)); };
  try {
    let body = '';
    for await (const chunk of request) body += chunk;
    const input = body ? JSON.parse(body) : undefined;
    const path = url.pathname;
    if (path === '/__fixture') { if (input) enabled = input.enabled; return send({ enabled, ...counts }); }
    if (path === '/api/session') return send({ data: { authenticated: true, reportTemplatesEnabled: enabled, user: { id: '44444444-4444-4444-8444-444444444444', name: 'Pessoa sintética', roles: [], permissions: ['reports.view', 'courses.view', 'schools.view', 'settings.view'] } }, meta: { generatedAt: at } });
    if (path === '/api/connections') return send({ data: [scope], meta: { generatedAt: at } });
    if (path === '/api/csrf') return send({ token: 'synthetic-fixture-only' });
    if (path === '/api/info') return send({ courseDetailsOverviewV1Enabled: true });
    if (path === '/api/schools') return send({ data: [{ path: 'Root > Catálogo Alfa', name: 'Catálogo Alfa', level: 1, courseCount: 110 }, { path: 'Root > Catálogo Beta', name: 'Catálogo Beta', level: 1, courseCount: 2 }], meta: { generatedAt: at, complete: true } });
    if (path === '/api/courses') return send(page(courses, url.searchParams));
    if (path === '/api/schools/courses') return send(page(courses.filter((course) => course.categoryName === url.searchParams.get('categoryPath')), url.searchParams));
    if (path === '/api/courses/search') return send(page(courses.filter((course) => course.fullName.toLowerCase().includes((url.searchParams.get('query') ?? '').toLowerCase())), url.searchParams));
    if (path.includes('ignored-courses') || path.includes('tracked-courses')) return send({ data: [], meta: { generatedAt: at } });
    if (path === '/api/reports/catalog') return enabled ? send({ schemaVersion: 1, sourceId: 'student_course', fields: catalog, limits: { maxColumns: 30, maxTemplatesPerOwner: 100, maxCourses: 500, maxRows: 50000 } }) : send({}, 404);
    if (path.startsWith('/api/reports/templates')) {
      if (!enabled) return send({}, 404);
      const id = path.split('/')[4]; const model = models.find((item) => item.id === id);
      if (request.method === 'GET') return id ? send(model ?? {}, model ? 200 : 404) : send({ items: models, page: 1, pageSize: 20, total: models.length });
      if (request.method === 'POST') { const created = { ...input, id: randomUUID(), revision: 1, createdAt: at, updatedAt: at }; models.push(created); return send(created, 201); }
      if (!model) return send({}, 404);
      if (Number(input?.expectedRevision ?? url.searchParams.get('expectedRevision')) !== model.revision) return send({ error: 'report_template_revision_conflict' }, 409);
      if (request.method === 'PUT') { Object.assign(model, input, { revision: model.revision + 1, updatedAt: at }); return send(model); }
      if (request.method === 'DELETE') { models.splice(models.indexOf(model), 1); return send(undefined, 204); }
    }
    if (path === '/api/reports/preview') {
      counts.previews++;
      const chosen = input.definition ?? models.find((model) => model.id === input.templateId)?.definition ?? { ...definition, columns: [{ fieldId: 'student.name' }, { fieldId: 'course.name' }, { fieldId: 'access.platform_at' }, { fieldId: 'grade.value' }] };
      const columns = chosen.columns.map((column) => ({ fieldId: column.fieldId, label: column.label ?? catalog.find((field) => field.fieldId === column.fieldId).label, type: catalog.find((field) => field.fieldId === column.fieldId).type }));
      const sampled = input.courseIds.slice(0, 2);
      const rows = sampled.map((courseId, index) => ({ courseId, studentId: String(7001 + index), cells: columns.map((column) => column.fieldId.startsWith('grade.') ? { value: null, state: 'Unavailable', reason: 'preview_read_budget' } : column.fieldId.startsWith('access.') ? { value: null, state: 'Missing' } : { value: column.fieldId === 'student.name' ? `Estudante sintético ${index + 1}` : courses.find((course) => course.courseId === courseId)?.fullName ?? courseId, state: 'Known' }) }));
      return send({ columns, rows, sampledCourseIds: sampled, requestedCourseCount: input.courseIds.length, returnedRows: rows.length, truncated: input.courseIds.length > 2, partial: true, coverage: [{ courseId: sampled[0], family: 'participants', fieldIds: ['student.name'], source: 'live', readAt: at, complete: true }, { courseId: sampled[0], family: 'grades', fieldIds: ['grade.value'], source: 'unavailable', readAt: at, complete: false, reason: 'preview_read_budget' }], warnings: ['synthetic_preview_limit'], generatedAt: at, referenceAt: at });
    }
    if (path === '/api/reports/jobs') {
      if (request.method === 'POST') { counts.generations++; const source = jobs.find((job) => job.id === input.refreshFromJobId); const model = models.find((item) => item.id === input.templateId); const courseIds = source?.selectionSnapshot?.courseIds ?? input.courseIds; const connectionRef = source?.selectionSnapshot?.connectionRef ?? input.connectionRef; const job = { id: randomUUID(), reportType: source?.reportType ?? input.reportType, scopeType: 'courses', connectionRef, courseIds, courses: source?.courses ?? courseIds.map((courseId) => ({ name: courses.find((course) => course.courseId === courseId)?.fullName ?? courseId })), status: 'queued', progressPercent: 0, totalCourses: courseIds.length, processedCourses: 0, fileSizeBytes: 0, requestedAt: at, updatedAt: at, templateNameSnapshot: model?.name ?? source?.templateNameSnapshot, templateRevisionSnapshot: model?.revision ?? source?.templateRevisionSnapshot, definitionSnapshot: model?.definition ?? source?.definitionSnapshot, selectionSnapshot: source?.selectionSnapshot ?? { schemaVersion: 1, connectionId: scope.connectionId, connectionRef, courseIds } }; jobs.unshift(job); return send({ id: job.id, status: job.status }, 202); }
      const filtered = jobs.filter((job) => (!url.searchParams.get('status') || job.status === url.searchParams.get('status')) && (!url.searchParams.get('search') || `${job.templateNameSnapshot ?? ''} ${job.fileName ?? ''}`.toLowerCase().includes(url.searchParams.get('search').toLowerCase())));
      const result = page(filtered, url.searchParams); Object.assign(result.meta, { storageUsedBytes: 2048, storageLimitBytes: 314572800, storageAvailableBytes: 314570752 }); return send(result);
    }
    if (path.startsWith('/api/reports/jobs/')) { const job = jobs.find((item) => item.id === path.split('/')[4]); if (!job) return send({}, 404); if (request.method === 'DELETE') { jobs.splice(jobs.indexOf(job), 1); return send(undefined, 204); } return send(job); }
    if (path.startsWith('/api/')) return send({ data: [], meta: { generatedAt: at } });
    const file = resolve(root, path.replace(/^\/+/, '') || 'index.html');
    if (file !== root && !file.startsWith(root + sep)) return send({}, 400);
    let bytes; try { bytes = await readFile(file); } catch { bytes = await readFile(resolve(root, 'index.html')); }
    response.writeHead(200, { 'Content-Type': ({ '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png' })[extname(file)] ?? 'text/html; charset=utf-8', 'Cache-Control': 'no-store' }); response.end(bytes);
  } catch { send({ error: 'synthetic_fixture_failure' }, 500); }
});
server.listen(5173, '127.0.0.1', () => console.log('Synthetic browser fixture ready at http://127.0.0.1:5173'));
