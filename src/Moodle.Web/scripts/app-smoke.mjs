import { request } from 'node:http';

const baseUrl = process.env.APP_SMOKE_URL ?? 'http://127.0.0.1:4173';
const paths = [
  '/',
  '/meus-cursos',
  '/cursos/demo/1',
  '/cursos/demo/1?tab=students',
  '/pendencias',
  '/tarefas',
  '/agenda',
  '/mensagens',
  '/foruns',
  '/followup',
  '/relatorios',
  '/configuracoes',
  '/conexoes',
  '/rota-inexistente',
];

const get = (url) => new Promise((resolve, reject) => {
  const req = request(url, response => {
    let body = '';
    response.setEncoding('utf8');
    response.on('data', chunk => { body += chunk; });
    response.on('end', () => resolve({ status: response.statusCode ?? 0, body, headers: response.headers }));
  });
  req.on('error', reject);
  req.end();
});

for (const path of paths) {
  const response = await get(`${baseUrl}${path}`);
  if (response.status < 200 || response.status >= 300 || !response.body.includes('id="root"')) {
    throw new Error(`App smoke failed for ${path}: status=${response.status}`);
  }
  console.log(`PASS ${path} ${response.status}`);
}

for (const path of ['/app.html', '/auth.html']) {
  const response = await get(`${baseUrl}${path}`);
  const isSpaFallback = response.status >= 200 && response.status < 300 && response.body.includes('id="root"');
  const isRedirect = response.status >= 300 && response.status < 400 && response.headers?.location === '/';
  if (!isSpaFallback && !isRedirect) {
    throw new Error(`Legacy compatibility check failed for ${path}: status=${response.status}`);
  }
  console.log(`PASS compatibility ${path} ${response.status}`);
}

console.log(`App smoke passed (${paths.length} SPA routes + 2 compatibility paths)`);
