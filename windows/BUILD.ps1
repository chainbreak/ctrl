$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    node tests/interface-check.cjs
    if ($LASTEXITCODE) { throw 'Interface validation failed' }
    npm --prefix verifier ci --omit=dev --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'Verifier dependencies failed' }
    $runtime = (Get-Command node.exe).Source
    New-Item -ItemType Directory -Force -Path staging | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    & ./BuildInterface.ps1 -Archive (Join-Path $PSScriptRoot "staging/interface.zip")
    $archiveFile = Join-Path $PSScriptRoot 'staging/ton-runtime.zip'
    $file = [IO.File]::Create($archiveFile)
    $archive = [IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$runtime,'node.exe') | Out-Null
        $root=(Resolve-Path verifier).Path
        foreach($source in Get-ChildItem $root -Recurse -File -Force) {
            $entry=$source.FullName.Substring($root.Length+1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source.FullName,$entry) | Out-Null
        }
    } finally { $archive.Dispose(); $file.Dispose() }
    dotnet publish server/CtrlServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o publish-server
    if ($LASTEXITCODE) { throw 'Server build failed' }
    dotnet publish desktop/Ctrl.csproj -c Release -o publish-client
    if ($LASTEXITCODE) { throw 'Client build failed' }
} finally { Pop-Location }

