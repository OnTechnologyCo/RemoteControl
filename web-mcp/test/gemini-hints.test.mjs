import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { once } from 'node:events';
import { fixture, redirects, returned } from './helpers/gemini-fixture.mjs';
import { lanRequest, CALLBACK, CLAUDE_CALLBACK } from '../gateway.mjs';
import { applyGeminiHints, hintsMetadataKey } from '../gemini-hints.mjs';
import { fileTool } from '../transfers.mjs';

test('hint transformation clones nested catalog and preserves all unrelated metadata', () => {
  const tool = {...structuredClone(fileTool), _meta:{existing:{value:42}}, securitySchemes:[{type:'oauth2'}]};
  const before = structuredClone(tool);
  const changed = applyGeminiHints([tool], {[tool.name]:false});
  assert.deepEqual(tool,before);
  const setter={name:'pc_set_gemini_settings',description:'Save policy',annotations:{readOnlyHint:false,destructiveHint:false,idempotentHint:true,openWorldHint:false}};
  assert.deepEqual(applyGeminiHints([setter],{pc_set_gemini_settings:true}),[setter]);
  assert.deepEqual(changed[0].inputSchema,tool.inputSchema);
  for (const key of ['destructiveHint','idempotentHint','openWorldHint']) assert.equal(changed[0].annotations[key],tool.annotations[key]);
  assert.equal(changed[0]._meta['remotecontrol/defaultReadOnlyHint'],true);
  assert.equal(changed[0]._meta['remotecontrol/readOnlyHintOverride'],true);
  changed[0]._meta.existing.value=0; changed[0].inputSchema.properties.fake={};
  assert.deepEqual(tool,before);
  for(const input of [null,[],true,{'pc_get_status':'true'},{'pc_get_status':null},Object.fromEntries(Array.from({length:2000},(_,i)=>['x'+i,true]))])
    assert.throws(()=>applyGeminiHints([tool],input));
});

test('saved native settings reach authenticated Google HTTP tools/list only', {timeout:90000}, async t => {
  const directory=mkdtempSync(join(tmpdir(),'rc-hints-'));
  let child, lines, port, errors='';
  async function start(){
    child=spawn(resolve('../artifacts/gemini-settings-test/GeminiSettingsTest.exe'),['serve',directory],{stdio:['pipe','pipe','pipe'],windowsHide:true});
    child.stderr.on('data',b=>errors+=b);
    lines=createInterface({input:child.stdout});
    const first=await Promise.race([once(lines,'line'),once(child,'exit').then(([code])=>{throw Error('Fixture exit '+code+' '+errors);})]);
    port=Number(first[0]);assert(port>0,errors);
  }
  async function stop(){const exited=once(child,'exit');child.stdin.end();await exited;lines.close();}
  async function save(value,ok=true){const next=once(lines,'line');child.stdin.write((typeof value==='string'?value:JSON.stringify(value))+'\n');assert.equal((await next)[0],ok?'saved':'rejected');}
  await start();t.after(stop);
  const f=await fixture(t,undefined,(path,data)=>lanRequest(port,path,data));
  async function connect(uris,method='none'){
    const r=await f.register(uris,method);assert.equal(r.status,201,r.text);const client=r.json();
    const a=await f.authorize(client,uris[0]);assert.equal(a.status,200,a.text);
    const page=await f.login(a);assert.equal(page.status,200,page.text);
    const exchanged=await f.exchange(client,a,returned(page.text).searchParams.get('code'));assert.equal(exchanged.status,200,exchanged.text);
    const tokens=exchanged.json();return {client,tokens};
  }
  const google=await connect(redirects), secretGoogle=await connect(redirects,'client_secret_post');
  const chat=await connect([CALLBACK]), claude=await connect([CLAUDE_CALLBACK]), local=await connect(['http://localhost:49123/callback']);
  async function rpc(connection,method='tools/list',params={},extra={}){
    const r=await f.request('/mcp','POST',JSON.stringify({jsonrpc:'2.0',id:1,method,params}),
      {'Content-Type':'application/json',...(connection?{Authorization:'Bearer '+connection.tokens.access_token}:{}),...extra});
    return {status:r.status,...r.json()};
  }
  async function list(connection,extra){const value=await rpc(connection,'tools/list',{},extra);assert.equal(value.status,200);assert(!value.error,JSON.stringify(value));assert(!JSON.stringify(value).includes(hintsMetadataKey));return value.result.tools;}
  const baseline=await list(google);assert.equal(baseline.length,29);
  assert.equal(baseline.find(t=>t.name==='pc_get_gemini_settings').annotations.readOnlyHint,true);
  assert.equal(baseline.find(t=>t.name==='pc_set_gemini_settings').annotations.readOnlyHint,false);
  for(const connection of [secretGoogle,chat,claude,local]) assert.deepEqual(await list(connection),baseline);
  const annotations=baseline.map(({name,annotations})=>({name,annotations}));
  assert(annotations.every(row=>Object.keys(row.annotations).length===4));
  assert.equal((await rpc(google,'initialize',{protocolVersion:'2025-11-25',clientInfo:{name:'Gemini'},capabilities:{}})).result.capabilities.tools.listChanged,false);
  assert.equal((await rpc(null)).status,401);
  const status=await rpc(google,'tools/call',{name:'pc_get_status',arguments:{}});
  const hostname=JSON.parse(status.result.content[0].text).hostname;
  const request=(request_id,fields={})=>({expected_hostname:hostname,request_id,...fields});
  async function call(connection,name,args){
    const response=await rpc(connection,'tools/call',{name,arguments:args});
    assert.equal(response.status,200,JSON.stringify(response));assert(!response.error,JSON.stringify(response));
    return response.result;
  }
  async function settings(connection,name,args){const result=await call(connection,name,args);assert(!result.isError,JSON.stringify(result));return JSON.parse(result.content[0].text);}
  const getter='pc_get_gemini_settings',setter='pc_set_gemini_settings';
  const first=request('remote-first',{mode:'patch',read_only_hints:{pc_get_status:false,pc_write_file:true}});
  const applied=await settings(google,setter,first);
  assert.equal(applied.saved,true);assert.equal(applied.gemini_permission_updated,false);assert.equal(applied.schema_refresh_required,true);
  assert.equal(applied.tools.length,29);assert.equal(applied.tools.find(t=>t.tool_name===setter).editable,false);
  assert.equal((await list(google)).find(t=>t.name==='pc_get_status').annotations.readOnlyHint,false);
  const current=await settings(google,getter,{expected_hostname:hostname});
  assert.deepEqual(current.read_only_hints,first.read_only_hints);
  assert.equal((await settings(google,setter,first)).replayed,true);
  assert.equal((await settings(google,getter,request('remote-first'))).request_status,'saved');
  assert.equal((await settings(secretGoogle,getter,request('remote-first'))).request_status,'not_found');
  assert.equal((await call(google,setter,{...first,mode:'reset_all',read_only_hints:undefined})).structuredContent.error_code,'REQUEST_ID_CONFLICT');
  assert.equal((await call(google,setter,{...first,expected_hostname:'wrong-target'})).structuredContent.error_code,'TARGET_MISMATCH');
  for(const fields of [
    {mode:'patch'}, {mode:'reset_all',reset_tools:[]}, {mode:'patch',read_only_hints:{[setter]:true}},
    {mode:'patch',read_only_hints:{pc_read_file:1}}, {mode:'patch',read_only_hints:{pc_read_file:null}},
    {mode:'patch',read_only_hints:{pc_read_file:true,unknown:false}},
    {mode:'patch',reset_tools:['pc_read_file','pc_read_file']},
    {mode:'patch',reset_tools:['pc_read_file'],read_only_hints:{pc_read_file:true}},
    {mode:'reset_all',unexpected:'field'}, {mode:'patch',reset_tools:Array(65).fill('pc_read_file')},
    {mode:'patch',read_only_hints:Object.fromEntries(Array.from({length:65},(_,i)=>['tool'+i,true]))},
    {mode:'patch',read_only_hints:{['x'.repeat(16384)]:false}}
  ]){
    const before=readFileSync(join(directory,'settings.txt'),'utf8');
    const rejected=await call(google,setter,request('invalid',fields));
    assert.equal(rejected.isError,true);assert.equal(rejected.structuredContent.error_code,'INVALID_ARGUMENT');
    assert.equal(readFileSync(join(directory,'settings.txt'),'utf8'),before);
  }
  assert.equal((await rpc(null,'tools/call',{name:setter,arguments:request('unauthenticated',{mode:'reset_all'})})).status,401);
  const reset=await settings(google,setter,request('remote-reset-one',{mode:'patch',reset_tools:['pc_get_status']}));
  assert.deepEqual(reset.read_only_hints,{pc_write_file:true});
  const receipt=await settings(google,setter,first);assert.equal(receipt.result_is_current,false);
  assert.deepEqual((await settings(google,getter,{expected_hostname:hostname})).read_only_hints,{pc_write_file:true});
  await settings(google,setter,request('remote-reset-all',{mode:'reset_all'}));
  assert.deepEqual(await list(google),baseline);
  const overrides={pc_get_status:false,pc_write_file:true,pc_start_process:true,pc_open_file_transfer:false,pc_read_file:true};
  await save(overrides);
  const changed=await list(google);
  assert.deepEqual(await list(secretGoogle),changed);
  for(const original of baseline){
    const tool=changed.find(x=>x.name===original.name);
    if(!Object.hasOwn(overrides,tool.name)){assert.deepEqual(tool,original);continue;}
    assert.equal(tool.annotations.readOnlyHint,overrides[tool.name]);
    assert.equal(tool._meta['remotecontrol/defaultReadOnlyHint'],original.annotations.readOnlyHint);
    assert.equal(tool._meta['remotecontrol/readOnlyHintOverride'],true);
    assert.deepEqual(tool.inputSchema,original.inputSchema);assert.deepEqual(tool.securitySchemes,original.securitySchemes);
    for(const key of ['destructiveHint','idempotentHint','openWorldHint']) assert.equal(tool.annotations[key],original.annotations[key]);
    if(tool.annotations.readOnlyHint!==original.annotations.readOnlyHint) assert.match(tool.description,/Actual behavior, side effects and execution permissions are unchanged/);
    else assert.equal(tool.description,original.description);
  }
  for(const connection of [chat,claude,local]) assert.deepEqual(await list(connection,{'User-Agent':'Google Gemini',Origin:'https://chatgpt.com'}),baseline);
  // Even changing the stored display name cannot substitute for registered callbacks.
  const grant=await f.provider.verifyAccessToken(chat.tokens.access_token);grant.clientName='Google (Gemini)';
  await rpc(chat,'initialize',{protocolVersion:'2025-11-25',clientInfo:{name:'Gemini'},capabilities:{}});
  assert.deepEqual(await list(chat),baseline);
  const registered=await f.provider.clientsStore.getClient(google.client.client_id), savedUris=registered.redirect_uris;
  registered.redirect_uris=[redirects[0],CALLBACK];assert.deepEqual(await list(google),baseline);registered.redirect_uris=savedUris;
  assert.equal((await rpc(google,'tools/call',{name:'pc_start_process',arguments:{command:'throw "must never execute"'}})).result.structuredContent.error_code,'CAPABILITY_DISABLED');
  const before=readFileSync(join(directory,'settings.txt'),'utf8');
  for(const bad of ['{"pc_get_status":"true"}','{"pc_get_status":0}','{"pc_get_status":null}','{"no_such_tool":true}',' '.repeat(16384)+'{}']) await save(bad,false);
  assert.equal(readFileSync(join(directory,'settings.txt'),'utf8'),before);
  await stop();await start();await f.restart();
  assert.deepEqual(await list(google),changed);
  assert.equal((await settings(google,getter,request('remote-first'))).request_status,'saved');
  assert.equal((await settings(google,setter,first)).replayed,true);
  assert.deepEqual(await list(google),changed); // Restart and replay never roll back later UI saves.
  for(let i=0;i<10;i++){
    await Promise.all([save({pc_get_status:!!(i%2),pc_read_file:!!(i%2)}),
      ...Array.from({length:3},async()=>{const tools=await list(secretGoogle);const a=tools.find(x=>x.name==='pc_get_status');const b=tools.find(x=>x.name==='pc_read_file');
        // The first concurrent read may still observe the previously committed map.
        if(!tools.find(x=>x.name==='pc_write_file')._meta['remotecontrol/readOnlyHintOverride']) assert.equal(a.annotations.readOnlyHint,b.annotations.readOnlyHint);
      })]);
  }
  await save({});assert.deepEqual(await list(google),baseline);assert.deepEqual(await list(chat),baseline);
  const transfer=await rpc(google,'tools/call',{name:'pc_open_file_transfer',arguments:{}});
  assert.match(JSON.parse(transfer.result.content[0].text).url,/\/files\/open\?code=/);
  assert.deepEqual(fileTool,JSON.parse(readFileSync(new URL('../web-tools.json',import.meta.url),'utf8')));
});
