import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';

const root = resolve(process.argv[2] ?? 'dist');
const port = Number(process.argv[3] ?? 5181);
const mime = {'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.json':'application/json','.svg':'image/svg+xml','.png':'image/png','.jpg':'image/jpeg','.ico':'image/x-icon'};
createServer(async (request, response) => {
  try {
    const path = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
    const target = resolve(root, '.' + (path === '/' ? '/index.html' : path));
    if (target !== root && !target.startsWith(root + sep)) {
      response.writeHead(403); response.end('Forbidden'); return;
    }
    const data = await readFile(target);
    response.writeHead(200, {'Content-Type':mime[extname(target)] ?? 'application/octet-stream','Cache-Control':'no-store'});
    response.end(data);
  } catch {
    response.writeHead(404); response.end('Not found');
  }
}).listen(port, '127.0.0.1', () => console.log('F2-01_STATIC_SERVER http://127.0.0.1:' + port));
