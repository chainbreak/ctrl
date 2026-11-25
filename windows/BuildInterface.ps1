param([string]$Archive)
$source=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$file=[IO.File]::Create($Archive)
$zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Create)
try {
    $entry=$zip.CreateEntry('index.html')
    $html=[IO.File]::ReadAllText((Join-Path $source 'test.html')).Replace('<head>','<head><meta name="ctrl-runtime" content="desktop">')
    $writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
    try {$writer.Write($html)} finally {$writer.Dispose()}
    foreach($asset in Get-ChildItem (Join-Path $source 'assets') -File) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$asset.FullName,('assets/'+$asset.Name))|Out-Null
    }
    foreach($asset in Get-ChildItem (Join-Path $source 'assets/gifts') -Recurse -File | Where-Object { $_.Extension -ne '.md' }) {
        $relative=$asset.FullName.Substring($source.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$asset.FullName,$relative)|Out-Null
    }
    foreach($name in 'tonconnect.js','ton-icon.png','TON-Connect-LICENSE.txt'){
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $PSScriptRoot "client/$name"),$name)|Out-Null
    }
} finally {$zip.Dispose();$file.Dispose()}

