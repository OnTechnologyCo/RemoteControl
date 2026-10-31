// Uses the official MCP SDK already installed in the reference project. No live Remote settings.
const fs=require('fs'),path=require('path'),os=require('os'),assert=require('assert/strict');
const {spawn}=require('child_process'),{pathToFileURL}=require('url'),crypto=require('crypto');
const root=path.resolve(__dirname,'../artifacts/mcp-test');
const fixture=path.join(root,'McpFixture.exe');
const work=fs.mkdtempSync(path.join(root,'case-'));const remote=path.join(work,'remote');fs.mkdirSync(remote);
const sdk=process.env.RC_MCP_SDK||'D:/OnMakeSrc/MCP_LocalPC/node_modules/@modelcontextprotocol/sdk/dist/esm';
let checks=0,server,clients=[];
const check=(v,label)=>{assert(v,label);console.log('PASS '+(++checks)+' '+label);};
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const hash=data=>crypto.createHash('sha256').update(data).digest('hex');
(async()=>{
 const {Client}=await import(pathToFileURL(path.join(sdk,'client/index.js')));
 const {StdioClientTransport}=await import(pathToFileURL(path.join(sdk,'client/stdio.js')));
 const {StreamableHTTPClientTransport}=await import(pathToFileURL(path.join(sdk,'client/streamableHttp.js')));
 server=spawn(fixture,[remote],{stdio:['pipe','pipe','pipe'],windowsHide:true});let audit='';server.stderr.on('data',b=>audit+=b);
 const port=await new Promise((resolve,reject)=>{let text='';server.stdout.on('data',b=>{text+=b;if(text.includes('\n'))resolve(Number(text.trim().split('\n')[0]));});server.on('exit',c=>reject(Error('fixture exit '+c)));setTimeout(()=>reject(Error('fixture startup timeout '+audit)),10000).unref();});
 const base='http://127.0.0.1:'+port;
 check(Number.isInteger(port)&&port>0,'isolated LAN MCP listener');
 let r=await fetch(base+'/mcp',{method:'POST',body:'{}'});check(r.status===401,'MCP rejects unauthenticated calls');
 r=await fetch(base+'/login',{method:'POST',body:JSON.stringify({user:'fixture',password:'wrong'})});check(r.status===401,'wrong Remote password rejected');
 r=await fetch(base+'/login',{method:'POST',body:JSON.stringify({user:'fixture',password:'fixture-only'})});assert.equal(r.status,200);let cookie=r.headers.get('set-cookie').split(';')[0];
 check(cookie.startsWith('rcmcpid='),'Remote ID/PW session without external authentication');
 r=await fetch(base+'/mcp',{method:'POST',headers:{Cookie:cookie,Origin:'https://example.org'},body:'{}'});check(r.status===403,'browser Origin cannot invoke LAN command API');
 r=await fetch(base+'/mcp',{method:'POST',headers:{Cookie:cookie},body:'null'});check((await r.json()).error.code===-32600,'null JSON-RPC handled');
 const http=new Client({name:'protocol-test',version:'1'},{capabilities:{}});clients.push(http);
 await http.connect(new StreamableHTTPClientTransport(new URL(base+'/mcp'),{requestInit:{headers:{Cookie:cookie}}}));
 check((await http.listTools()).tools.some(t=>t.name==='pc_start_process'),'official SDK Streamable HTTP initialize/list');
 const init=await (await fetch(base+'/mcp',{method:'POST',headers:{Cookie:cookie},body:JSON.stringify({jsonrpc:'2.0',id:77,method:'initialize',params:{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'workspace-test',version:'1'}}})})).json();
 check(init.result.instructions.includes('not access restrictions')&&init.result.instructions.includes('NOT_FOUND'),'LAN initialize explains workspace and actual error semantics');
 await http.close();clients=clients.filter(c=>c!==http);
 const exe=process.env.RC_MCP_CLIENT_EXE||fixture;
 async function connect(){const c=new Client({name:'claude-stdio-protocol-test',version:'1'},{capabilities:{}});clients.push(c);await c.connect(new StdioClientTransport({command:exe,args:['--mcp-client'],env:{...process.env,RC_MCP_HOST:'127.0.0.1',RC_MCP_PORT:String(port),RC_MCP_USER:'fixture',RC_MCP_PASSWORD:'fixture-only'},stderr:'pipe'}));return c;}
 let client=await connect();const tools=(await client.listTools()).tools;
 check(['pc_get_status','pc_capture_screen','pc_upload_file','pc_download_file','pc_read_event_log'].every(n=>tools.some(t=>t.name===n)),'stdio bridge advertises remote and local transfer tools');
 async function raw(name,args={}){return client.callTool({name,arguments:args});}
 async function call(name,args={}){const value=await raw(name,args);assert(!value.isError,JSON.stringify(value));return JSON.parse(value.content[0].text);}
 async function configure(command){await new Promise(resolve=>{server.stdout.once('data',resolve);server.stdin.write(command+'\n');});}
 let st=await call('pc_get_status');check(st.hostname===os.hostname()&&st.pid===server.pid,'remote target identity verified through stdio');
 check(st.workspace_directories.length===1&&st.workspace_directories[0]===remote&&st.default_working_directory===remote&&st.workspaces_restrict_access===false&&!('allowed_roots' in st),'status exposes workspaces without an access allowlist');
 for(let i=0;i<3;i++){await client.close();clients=clients.filter(c=>c!==client);client=await connect();await call('pc_get_status');}
 check((await call('pc_list_sessions')).sessions.length===0,'reconnects do not spawn command jobs');
 const outside=path.join(work,'outside');const folder=path.join(outside,'한글 폴더');await call('pc_create_directory',{path:folder});
 const text=path.join(folder,'진단.txt');let info=await call('pc_write_file',{path:text,content:'첫째\nsecond\n'});
 let read=await call('pc_read_file',{path:text});check(read.content.includes('첫째')&&read.sha256===info.sha256,'UTF-8 file write/read and hash');
 check((await raw('pc_write_file',{path:text,content:'wrong'})).isError,'overwrite requires current SHA-256');
 await call('pc_edit_file',{path:text,old_text:'second',new_text:'수정',expected_sha256:info.sha256});
 check(fs.readFileSync(text,'utf8').includes('수정')&&fs.readdirSync(folder).some(n=>n.includes('mcp-backup')),'edit verified and original backup retained');
 check((await raw('pc_write_file',{path:path.join(folder,'empty.txt')})).isError,'missing text content rejected');
 check((await call('pc_list_directory',{path:outside})).entries.some(e=>e.name==='한글 폴더'),'directory listing outside configured workspace succeeds');
 check((await call('pc_read_file',{path:text})).content.includes('수정'),'existing file outside workspace is readable');
 const missing=await raw('pc_read_file',{path:path.join(outside,'missing.txt')});check(missing.isError&&missing.structuredContent.error_code==='NOT_FOUND','missing file is not reported as permission denial');
 const missingDownload=await raw('pc_download_file',{remote_path:path.join(outside,'missing.txt'),local_path:path.join(work,'missing-download.txt')});check(missingDownload.isError&&missingDownload.structuredContent.error_code==='NOT_FOUND','stdio download preserves the server error without inferring workspace permissions');
 const invalid=await raw('pc_read_file',{path:'relative.txt'});check(invalid.isError&&invalid.structuredContent.error_code==='INVALID_ARGUMENT','relative path is a format error, not permission denial');
 fs.mkdirSync(outside,{recursive:true});fs.writeFileSync(path.join(outside,'secret.txt'),'outside');fs.symlinkSync(outside,path.join(remote,'junction'),'junction');
 const linked=await raw('pc_read_file',{path:path.join(remote,'junction','secret.txt')});check(linked.isError&&linked.structuredContent.error_code==='INVALID_ARGUMENT','junction requires its direct destination path rather than a permission claim');
 const local=path.join(work,'compiled.bin'),dest=path.join(folder,'compiled.bin');const bytes=crypto.randomBytes(4*1024*1024);fs.writeFileSync(local,bytes);
 info=await call('pc_upload_file',{local_path:local,remote_path:dest});check(info.sha256===hash(bytes)&&fs.readFileSync(dest).equals(bytes),'4MiB streamed binary upload and SHA-256 verified');
 check((await raw('pc_upload_file',{local_path:local,remote_path:dest})).isError,'binary overwrite without hash rejected');
 await call('pc_upload_file',{local_path:local,remote_path:dest,expected_sha256:info.sha256});
 const downloaded=path.join(work,'downloaded.bin');await call('pc_download_file',{remote_path:dest,local_path:downloaded});check(fs.readFileSync(downloaded).equals(bytes),'streamed download verified');
 check((await raw('pc_download_file',{remote_path:dest,local_path:downloaded})).isError,'existing local download never overwritten');
 check((await call('pc_search_files',{path:outside,query:'진단'})).paths.includes(text),'bounded recursive file search');
 const disabledScreen=await raw('pc_capture_screen');check(disabledScreen.isError&&disabledScreen.structuredContent.error_code==='CAPABILITY_DISABLED','disabled screen is distinguished from filesystem permission denial');
 await configure('screen');let screen=await raw('pc_capture_screen',{max_width:640});
 check(!screen.isError&&screen.content.some(c=>c.type==='image'&&c.mimeType==='image/jpeg'&&Buffer.from(c.data,'base64').length>100),'MCP image response from synthetic capture');
 await configure('commands-off');check((await raw('pc_start_process',{command:'exit 0'})).isError,'commands disabled immediately by setting');await configure('commands-on');
 await configure('password-change');check((await call('pc_get_status')).pid===server.pid,'expired session relogs in once with Remote ID/PW');
 let job=await call('pc_start_process',{command:'(Get-Location).Path',timeout_ms:10000});
 check(!job.running&&job.exit_code===0&&job.output.includes(remote),'omitted cwd uses the first workspace');
 job=await call('pc_start_process',{command:"Write-Output 'MCP 한글 OK'; exit 7",cwd:folder,timeout_ms:10000});
 check(!job.running&&job.exit_code===7&&job.output.includes('MCP 한글 OK'),'explicit cwd outside workspace executes with owned output and exit code');
 const denied=path.join(folder,'os-denied.txt');await call('pc_write_file',{path:denied,content:'synthetic ACL fixture'});
 const psquote=v=>"'"+v.replaceAll("'","''")+"'";
 const aclBase="$path="+psquote(denied)+";$acl=Get-Acl -LiteralPath $path;$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,[Security.AccessControl.FileSystemRights]::ReadData,[Security.AccessControl.AccessControlType]::Deny);";
 try {
   job=await call('pc_start_process',{command:aclBase+"[void]$acl.AddAccessRule($rule);Set-Acl -LiteralPath $path -AclObject $acl",cwd:folder,timeout_ms:10000});assert.equal(job.exit_code,0,job.output);
   const blocked=await raw('pc_read_file',{path:denied});check(blocked.isError&&blocked.structuredContent.error_code==='ACCESS_DENIED','actual Windows ACL denial remains enforced outside workspaces');
 } finally {
   job=await call('pc_start_process',{command:aclBase+"$acl.RemoveAccessRuleSpecific($rule);Set-Acl -LiteralPath $path -AclObject $acl",cwd:folder,timeout_ms:10000});assert.equal(job.exit_code,0,job.output);
 }
 job=await call('pc_start_process',{command:"Write-Output 'early output'; Start-Sleep -Seconds 120",cwd:folder,timeout_ms:2000});
 for(let i=0;i<40&&job.running&&!job.output.includes('early output');i++){await delay(200);job=await call('pc_read_process_output',{session_id:job.session_id});}
 check(job.running&&job.output.includes('early output'),'output available while command is still running');await call('pc_stop_process',{session_id:job.session_id});
 job=await call('pc_start_process',{command:"[Console]::Write(('x' * 200000))",cwd:folder,timeout_ms:10000});
 let output=await call('pc_read_process_output',{session_id:job.session_id,offset:0,limit:65536});check(output.truncated&&output.output_start>0&&output.output.length<=65536,'long output bounded even without line breaks');
 job=await call('pc_start_process',{command:'Start-Sleep -Seconds 120',cwd:folder,timeout_ms:0});
 let stop=await call('pc_stop_process',{session_id:job.session_id});check(!stop.running,'stop only selected MCP process job');
 const probe=path.join(folder,'DeployedProbe.exe'),marker=path.join(folder,'executed.txt');
 await call('pc_upload_file',{local_path:fixture,remote_path:probe});job=await call('pc_launch_program',{path:probe,arguments:'--probe "'+marker+'"',visible:false});
 for(let i=0;i<50&&job.running;i++){await delay(200);job=await call('pc_read_process_output',{session_id:job.session_id});}
 check(!job.running&&job.exit_code===23&&fs.readFileSync(marker,'utf8')==='remote-probe-v1','compiled executable upload, launch and remote result verification');
 job=await call('pc_start_process',{command:"$p=Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120' -WindowStyle Hidden -PassThru; Write-Output $p.Id; Start-Sleep -Seconds 120",cwd:folder,timeout_ms:2000});
 for(let i=0;i<40&&job.running&&!/^\d+\r?$/m.test(job.output);i++){await delay(200);job=await call('pc_read_process_output',{session_id:job.session_id});}
 const pidLine=job.output.split(/\r?\n/).find(line=>/^\d+$/.test(line));const childPid=Number(pidLine);assert(childPid>0,JSON.stringify(job));check(childPid>0,'owned child process started');
 await call('pc_stop_process',{session_id:job.session_id});await delay(200);
 check(!(await call('pc_list_processes')).processes.some(p=>p.pid===childPid),'job stop removes descendants without killing by name');
 r=await fetch(base+'/login',{method:'POST',body:JSON.stringify({user:'fixture',password:'fixture-only'})});cookie=r.headers.get('set-cookie').split(';')[0];
 r=await fetch(base+'/mcp',{method:'POST',headers:{Cookie:cookie},body:JSON.stringify({jsonrpc:'2.0',method:'tools/call',params:{name:'pc_write_file',arguments:{path:path.join(folder,'invalid.txt'),content:'bad'}}})});
 check((await r.json()).error.code===-32600&&!fs.existsSync(path.join(folder,'invalid.txt')),'mutations require JSON-RPC request ID');
 check(!audit.includes('fixture-only'),'audit excludes login password');
 const claude=process.env.RC_CLAUDE_EXE||path.join(process.env.APPDATA,'npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe');
 if(fs.existsSync(claude)){
   const config=path.join(work,'claude-isolated');fs.mkdirSync(config);
   const env={...process.env,CLAUDE_CONFIG_DIR:config,RC_MCP_HOST:'127.0.0.1',RC_MCP_PORT:String(port),RC_MCP_USER:'fixture',RC_MCP_PASSWORD:'fixture-only'};
   async function run(args){return new Promise((resolve,reject)=>{const p=spawn(claude,args,{cwd:work,env,windowsHide:true,stdio:['ignore','pipe','pipe']});let out='';p.stdout.on('data',b=>out+=b);p.stderr.on('data',b=>out+=b);const timeout=setTimeout(()=>{p.kill();reject(Error('Claude health timeout'));},30000);p.on('error',reject);p.on('exit',code=>{clearTimeout(timeout);code===0?resolve(out):reject(Error('Claude '+code+': '+out));});});}
   await run(['mcp','add','--scope','user','remotecontrol-test','--',exe,'--mcp-client']);
   const health=await run(['mcp','list']);check(/remotecontrol-test/.test(health)&&/Connected/.test(health),'actual Claude Code MCP health connection (isolated config, no model request)');
 }
 console.log('MCP integration: '+checks+' checks passed; '+tools.length+' tools; client='+path.basename(exe));
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(async()=>{for(const c of clients)try{await c.close();}catch{}if(server&&!server.killed){server.stdin.end('\n');setTimeout(()=>{if(server.exitCode===null)server.kill();},3000).unref();}});
