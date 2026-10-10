# 외부 HTTPS가 실제 개통된 후에만 연결 패키지를 만든다. 가짜 app ID를 만들지 않는다.

[CmdletBinding()]

param([Parameter(Mandatory=$true)][string]$PublicUrl,[Parameter(Mandatory=$true)][string]$Destination)

$ErrorActionPreference='Stop'

$uri=[Uri]$PublicUrl

if($uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo){throw 'HTTPS 서버 주소를 지정하세요.'}

$origin=$uri.GetLeftPart([UriPartial]::Authority)

$metadata=Invoke-RestMethod ($origin+'/.well-known/oauth-protected-resource') -TimeoutSec 15

if($metadata.resource -cne ($origin+'/mcp') -or $metadata.authorization_servers[0] -cne ($origin+'/')){throw 'MCP 인증 정보가 지정한 서버와 일치하지 않습니다.'}

$auth=Invoke-RestMethod ($origin+'/.well-known/oauth-authorization-server') -TimeoutSec 15

if($auth.issuer -cne ($origin+'/') -or -not $auth.authorization_response_iss_parameter_supported -or $auth.code_challenge_methods_supported -notcontains 'S256'){throw 'OAuth 인증 정보를 확인할 수 없습니다.'}

if(Test-Path -LiteralPath $Destination){throw '기존 패키지를 덮어쓰지 않습니다. 새 ZIP 경로를 지정하세요.'}

Add-Type -AssemblyName System.IO.Compression

$file=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew)

$zip=New-Object IO.Compression.ZipArchive($file,[IO.Compression.ZipArchiveMode]::Create,$false)

try {

    $files=@{

        'plugin.json'=(@{'$schema'='https://agent-plugins.org/schemas/1.0.0/plugin.schema.json';name='remotecontrol-web';version='1.8.10';description='Control an authorized RemoteControl PC through HTTPS and OAuth.';author=@{name='ontech'}} | ConvertTo-Json -Depth 8)

        'mcp.json'=(@{'$schema'='https://agent-plugins.org/schemas/1.0.0/mcp.schema.json';mcpServers=@{'remotecontrol-web'=@{type='streamable-http';url=($origin+'/mcp')}}} | ConvertTo-Json -Depth 8)

        'skills/remotecontrol-web/SKILL.md'="---`nname: remotecontrol-web`ndescription: Inspect and manage the connected RemoteControl PC over HTTPS MCP.`n---`nCall pc_get_status first and verify the target hostname before changes. File paths refer to that PC. Commands use the remote agent's Windows rights. Keep output concise. Never retry an execution after an uncertain transport failure. Reconnect through OAuth when authentication expires. Use pc_open_file_transfer for browser file/folder uploads and streamed downloads. Direct binary transfers use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk with SHA-256. Local and external clients use the same HTTPS /mcp URL without installing a local connector.`n"

    }

    foreach($name in $files.Keys){$entry=$zip.CreateEntry('remotecontrol-web/'+$name);$stream=$entry.Open();$bytes=[Text.Encoding]::UTF8.GetBytes($files[$name]);try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}}

}finally{$zip.Dispose();$file.Dispose()}

"검증된 웹 MCP 연결 패키지: $Destination"
