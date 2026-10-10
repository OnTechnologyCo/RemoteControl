import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createGateway } from '../gateway.mjs';

const config=()=>({publicUrl:'https://remote.example.test',listenPort:59432,lanPort:59431,proxyKey:randomBytes(32).toString('base64url')});
const grant=(now)=>({clientId:'owner',clientName:'ChatGPT',cookie:'rcmcpid='+randomBytes(32).toString('base64url'),expires:Number.MAX_SAFE_INTEGER,revoked:false});
const target=c=>new URL(c.publicUrl+'/mcp');

test('encrypted OAuth state survives repeated gateway restarts and remains refreshable after 90 days',async()=>{
  const directory=mkdtempSync(join(tmpdir(),'rc-auth-')), c=config();let now=Date.now(), logins=0, logouts=0;
  const upstream=async(path)=>{if(path==='/login')logins++;if(path==='/logout')logouts++;return {status:200,body:'{}'};};
  const open=()=>createGateway(c,{directory,now:()=>now,upstream});let gateway=open();
  const original=grant(now);gateway.provider.grants.add(original);let tokens=gateway.provider.issue(original);
  const sealed=readFileSync(join(directory,'auth-state.bin'));
  for(const secret of [original.cookie,tokens.access_token,tokens.refresh_token])assert(!sealed.includes(Buffer.from(secret)));
  for(let i=0;i<3;i++){
    gateway.close();assert.equal(logouts,0);gateway=open();assert(await gateway.provider.verifyAccessToken(tokens.access_token));
    tokens=await gateway.provider.exchangeRefreshToken({client_id:'owner'},tokens.refresh_token,undefined,target(c));
  }
  now+=90*24*60*60000;gateway.close();gateway=open();
  await assert.rejects(gateway.provider.verifyAccessToken(tokens.access_token));
  tokens=await gateway.provider.exchangeRefreshToken({client_id:'owner'},tokens.refresh_token,undefined,target(c));
  assert(await gateway.provider.verifyAccessToken(tokens.access_token));assert.equal(logins,0);
  await gateway.provider.revokeToken({client_id:'owner'},{token:tokens.refresh_token});assert.equal(logouts,1);
  gateway.close();gateway=open();await assert.rejects(gateway.provider.verifyAccessToken(tokens.access_token));assert.equal(gateway.provider.grants.size,0);gateway.close();
});

test('temporary network and HTTP failures preserve the same refresh token',async()=>{
  const directory=mkdtempSync(join(tmpdir(),'rc-refresh-')), c=config();let mode='network', logouts=0;
  const gateway=createGateway(c,{directory,upstream:async(path)=>{if(path==='/logout'){logouts++;return {status:200};}if(mode==='network')throw Error('offline');return {status:mode==='ok'?200:503,body:'{}'};}});
  const g=grant(Date.now());gateway.provider.grants.add(g);const tokens=gateway.provider.issue(g), client={client_id:'owner'};
  for(mode of ['network','http']){
    await assert.rejects(gateway.provider.exchangeRefreshToken(client,tokens.refresh_token,undefined,target(c)),e=>e.errorCode==='temporarily_unavailable');
    assert.equal(gateway.provider.refresh.get(tokens.refresh_token).used,false);assert.equal(logouts,0);assert(await gateway.provider.verifyAccessToken(tokens.access_token));
  }
  mode='ok';const updated=await gateway.provider.exchangeRefreshToken(client,tokens.refresh_token,undefined,target(c));
  assert(await gateway.provider.verifyAccessToken(updated.access_token));gateway.close();
});

test('explicit LAN authentication rejection revokes persisted authorization',async()=>{
  const directory=mkdtempSync(join(tmpdir(),'rc-revoke-')), c=config(), upstream=async()=>({status:401,body:'{}'});
  let gateway=createGateway(c,{directory,upstream});const g=grant(Date.now());gateway.provider.grants.add(g);const tokens=gateway.provider.issue(g);
  await assert.rejects(gateway.provider.exchangeRefreshToken({client_id:'owner'},tokens.refresh_token,undefined,target(c)),e=>e.errorCode==='invalid_grant');
  gateway.close();gateway=createGateway(c,{directory,upstream});assert.equal(gateway.provider.grants.size,0);await assert.rejects(gateway.provider.verifyAccessToken(tokens.access_token));gateway.close();
});

test('concurrent refresh does not consume or revoke an in-flight token',async()=>{
  const directory=mkdtempSync(join(tmpdir(),'rc-concurrent-')), c=config();let release;
  const gateway=createGateway(c,{directory,upstream:async()=>new Promise(r=>release=r)});const g=grant(Date.now());gateway.provider.grants.add(g);const tokens=gateway.provider.issue(g), client={client_id:'owner'};
  const first=gateway.provider.exchangeRefreshToken(client,tokens.refresh_token,undefined,target(c));
  await assert.rejects(gateway.provider.exchangeRefreshToken(client,tokens.refresh_token,undefined,target(c)),e=>e.errorCode==='temporarily_unavailable');
  release({status:200,body:'{}'});const updated=await first;assert(await gateway.provider.verifyAccessToken(updated.access_token));gateway.close();
});

test('changed issuer, changed encryption key or damaged state cannot restore credentials',()=>{
  const directory=mkdtempSync(join(tmpdir(),'rc-bound-')), c=config();const gateway=createGateway(c,{directory,upstream:async()=>({status:200})});const g=grant(Date.now());gateway.provider.grants.add(g);gateway.provider.issue(g);gateway.close();
  assert.throws(()=>createGateway({...c,proxyKey:randomBytes(32).toString('base64url')},{directory}));
  const other=createGateway({...c,publicUrl:'https://other.example.test'},{directory});assert.equal(other.provider.grants.size,0);other.close();
  const file=join(directory,'auth-state.bin'), data=readFileSync(file);data[data.length-1]^=1;writeFileSync(file,data);assert.throws(()=>createGateway(c,{directory}));
});
