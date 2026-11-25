param([ValidateSet('linux-x64','linux-arm64')][string]$Runtime='linux-x64', [switch]$Archive)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force staging | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    node tests/interface-check.cjs
    if($LASTEXITCODE){throw 'Interface validation failed'}
    & ./BuildInterface.ps1 -Archive (Join-Path $PSScriptRoot 'staging/interface.zip')
    $destination=Join-Path $PSScriptRoot "publish-$Runtime"
    dotnet publish server/CtrlServer.csproj -c Release -r $Runtime --self-contained true -o $destination
    if($LASTEXITCODE){throw 'Server publish failed'}
    $verifier=Join-Path $destination 'verifier'
    New-Item -ItemType Directory -Force $verifier | Out-Null
    Get-ChildItem verifier -File | Copy-Item -Destination $verifier -Force
    npm --prefix $verifier ci --omit=dev --ignore-scripts --no-audit --no-fund
    if($LASTEXITCODE){throw 'Verifier dependencies failed'}
    if ($Archive) {
        Copy-Item -LiteralPath '../backend/deploy/ctrl-messenger.service' -Destination $destination -ErrorAction Stop
        $project=[xml](Get-Content -Raw -LiteralPath 'server/CtrlServer.csproj')
        $version=@($project.Project.PropertyGroup.Version | Where-Object {$_})[0]
        $release=Join-Path $PSScriptRoot "../backend/Ctrl-Server-$version-$Runtime.tar.gz"
        & tar.exe -czf $release --exclude '*.pdb' -C $destination .
        if($LASTEXITCODE){throw 'Server archive failed'}
    }
} finally {Pop-Location}

