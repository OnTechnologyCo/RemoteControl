#requires -Version 5.1
[CmdletBinding()]
param(
    [string[]]$Paths = @('D:\Src\원격제어\dist\RemoteControl_Setup.exe', 'D:\Src\원격제어\dist\RemoteControl.exe', 'C:\Program Files\RemoteControl\RemoteControl.exe'),
    [datetime]$Since = (Get-Date).AddDays(-14),
    [string]$OutputDirectory = (Join-Path (Get-Location) ('RemoteControl-DefenderEvidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)))
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new evidence directory; existing evidence is never overwritten.' }
$dir = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
# 경로·사용자 이름이 담긴 진단 결과를 다른 사용자에게 공개하지 않는다.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl.SetOwner($owner)
foreach ($sid in @($owner.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique) {
    $identity = New-Object Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
Set-Acl -LiteralPath $dir -AclObject $acl
$errorsFound = New-Object Collections.Generic.List[object]
function Save-Json([string]$Name, $Value) {
    ConvertTo-Json -InputObject $Value -Depth 10 | Set-Content -LiteralPath (Join-Path $dir $Name) -Encoding UTF8
}
function Record-Error([string]$Stage, $Failure) {
    $errorsFound.Add([ordered]@{Stage=$Stage;Error=$Failure.Exception.Message;Id=$Failure.FullyQualifiedErrorId})
}
Save-Json 'context.json' ([ordered]@{Computer=$env:COMPUTERNAME;CollectedAtUtc=[DateTime]::UtcNow.ToString('o');SinceUtc=$Since.ToUniversalTime().ToString('o');Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name;ReadOnlyCollection=$true})
try { Save-Json 'defender-status.json' (Get-MpComputerStatus | Select-Object AMServiceEnabled,AntivirusEnabled,RealTimeProtectionEnabled,AMRunningMode,IsTamperProtected,AMProductVersion,AMEngineVersion,AntivirusSignatureVersion,AntivirusSignatureLastUpdated) }
catch { Record-Error 'DefenderStatus' $_ }
try { Save-Json 'threats.json' @(Get-MpThreat | Select-Object ThreatID,ThreatName,IsActive,DidThreatExecute,Resources) }
catch { Record-Error 'Threats' $_ }
$detections = @()
try {
    $detections = @(Get-MpThreatDetection)
    Save-Json 'detections.json' @($detections | Select-Object ThreatID,DetectionID,InitialDetectionTime,LastThreatStatusChangeTime,RemediationTime,ActionSuccess,ThreatStatusID,CurrentThreatExecutionStatusID,Resources)
} catch { Record-Error 'Detections' $_ }
try {
    $events = @(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Windows Defender/Operational';Id=1116,1117,1118,1119;StartTime=$Since} -MaxEvents 10000 -ErrorAction Stop)
    Save-Json 'events.json' @($events | Select-Object TimeCreated,Id,RecordId,Message)
    [IO.File]::WriteAllText((Join-Path $dir 'events.xml'), '<Events>' + (($events | ForEach-Object {$_.ToXml()}) -join "`r`n") + '</Events>', [Text.Encoding]::UTF8)
    if ($events.Count -eq 10000) { $errorsFound.Add(@{Stage='Events';Error='10000 event limit reached; narrow Since.'}) }
} catch {
    if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { Save-Json 'events.json' @() }
    else { Record-Error 'Events' $_ }
}
try {
    $platform = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    $mp = @(Get-ChildItem -LiteralPath $platform -Filter MpCmdRun.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1)
    if ($mp.Count) { $mpPath = $mp[0].FullName } else { $mpPath = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe' }
    # -ListAll은 목록 조회다. -Name/-FilePath/-Path로 격리 파일을 복원하지 않는다.
    & $mpPath -Restore -ListAll 2>&1 | Out-File -LiteralPath (Join-Path $dir 'quarantine-list.txt') -Encoding UTF8
    Save-Json 'quarantine-command.json' @{Command='MpCmdRun.exe -Restore -ListAll';ExitCode=$LASTEXITCODE;Path=$mpPath}
} catch { Record-Error 'QuarantineList' $_ }
# 탐지 리소스가 단일 로컬 파일일 때만 더한다. 다운로드·격리 복원·실행은 하지 않는다.
$targets = @($Paths)
foreach ($detection in $detections) {
    foreach ($resource in $detection.Resources) {
        if ($resource -match '^file:_?([A-Za-z]:\\[^|]+)$') { $targets += $Matches[1] }
    }
}
$files = @()
foreach ($path in $targets | Select-Object -Unique) {
    try {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $files += @{Path=$path;Exists=$false;Note='Missing or quarantined; no hash inferred'};continue }
        $file = Get-Item -LiteralPath $path -Force
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point target skipped' }
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $zoneId = $null
        try { $zoneId = Get-Content -LiteralPath $path -Stream Zone.Identifier -ErrorAction Stop | Where-Object {$_ -match '^ZoneId='} } catch { }
        $files += [ordered]@{Path=$file.FullName;Exists=$true;Length=$file.Length;CreationTimeUtc=$file.CreationTimeUtc.ToString('o');LastWriteTimeUtc=$file.LastWriteTimeUtc.ToString('o');SHA256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;Signature=$signature.Status.ToString();SignatureMessage=$signature.StatusMessage;Signer=$signature.SignerCertificate.Subject;SignerThumbprint=$signature.SignerCertificate.Thumbprint;TimestampSigner=$signature.TimeStamperCertificate.Subject;FileVersion=$file.VersionInfo.FileVersion;ZoneId=$zoneId}
    } catch { Record-Error ('File: '+$path) $_ }
}
Save-Json 'files.json' $files
Save-Json 'errors.json' @($errorsFound.ToArray())
@'
Read-only evidence collection. No malware sample is copied, restored, run or uploaded.
No exclusions, security preferences, application settings or credentials are changed.
CreationTimeUtc/LastWriteTimeUtc are filesystem metadata, not proof of compilation time.
Missing history is not evidence that a different PC/file was clean.
Review files.json, detections.json, events.json, quarantine-list.txt and errors.json together.
Paths and event messages can contain private information. Review before sharing.
'@ | Set-Content -LiteralPath (Join-Path $dir 'READ-ME.txt') -Encoding UTF8
$hashes = @(Get-ChildItem -LiteralPath $dir -File | ForEach-Object {[ordered]@{Name=$_.Name;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
Save-Json 'evidence-hashes.json' $hashes
Write-Output ('Evidence: '+$dir)
Write-Output ('Collected files: '+$files.Count+'; collection errors: '+$errorsFound.Count)
