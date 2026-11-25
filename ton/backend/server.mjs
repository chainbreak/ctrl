import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { Auth } from './auth.mjs';
import { TonChain } from './chain.mjs';
import { HttpError, check } from './proof.mjs';

async function body(req) {
  check(req.headers['content-type']?.split(';')[0] === 'application/json', 415, 'Ожидается JSON');
  const parts = []; let bytes = 0;
  for await (const part of req) { bytes += part.length; check(bytes <= 16384, 413, 'Слишком большой запрос'); parts.push(part); }
  try { const data = JSON.parse(Buffer.concat(parts)); if (data && !Array.isArray(data) && typeof data === 'object') return data; } catch {}
  throw new HttpError(400, 'Некорректный JSON');
}
const asset = (path, type) => [new URL(path, import.meta.url), type];
const assets = new Map([
  ['/',asset('./public/index.html','text/html; charset=utf-8')],
  ['/login.js',asset('./public/login.js','application/javascript')],
  ['/icon.png',asset('./public/icon.png','image/png')],
  ['/tonconnect.js',asset('../node_modules/@tonconnect/ui/dist/tonconnect-ui.min.js','application/javascript')]
]);
export function createApi(auth, origin) {
  const attempts = new Map();
  const server = createServer(async (req, res) => {
    res.setHeader('Cache-Control','no-store');
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Referrer-Policy','no-referrer');
    res.setHeader('X-Frame-Options','DENY');
    const json = (status, data) => { res.writeHead(status, {'Content-Type':'application/json; charset=utf-8'}); res.end(JSON.stringify(data)); };
    try {
      const route = req.url;
      if (req.method === 'GET' && assets.has(route)) {
        const [path,type] = assets.get(route);
        const file = await readFile(path); res.writeHead(200, {'Content-Type':type}); res.end(file); return;
      }
      if (req.method === 'GET' && route === '/tonconnect-manifest.json') {
        res.setHeader('Access-Control-Allow-Origin','*');
        return json(200, {url:origin, name:'Ctrl', iconUrl:origin+'/icon.png'});
      }
      if (req.method === 'GET' && route === '/config') return json(200, {network:auth.network, origin});
      if (req.headers.origin) check(req.headers.origin === origin, 403, 'Неверный источник запроса');
      const bearer = req.headers.authorization?.match(/^Bearer (\S+)$/i)?.[1];
      if (req.method === 'POST' && (route === '/auth/challenge' || route === '/auth/verify')) {
        const ip = req.socket.remoteAddress, now = Date.now();
        const hit = attempts.get(ip);
        if (!hit || hit.until <= now) {
          if (attempts.size >= 4096) for (const [key,value] of attempts) if (value.until <= now) attempts.delete(key);
          check(attempts.size < 4096 || attempts.has(ip), 429, 'Повторите попытку через минуту');
          attempts.set(ip,{count:1,until:now+60000});
        } else { check(++hit.count <= 45, 429, 'Повторите попытку через минуту'); }
        const data = await body(req);
        return json(200, route === '/auth/challenge' ? auth.challenge(data.number) : await auth.login(data));
      }
      if (req.method === 'GET' && route === '/auth/me') return json(200, await auth.me(bearer));
      if (req.method === 'POST' && route === '/auth/logout') { await auth.logout(bearer); return json(200, {ok:true}); }
      return json(404, {error:'Страница не найдена'});
    } catch (err) {
      if (!err.status) console.error('Request failed:', err.name);
      return json(err.status ?? 500, {error:err.status ? err.message : 'Ошибка сервера'});
    }
  });
  return server;
}
function main() {
  const e = process.env;
  const origin = new URL(e.PUBLIC_ORIGIN);
  if (origin.origin !== e.PUBLIC_ORIGIN || (origin.protocol !== 'https:' && !['localhost','127.0.0.1'].includes(origin.hostname))) throw Error('Expected HTTPS origin');
  if (!['-3','-239'].includes(e.TON_NETWORK)) throw Error('Invalid TON_NETWORK');
  if (!/^[0-9a-f]{64}$/i.test(e.JWT_SECRET_HEX ?? '')) throw Error('Set JWT_SECRET_HEX to a random 32-byte hex key');
  const file = resolve(e.AUTH_DB || 'data/auth.sqlite'); mkdirSync(dirname(file), {recursive:true});
  const chain = new TonChain(e.TON_V4_ENDPOINT, e.NUMBERS_COLLECTION);
  const auth = new Auth({file, chain, domain:origin.host, network:e.TON_NETWORK, secret:Buffer.from(e.JWT_SECRET_HEX,'hex')});
  let running = false;
  const timer = setInterval(async () => {
    if (running) return;
    running = true;
    try { await auth.sweep(); } catch { console.error('TON ownership check unavailable'); } finally { running = false; }
  }, 30000);
  const server = createApi(auth, origin.origin);
  server.listen(Number(e.PORT ?? 8788), e.BIND_HOST || '127.0.0.1', () => console.log('Ctrl Auth ready'));
  const stop = () => { clearInterval(timer); server.close(); };
  process.on('SIGINT', stop); process.on('SIGTERM', stop);
}
if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) main();

