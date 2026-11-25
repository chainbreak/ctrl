$ErrorActionPreference='Stop'
$listen='http://127.0.0.1:48734'
if(Get-NetTCPConnection -State Listen -LocalPort 48734 -ErrorAction SilentlyContinue){throw 'Default server port is already in use.'}
$run=Join-Path ([IO.Path]::GetTempPath()) ('ctrl-embedded-'+[guid]::NewGuid().ToString('N'))
$profile=Join-Path $run 'client'
$env:CTRL_CLIENT_DATA=$profile
$env:CTRL_DATA=Join-Path $run 'db'
$env:CTRL_LISTEN=$listen
$binary=(Resolve-Path (Join-Path $PSScriptRoot '../publish-client/ctrl.exe')).Path
$client=Start-Process -FilePath $binary -WindowStyle Hidden -PassThru
try {
    $verified=$false
    for($i=0;$i -lt 60;$i++){
        if($client.HasExited){throw 'Desktop exited unexpectedly'}
        try {$health=Invoke-RestMethod -Uri ($listen+'/health') -TimeoutSec 1 -ErrorAction Stop; if($health.service -eq 'ctrl' -and $health.version -eq '0.1.2'){$verified=$true;break}} catch{}
        Start-Sleep -Milliseconds 300
    }
    if(!$verified){throw 'Embedded server startup failed'}
    $site=Invoke-WebRequest -UseBasicParsing ($listen+'/')
    if(!$site.Content.Contains('ctrl-runtime')){throw 'Embedded client assets missing'}
    'PASS standalone EXE extracted and started its embedded service'
} finally {
    $children=Get-CimInstance Win32_Process -Filter "Name='ctrl-server.exe'" | Where-Object {$_.ParentProcessId -eq $client.Id -and $_.ExecutablePath.StartsWith(($profile+'\'),[StringComparison]::OrdinalIgnoreCase)}
    if(!$client.HasExited){$client.Kill();$client.WaitForExit()}
    foreach($child in $children){Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue}
}

