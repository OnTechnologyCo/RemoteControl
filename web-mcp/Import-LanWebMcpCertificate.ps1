param([Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256)
# 대상 PC에서 확인한 지문을 필수로 받아 다른 CA의 오등록을 막는다.
$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot 'RemoteControl-LAN-CA.crt'
if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $ExpectedSha256){throw '대상 PC에서 확인한 공개 CA SHA256과 다릅니다.'}
$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2($path)
if($cert.HasPrivateKey){throw '공개 인증서만 가져올 수 있습니다.'}
Import-Certificate -FilePath $path -CertStoreLocation Cert:\CurrentUser\Root|Select-Object Subject,Thumbprint,NotAfter
Write-Output '현재 Windows 사용자에 LAN CA를 등록했습니다. Node 클라이언트는 NODE_EXTRA_CA_CERTS에 이 인증서의 절대 경로를 설정하세요.'
