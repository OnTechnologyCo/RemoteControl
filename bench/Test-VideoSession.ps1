[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$offer=Join-Path $root 'artifacts\video-deps\fixture-offer.sdp'
$previous=$env:RC_VIDEO_TEST_OFFER
try {
    $env:RC_VIDEO_TEST_OFFER=$offer
    Push-Location (Join-Path $root 'video-helper')
    try { & go test -mod=readonly -count=1 -run TestHelperEOFClosesBeforeICEConnect .; if($LASTEXITCODE -ne 0){throw 'Offer fixture failed'} } finally {Pop-Location}
} finally {$env:RC_VIDEO_TEST_OFFER=$previous}
$exe=Join-Path $root 'artifacts\video-deps\VideoSessionTest.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /langversion:5 "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'VideoSessionTest.cs')
if($LASTEXITCODE -ne 0){throw 'Video session test compilation failed'}
try {
    & $exe (Join-Path $root 'dist\RemoteControl.exe') $offer
    if($LASTEXITCODE -ne 0){throw 'Embedded video session test failed'}
} finally { Remove-Item -LiteralPath $offer -ErrorAction SilentlyContinue }
