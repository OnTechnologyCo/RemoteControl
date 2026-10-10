import express from 'express';
import { rateLimit } from 'express-rate-limit';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { request } from 'node:http';
import { isIP } from 'node:net';
import { existsSync, readFileSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';
import { AuthState } from './auth-state.mjs';
import { attachTransfers, fileTool } from './transfers.mjs';
import { mcpAuthRouter, createOAuthMetadata } from '@modelcontextprotocol/sdk/server/auth/router.js';
import { InvalidClientMetadataError, InvalidGrantError, InvalidTargetError, InvalidScopeError, InvalidRequestError, InvalidTokenError, TemporarilyUnavailableError } from '@modelcontextprotocol/sdk/server/auth/errors.js';

export const CALLBACK = 'https://chatgpt.com/connector_platform_oauth_redirect';
export const CIMD = 'https://chatgpt.com/oauth/client.json';
export const CLAUDE_CALLBACK = 'https://claude.ai/api/mcp/auth_callback';
export const CLAUDE_CIMD = 'https://claude.ai/oauth/mcp-oauth-client-metadata';
const CLIENTS = new Map([
  [CIMD, {name:'ChatGPT', callback:CALLBACK}],
  [CLAUDE_CIMD, {name:'Claude', callback:CLAUDE_CALLBACK}]
]);
const CALLBACKS = new Map([...CLIENTS.values()].map(client => [client.callback, client.name]));
const SCOPE = 'remotecontrol';
const COOKIE = '__Host-rc-connect';
const minute = 60000;
const random = () => randomBytes(32).toString('base64url');
const equal = (a, b) => typeof a === 'string' && typeof b === 'string' && Buffer.byteLength(a) === Buffer.byteLength(b) && timingSafeEqual(Buffer.from(a), Buffer.from(b));
const html = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

export function validateConfig(value) {
  const c = { ...value };
  const u = new URL(c.publicUrl);
  if (u.protocol !== 'https:' || u.pathname !== '/' || u.search || u.hash || u.username || u.password || isIP(u.hostname))
    throw Error('publicUrl에는 경로 없는 HTTPS DDNS 주소를 지정하세요.');
  c.publicUrl = u.origin;
  for (const k of ['listenPort', 'lanPort']) if (!Number.isInteger(c[k]) || c[k] < 1024 || c[k] > 65535) throw Error(k + ': 1024~65535 포트가 필요합니다.');
  if (c.lanPort === c.listenPort) throw Error('LAN MCP와 웹 중계 포트는 달라야 합니다.');
  if (!/^[A-Za-z0-9_-]{43}$/.test(c.proxyKey)) throw Error('설치 시 생성한 프록시 키가 필요합니다.');
  return c;
}

// 대상 주소는 설정된 loopback 포트 하나뿐이다. 도구 인자나 공개 요청으로 바꿀 수 없다.
export function lanRequest(port, path, { body = '', cookie = '' } = {}) {
  return new Promise((resolve, reject) => {
    const req = request({ hostname: '127.0.0.1', port, path, method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body), ...(cookie ? { Cookie: cookie } : {}) } }, res => {
      const parts = []; let size = 0;
      res.on('data', part => { size += part.length; if (size > 8 * 1024 * 1024) res.destroy(Error('LAN response too large')); else parts.push(part); });
      res.on('error', reject);
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(parts).toString('utf8') }));
    });
    // 실행 명령은 응답 유실 뒤 재전송하지 않는다.
    req.setTimeout(35000, () => req.destroy(Error('LAN MCP timeout')));
    req.on('error', reject); req.end(body);
  });
}

async function getClientMetadata(id) {
  // 임의 client_id URL은 가져오지 않아 사설망 SSRF와 리디렉션 우회를 막는다.
  if (!CLIENTS.has(id)) throw Error('Unknown client metadata URL');
  const r = await fetch(id, { redirect: 'error', signal: AbortSignal.timeout(5000) });
  if (!r.ok) throw Error('Client metadata unavailable');
  const reader = r.body.getReader(); let size = 0; const parts = [];
  try {
    while (true) { const {done, value} = await reader.read(); if (done) break; size += value.length; if (size > 32768) throw Error('Client metadata too large'); parts.push(Buffer.from(value)); }
    return JSON.parse(Buffer.concat(parts).toString('utf8'));
  } finally { await reader.cancel(); }
}

class Clients {
  constructor(directory, metadata) {
    this.file = join(directory, 'clients.json'); this.metadata = metadata;
    this.cache = new Map(); this.pending = new Map();
    this.clients = new Map(existsSync(this.file) ? JSON.parse(readFileSync(this.file, 'utf8')).map(c => [c.client_id, c]) : []);
    if (this.clients.size > 128) throw Error('Too many OAuth clients');
  }
  async getClient(id) {
    const known = CLIENTS.get(id);
    if (!known) return this.clients.get(id);
    const cached = this.cache.get(id);
    if (cached && cached.until > Date.now()) return cached.client;
    // 동시 인증 요청도 같은 문서를 한 번만 읽는다.
    if (!this.pending.has(id)) this.pending.set(id, (async () => {
      const m = await this.metadata(id);
      const methods = m.token_endpoint_auth_methods_supported || [m.token_endpoint_auth_method];
      if (m.client_id !== id || !Array.isArray(m.redirect_uris) || !m.redirect_uris.includes(known.callback) || !methods.includes('none'))
        throw Error('Client metadata does not support this public client');
      const client = { client_id: id, client_name: known.name, redirect_uris: [known.callback], token_endpoint_auth_method: 'none', grant_types: ['authorization_code', 'refresh_token'], response_types: ['code'] };
      this.cache.set(id, {client, until:Date.now() + 60 * minute});
      return client;
    })().finally(() => { this.pending.delete(id); }));
    return this.pending.get(id);
  }
  async registerClient(client) {
    if (!Array.isArray(client.redirect_uris) || client.redirect_uris.length !== 1 || !CALLBACKS.has(client.redirect_uris[0]) ||
      !['none','client_secret_post'].includes(client.token_endpoint_auth_method || 'client_secret_post') ||
      client.grant_types?.some(g => !['authorization_code','refresh_token'].includes(g)) ||
      client.response_types?.some(r => r !== 'code')) throw new InvalidClientMetadataError('Supported callback and authorization_code flow required');
    if (this.clients.size >= 128) throw new TemporarilyUnavailableError('OAuth client registration limit reached');
    // DCR 이름은 누구나 정할 수 있어 로그인 화면의 신원 표시로 신뢰하지 않는다.
    const saved = { ...client, client_name: CALLBACKS.get(client.redirect_uris[0]), client_id: random(), client_id_issued_at: Math.floor(Date.now()/1000) };
    const next = [...this.clients.values(), saved];
    writeFileSync(this.file + '.tmp', JSON.stringify(next), { mode: 0o600 });
    renameSync(this.file + '.tmp', this.file); this.clients.set(saved.client_id, saved);
    return saved;
  }
}

class Provider {
  constructor(config, directory, upstream, metadata, now) {
    this.config = config; this.upstream = upstream; this.now = now;
    this.issuer = config.publicUrl + '/'; this.resource = config.publicUrl + '/mcp';
    this.clientsStore = new Clients(directory, metadata);
    this.pending = new Map(); this.codes = new Map(); this.access = new Map(); this.refresh = new Map(); this.grants = new Set();
    this.state = new AuthState(directory, config.proxyKey, this.issuer);
    this.restore();
    this.timer = setInterval(() => this.sweep(), minute); this.timer.unref();
  }
  restore() {
    const saved = this.state.read();
    if (!saved) return;
    if (saved.version !== 1 || !Array.isArray(saved.grants) || !Array.isArray(saved.access) || !Array.isArray(saved.refresh) || saved.grants.length > 64 || saved.access.length > 256 || saved.refresh.length > 4096) throw Error('Invalid OAuth state');
    const grants = new Map();
    for (const grant of saved.grants) {
      if (!/^[A-Za-z0-9_-]{43}$/.test(grant.id) || typeof grant.clientId !== 'string' || typeof grant.cookie !== 'string' || grant.cookie.length > 256 || !Number.isSafeInteger(grant.expires) || grants.has(grant.id)) throw Error('Invalid OAuth grant');
      grant.revoked = false; grant.authorized = true; grants.set(grant.id, grant); this.grants.add(grant);
    }
    for (const [name, table] of [['access',this.access],['refresh',this.refresh]]) for (const row of saved[name]) {
      if (!/^[A-Za-z0-9_-]{43}$/.test(row.token) || !grants.has(row.grant) || !Number.isSafeInteger(row.expires) || (name === 'refresh' && typeof row.used !== 'boolean') || table.has(row.token)) throw Error('Invalid OAuth token');
      table.set(row.token, {grant:grants.get(row.grant), expires:row.expires, ...(name === 'refresh' ? {used:row.used} : {})});
    }
    this.sweep();
  }
  persist() {
    const grants = [...this.grants].filter(g => g.authorized && !g.revoked);
    const ids = new Set(grants.map(g => g.id));
    const rows = table => [...table].filter(([,v]) => ids.has(v.grant.id)).map(([token,v]) => ({token, grant:v.grant.id, expires:v.expires, ...(table === this.refresh ? {used:v.used} : {})}));
    try { this.state.write({version:1, grants:grants.map(g => ({id:g.id,clientId:g.clientId,clientName:g.clientName,cookie:g.cookie,expires:g.expires})), access:rows(this.access), refresh:rows(this.refresh)}); }
    catch { throw new TemporarilyUnavailableError('Authentication storage temporarily unavailable'); }
  }
  checkResource(resource) { if (resource?.href !== this.resource) throw new InvalidTargetError('resource must match this MCP server'); }
  checkScopes(scopes) { if (scopes?.some(s => s !== SCOPE)) throw new InvalidScopeError('Only remotecontrol scope is supported'); }
  sweep() {
    let changed = false;
    for (const item of this.codes.values()) if (item.expires <= this.now()) this.drop(item.grant);
    for (const table of [this.pending, this.codes, this.access, this.refresh]) for (const [key, v] of table) if (v.expires <= this.now()) { table.delete(key); if (table === this.access || table === this.refresh) changed = true; }
    for (const grant of this.grants) if (grant.expires <= this.now()) this.drop(grant);
    if (changed) this.persist();
  }
  async authorize(client, params, res) {
    this.sweep(); this.checkResource(params.resource); this.checkScopes(params.scopes);
    if (!/^[A-Za-z0-9_-]{43}$/.test(params.codeChallenge) || !params.state || params.state.length > 1024)
      throw new InvalidRequestError('S256 challenge and state required');
    if (this.pending.size >= 128 || this.grants.size >= 64) throw new TemporarilyUnavailableError('Too many pending connections');
    const transaction = random(), csrf = random();
    this.pending.set(transaction, { ...params, clientId: client.client_id, csrf, expires: this.now() + 10 * minute });
    res.cookie(COOKIE, transaction, { httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 10 * minute });
    res.type('html').send(loginPage(csrf, this.config.publicUrl, CALLBACKS.get(params.redirectUri)));
  }
  async login(req, res) {
    this.sweep();
    const transaction = (req.headers.cookie || '').split(';').map(s => s.trim()).find(s => s.startsWith(COOKIE + '='))?.slice(COOKIE.length + 1);
    const pending = this.pending.get(transaction);
    if (req.get('origin') !== this.config.publicUrl || !pending || !equal(req.body?.csrf, pending.csrf))
      return res.status(403).type('text').send('연결 요청이 만료되었거나 올바르지 않습니다. 사용 중인 앱에서 다시 연결하세요.');
    // 한 폼의 동시 제출로 여러 인증 코드/세션이 생기지 않게 먼저 소모한다.
    this.pending.delete(transaction);
    res.clearCookie(COOKIE, { httpOnly: true, secure: true, sameSite: 'lax', path: '/' });
    const redirect = new URL(pending.redirectUri);
    redirect.searchParams.set('state', pending.state); redirect.searchParams.set('iss', this.issuer);
    if (req.body.decision !== 'allow') { redirect.searchParams.set('error', 'access_denied'); return returnToClient(res, redirect, false); }
    if (this.grants.size >= 64) return res.status(503).type('text').send('연결 수가 많습니다. 잠시 후 다시 연결하세요.');
    const {user, password} = req.body;
    if (typeof user !== 'string' || typeof password !== 'string' || !user || !password || user.length > 128 || password.length > 1024)
      return res.status(400).type('text').send('아이디와 비밀번호를 확인하고 사용 중인 앱에서 다시 연결하세요.');
    let result;
    try { result = await this.upstream('/login', {body: JSON.stringify({user, password})}); }
    catch { return res.status(503).type('text').send('이 PC의 원격제어 MCP에 연결할 수 없습니다. 실행 상태를 확인하세요.'); }
    // 비밀번호는 디스크, 토큰, 로그에 남기지 않는다. 승인된 인증 쿠키와 토큰만 암호화해 보관한다.
    req.body.password = '';
    if (result.status !== 200) return res.status(result.status === 429 ? 429 : 401).type('text').send(result.status === 429 ? '로그인 시도가 반복되어 잠시 잠겼습니다. 잠시 후 사용 중인 앱에서 다시 연결하세요.' : '원격제어 아이디 또는 비밀번호가 맞지 않습니다. 사용 중인 앱에서 다시 연결하세요.');
    const cookie = result.headers['set-cookie']?.find(c => /^rcmcpid=[A-Za-z0-9_-]+;/.test(c))?.split(';')[0];
    if (!cookie) return res.status(502).type('text').send('원격제어 로그인 응답을 확인할 수 없습니다.');
    const grant = { clientId: pending.clientId, clientName:CALLBACKS.get(pending.redirectUri), cookie, expires: Number.MAX_SAFE_INTEGER, revoked: false };
    this.grants.add(grant);
    const code = random(); this.codes.set(code, { ...pending, grant, expires: this.now() + 5 * minute });
    redirect.searchParams.set('code', code); returnToClient(res, redirect, true);
  }
  code(client, code) {
    const item = this.codes.get(code);
    if (!item || item.expires <= this.now() || item.clientId !== client.client_id || item.grant.revoked) throw new InvalidGrantError('Invalid authorization code');
    return item;
  }
  async challengeForAuthorizationCode(client, code) { return this.code(client, code).codeChallenge; }
  async exchangeAuthorizationCode(client, code, verifier, redirectUri, resource) {
    this.checkResource(resource);
    const item = this.code(client, code);
    if (redirectUri !== item.redirectUri) throw new InvalidGrantError('redirect_uri does not match');
    this.codes.delete(code);
    return this.issue(item.grant);
  }
  issue(grant, consumed = null) {
    this.sweep();
    if (grant.revoked || grant.expires <= this.now()) throw new InvalidGrantError('Connection expired');
    if (this.access.size >= 256 || this.refresh.size >= 4096) throw new TemporarilyUnavailableError('OAuth token limit reached');
    const access_token = random(), refresh_token = random();
    const expires_in = Math.min(3600, Math.floor((grant.expires - this.now()) / 1000));
    this.access.set(access_token, {grant, expires: this.now() + expires_in * 1000});
    this.refresh.set(refresh_token, {grant, expires: grant.expires, used: false});
    const previous = consumed && {used:consumed.used, expires:consumed.expires};
    if (consumed) { consumed.used = true; consumed.expires = Math.min(grant.expires, this.now() + 24 * 60 * minute); }
    grant.id ||= random(); grant.authorized = true;
    try { this.persist(); } catch (error) { this.access.delete(access_token); this.refresh.delete(refresh_token); if (consumed) Object.assign(consumed, previous); throw error; }
    return { access_token, token_type: 'Bearer', expires_in, refresh_token, scope: SCOPE };
  }
  async exchangeRefreshToken(client, token, scopes, resource) {
    this.checkResource(resource); this.checkScopes(scopes);
    const item = this.refresh.get(token);
    if (!item || item.expires <= this.now() || item.grant.clientId !== client.client_id || item.grant.revoked) throw new InvalidGrantError('Invalid refresh token');
    // 지난 refresh 토큰의 재사용은 같은 연결에서 발급한 토큰 전체를 폐기한다.
    if (item.used) { this.drop(item.grant); throw new InvalidGrantError('Refresh token reused'); }
    if (item.busy) throw new TemporarilyUnavailableError('Refresh already in progress');
    item.busy = true;
    let probe;
    try {
      try { probe = await this.upstream('/mcp', { cookie: item.grant.cookie, body: JSON.stringify({jsonrpc:'2.0',id:1,method:'ping'}) }); }
      catch { throw new TemporarilyUnavailableError('LAN MCP temporarily unreachable; retry refresh'); }
      if (probe.status === 401) { this.drop(item.grant); throw new InvalidGrantError('RemoteControl login revoked'); }
      if (probe.status !== 200) throw new TemporarilyUnavailableError('LAN MCP temporarily unavailable; retry refresh');
      return this.issue(item.grant, item);
    } finally { item.busy = false; }
  }
  async verifyAccessToken(token) {
    const item = this.access.get(token);
    if (!item || item.expires <= this.now() || item.grant.expires <= this.now() || item.grant.revoked) throw new InvalidTokenError('Reconnect to RemoteControl');
    return item.grant;
  }
  async revokeToken(client, request) {
    const item = this.access.get(request.token) || this.refresh.get(request.token);
    if (item?.grant.clientId === client.client_id) this.drop(item.grant);
  }
  drop(grant) {
    if (grant.revoked) return;
    grant.revoked = true; this.grants.delete(grant);
    for (const table of [this.codes, this.access, this.refresh]) for (const [key, item] of table) if (item.grant === grant) table.delete(key);
    this.persist();
    this.upstream('/logout', {cookie: grant.cookie}).catch(() => {});
  }
  close() { clearInterval(this.timer); for (const grant of this.grants) if (!grant.authorized) this.drop(grant); this.persist(); }
}

function returnToClient(res, redirect, allowed) {
  const clientName = CALLBACKS.get(redirect.origin + redirect.pathname);
  if (!clientName) throw Error('Unexpected OAuth callback');
  const nonce = random();
  // Commit a new document before leaving the site. A form's 303 redirect chain
  // carries form-action into subsequent app/login redirects in mobile browsers.
  res.set('Content-Security-Policy', `default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'`);
  res.type('html').send(`<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>원격제어 · ${clientName}로 돌아가기</title>
<style>body{font:16px system-ui,sans-serif;background:#101a29;color:#eef3fa;padding:32px 20px}main{max-width:430px;margin:5vh auto}p{line-height:1.7}a{display:inline-block;padding:14px 20px;background:#73d9ee;color:#10202c;border-radius:8px;text-decoration:none}</style>
<main><h1>${allowed ? '원격제어 로그인 성공' : '연결을 취소했습니다'}</h1><p>${clientName}로 돌아가는 중입니다. 화면이 이동하지 않으면 아래 버튼을 눌러 주세요.</p><a id="return-client" href="${html(redirect.href)}" rel="noreferrer">${clientName}로 돌아가기</a><p>${clientName}에서 연결 완료가 표시되어야 사용할 수 있습니다.</p></main>
<script nonce="${nonce}">setTimeout(function(){location.replace(document.getElementById('return-client').href)},250)</script></html>`);
}

function loginPage(csrf, origin, clientName) {
  if (!['ChatGPT','Claude'].includes(clientName)) throw Error('Unknown login client');
  return `<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>원격제어 · ${clientName} 연결</title>
<style>body{font:16px system-ui,sans-serif;background:#101a29;color:#eef3fa;margin:0;padding:32px 20px}main{max-width:430px;margin:5vh auto;background:#1b2a40;padding:32px;border-radius:16px}h1{font-size:25px}p{line-height:1.65;color:#ced9e8}label{display:block;margin-top:18px}input{box-sizing:border-box;width:100%;padding:12px;margin-top:7px;border:1px solid #8091ab;border-radius:7px;font:inherit}button{font:inherit;cursor:pointer;padding:12px 16px;border:0;border-radius:7px;margin:24px 8px 0 0;background:#73d9ee;color:#10202c}button[value=deny]{background:#384c66;color:white}small{overflow-wrap:anywhere;color:#b3c6df}</style>
<main><small>${html(origin)}</small><h1>${clientName}에 원격제어 연결</h1><p>이 PC의 원격제어 아이디와 비밀번호를 입력하세요.</p><p>연결하면 ${clientName}가 MCP 설정에서 허용한 파일, 명령 실행, 화면 조회 기능을 사용합니다. 실행 권한은 대상 원격제어 프로그램의 권한을 따릅니다.</p>
<form method="post" action="/connect"><input type="hidden" name="csrf" value="${html(csrf)}"><label>원격제어 아이디<input name="user" autocomplete="username" maxlength="128" required></label><label>비밀번호<input name="password" type="password" autocomplete="current-password" maxlength="1024" required></label><button name="decision" value="allow">로그인하고 연결</button><button name="decision" value="deny" formnovalidate>취소</button></form><p><small>비밀번호는 ${clientName}에 전달하거나 이 중계 프로그램에 저장하지 않습니다. 인증은 재시작 후에도 유지되며, 연결 해제 또는 비밀번호 변경 시 해제됩니다.</small></p></main></html>`;
}

export function createGateway(config, { directory, upstream, metadata = getClientMetadata, now = Date.now, audit = () => {} } = {}) {
  const c = validateConfig(config), app = express();
  const provider = new Provider(c, directory, upstream || ((path, data) => lanRequest(c.lanPort, path, data)), metadata, now);
  const authOptions = { provider, issuerUrl: new URL(c.publicUrl), resourceServerUrl: new URL(provider.resource), scopesSupported: [SCOPE], resourceName: 'RemoteControl', clientRegistrationOptions: { clientSecretExpirySeconds: 0 } };
  const oauth = { ...createOAuthMetadata(authOptions), client_id_metadata_document_supported: true, authorization_response_iss_parameter_supported: true };
  const resourceMetadata = { resource: provider.resource, authorization_servers: [provider.issuer], scopes_supported: [SCOPE], bearer_methods_supported: ['header'], resource_name: 'RemoteControl' };
  const challenge = `Bearer resource_metadata="${c.publicUrl}/.well-known/oauth-protected-resource", scope="${SCOPE}"`;
  app.disable('x-powered-by'); app.set('trust proxy', false); app.set('query parser', 'simple');
  app.use((req, res, next) => {
    // Only fixed stage names and numeric status are recorded, never request
    // headers, URLs/queries, form fields, passwords, cookies or OAuth tokens.
    const stages = {'/authorize':'authorize','/connect':'login','/token':'token','/mcp':'mcp','/register':'register'};
    const stage = stages[req.path], started = Date.now();
    if (stage) res.once('finish', () => {
      try { audit({stage, method:['GET','POST','DELETE'].includes(req.method) ? req.method : 'OTHER', status:res.statusCode, ms:Date.now()-started}); } catch {}
    });
    next();
  });
  app.use((req, res, next) => {
    // HTML 폼 POST는 no-referrer에서 Origin:null이 되어 정상 로그인도 거부된다.
    // 같은 사이트 Origin은 보존하고 OAuth 외부 이동에는 Referer를 보내지 않는다.
    res.set({'Cache-Control':'no-store', 'X-Content-Type-Options':'nosniff', 'Referrer-Policy':'same-origin', 'X-Frame-Options':'DENY',
      // Chromium은 폼의 303 이동에도 form-action을 검사한다. 등록된 callback만 허용한다.
      'Content-Security-Policy':`default-src 'none'; style-src 'unsafe-inline'; form-action 'self' ${[...CALLBACKS.keys()].join(' ')}; frame-ancestors 'none'; base-uri 'none'`});
    if (req.headers.host !== new URL(c.publicUrl).host || !equal(req.get('x-rc-proxy-key'), c.proxyKey) || req.get('x-forwarded-proto') !== 'https' || !isIP(req.get('x-rc-client-ip') || ''))
      return res.status(403).send('HTTPS proxy required');
    const origin = req.get('origin');
    if (origin && ![c.publicUrl, 'https://chatgpt.com', 'https://claude.ai'].includes(origin)) return res.status(403).send('Origin rejected');
    next();
  });
  // 헤더 위조로 키를 늘릴 수 없는 전역 제한을 먼저 적용한다.
  app.use(rateLimit({windowMs:minute, limit:600, keyGenerator:()=> 'gateway', standardHeaders:true, legacyHeaders:false}));
  let active = 0;
  app.use((req,res,next) => { if(active>=16)return res.status(503).send('Busy'); active++; let done=false; const release=()=>{if(!done){done=true;active--;}};res.once('finish',release);res.once('close',release);next(); });
  app.use((req, res, next) => {
    // SDK가 만드는 성공/오류 리디렉션 모두 같은 issuer를 명시해야 ChatGPT의 고정 callback을 쓸 수 있다.
    const original = res.redirect.bind(res);
    res.redirect = (status, url) => { if (url === undefined) {url = status;status = 302;} const target = new URL(url, c.publicUrl); if (CALLBACKS.has(target.origin + target.pathname)) target.searchParams.set('iss', provider.issuer); return original(status, target.href); };
    next();
  });
  app.get('/.well-known/oauth-authorization-server', (req,res)=>res.json(oauth));
  app.get(['/.well-known/oauth-protected-resource','/.well-known/oauth-protected-resource/mcp'], (req,res)=>res.json(resourceMetadata));
  app.get('/', (req,res)=>res.type('text').send('RemoteControl · ChatGPT MCP 연결 주소: ' + provider.resource));
  app.post('/connect', rateLimit({windowMs:minute,limit:10,keyGenerator:()=> 'login',standardHeaders:true,legacyHeaders:false}), express.urlencoded({extended:false,limit:'8kb',parameterLimit:8}), (req,res,next)=>provider.login(req,res).catch(next));
  // OAuth 요청과 MCP 요청은 본문 제한이 다르다. 인증 경로에서 큰 JSON을 먼저 읽지 않는다.
  app.use(['/authorize','/token','/register','/revoke'], express.urlencoded({extended:false,limit:'16kb',parameterLimit:20}), express.json({limit:'16kb'}));
  app.use('/token', (req, res, next) => {
    // SDK는 OAuthError를 모두 400으로 보낸다. 임시 오프라인은 재로그인 대신 갱신을 재시도할 수 있게 503으로 구분한다.
    const json = res.json.bind(res);
    res.json = body => { if (body?.error === 'temporarily_unavailable') res.status(503).set('Retry-After','30'); return json(body); };
    next();
  });
  const files = attachTransfers(app,provider,c,now);
  app.use(mcpAuthRouter(authOptions));
  app.all('/mcp', async (req,res,next) => {
    let grant;
    try { const token = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(req.get('authorization') || '')?.[1]; grant = await provider.verifyAccessToken(token); }
    catch { return res.status(401).set('WWW-Authenticate', challenge).json({error:'invalid_token'}); }
    if (req.method !== 'POST') return res.status(405).set('Allow','POST').end();
    req.grant = grant; next();
  }, express.json({limit:'2mb'}), async (req,res,next) => {
    try {
      const rpc = req.body;
      if (!rpc || typeof rpc !== 'object' || Array.isArray(rpc) || rpc.jsonrpc !== '2.0' || typeof rpc.method !== 'string') return res.status(400).json({error:'Invalid JSON-RPC request'});
      const opening = rpc.method === 'tools/call' && rpc.params?.name === fileTool.name;
      const forwarded = opening ? {jsonrpc:'2.0',id:rpc.id,method:'ping'} : rpc;
      const reply = await provider.upstream('/mcp', {cookie:req.grant.cookie, body:JSON.stringify(forwarded)});
      if(reply.status === 401) {
        provider.drop(req.grant);
        if(rpc.method === 'tools/call' && req.grant.clientName !== 'Claude') return res.json({jsonrpc:'2.0',id:rpc.id,result:{isError:true,content:[{type:'text',text:'원격제어 로그인이 만료되었습니다. 다시 연결하세요.'}],_meta:{'mcp/www_authenticate':[challenge]}}});
        return res.status(401).set('WWW-Authenticate',challenge).json({error:'invalid_token'});
      }
      if(reply.status === 202) return res.status(202).end();
      if(reply.status !== 200) return res.status(502).json({error:'LAN MCP request failed; operation was not retried'});
      const value = opening ? {jsonrpc:'2.0',id:rpc.id,result:{content:[{type:'text',text:JSON.stringify(files.open(req.grant))}]}} : JSON.parse(reply.body);
      if(rpc.method === 'tools/list' && Array.isArray(value.result?.tools)) value.result.tools.push({...fileTool});
      if(rpc.method === 'tools/list' && Array.isArray(value.result?.tools)) for(const tool of value.result.tools) { tool.securitySchemes = [{type:'oauth2',scopes:[SCOPE]}]; tool._meta = {...tool._meta,securitySchemes:tool.securitySchemes}; }
      if(rpc.method === 'initialize' && value.result) { value.result.serverInfo.name = 'RemoteControl Web'; value.result.instructions = 'Always call pc_get_status before any change and verify the target hostname. Paths refer to the remote PC, and commands run with the remote agent Windows rights. Read-only status queries may run immediately. On a read-only connection or authentication failure, do not immediately ask the user to log in: retry the same status/read call up to two times. If a retry succeeds, continue without asking for login. Distinguish authentication failures from offline, transport, or internal failures; if the PC or MCP server is unreachable, report it as offline or unreachable instead of requesting login. Ask the user to reconnect or log in only after repeated explicit reauthentication_required or invalid_token failures. Never automatically retry shutdown, reboot, delete, kill, write, or other state-changing operations after an ambiguous timeout or transport failure; verify the current state first and require clear user intent for destructive actions. For tracked process output, continue from next_offset and avoid duplicate logs. Do not invent unavailable tools or capabilities. Use pc_open_file_transfer for a user browser file/folder picker and streamed downloads without a local connector. For direct binary data use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk; verify SHA-256. Retain transfer IDs; query status after a failed chunk before resuming. Local and external clients use this same HTTPS /mcp URL.'; }
      res.json(value);
    } catch(error) { next(error); }
  });
  app.use((req,res)=>res.status(404).json({error:'Not found'}));
  app.use((error,req,res,next)=> { if(res.headersSent)return next(error); res.status(error.status === 413 ? 413 : error.status === 400 ? 400 : 502).json({error:'Request failed; operation was not retried'}); });
  return {app, provider, close:()=>provider.close()};
}
