// Local browser bridge: static production UI + actual Kestrel API, no API mocks.
import { createServer, request as upstreamRequest } from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const backend = new URL(process.env.APP_E2E_BACKEND_URL ?? 'http://127.0.0.1:0');
if (backend.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(backend.hostname) || backend.port === '0') {
  throw new Error('A real local Kestrel address is required.');
}
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../dist');
createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1:5174');
  if (/^\/(api|auth|connect|oauth)(\/|$)/.test(url.pathname)) {
    const upstream = upstreamRequest(new URL(request.url, backend), {
      method: request.method, headers: request.headers,
    }, result => {
      if (!['GET', 'HEAD'].includes(request.method)) console.log(`${request.method} ${url.pathname} HTTP ${result.statusCode}`);
      if (url.pathname.endsWith('/download')) {
        let bytes = 0; let signature;
        result.on('data', chunk => { bytes += chunk.length; if (signature === undefined && chunk.length >= 4) signature = chunk.readUInt32LE(0) === 0x04034b50; });
        result.on('end', () => console.log(`Download HTTP ${result.statusCode}, ${result.headers['content-type']}, ${bytes} bytes, OpenXML/ZIP signature=${signature === true}`));
      }
      response.writeHead(result.statusCode, result.headers); result.pipe(response);
    });
    upstream.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end(); });
    request.pipe(upstream);
    return;
  }
  try {
    const path = resolve(root, url.pathname.replace(/^\/+/, '') || 'index.html');
    if (path !== root && !path.startsWith(root + sep)) { response.writeHead(400); response.end(); return; }
    let bytes; let typePath = path;
    try { bytes = await readFile(path); }
    catch { typePath = resolve(root, 'index.html'); bytes = await readFile(typePath); }
    response.writeHead(200, { 'Content-Type': ({ '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png' })[extname(typePath)] ?? 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    response.end(bytes);
  } catch { response.writeHead(500); response.end(); }
}).listen(5174, '127.0.0.1', () => console.log('Real-backend browser gate ready at http://127.0.0.1:5174'));
