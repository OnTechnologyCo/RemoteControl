import { readFileSync, appendFileSync, existsSync, statSync, renameSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createServer } from 'node:http';
import { createGateway, validateConfig } from './gateway.mjs';

const path = resolve(process.argv[2] || 'gateway.json');
const config = validateConfig(JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, '')));
const auditFile = resolve(dirname(path), 'connection-events.jsonl');
const gateway = createGateway(config, {directory: dirname(path), audit: event => {
  try {
    if (existsSync(auditFile) && statSync(auditFile).size > 256 * 1024) renameSync(auditFile, auditFile + '.previous');
    appendFileSync(auditFile, JSON.stringify({at:new Date().toISOString(),...event}) + '\n', {mode:0o600});
  } catch {}
}});
const server = createServer({maxHeaderSize:16384, requestTimeout:45000, headersTimeout:10000}, gateway.app);
server.maxConnections = 32;
server.listen(config.listenPort, '127.0.0.1', () => console.log('RemoteControl Web MCP ready on loopback port ' + config.listenPort));
server.on('error', () => { console.error('RemoteControl Web MCP listener failed'); process.exitCode=1; gateway.close(); });
for(const signal of ['SIGINT','SIGTERM']) process.on(signal,()=>{gateway.close();server.close();server.closeAllConnections();});
