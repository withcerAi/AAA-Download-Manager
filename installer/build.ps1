param(
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$Compiler
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $root 'release/DownloadManager-0.5.7-preview'
& $Dotnet publish (Join-Path $root 'src/DownloadManager.Desktop') -c Release -r win-x64 --self-contained true -o $payload -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
& $Dotnet publish (Join-Path $root 'src/DownloadManager.BrowserHost') -c Release -r win-x64 --self-contained true -o (Join-Path $payload 'browser-host') -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Browser host publish failed' }
& $Compiler (Join-Path $PSScriptRoot 'DownloadManager.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup = Join-Path $root 'release/setup/AAA-Download-Manager-0.5.7-Setup-x64.exe'
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
($hash.Hash.ToLowerInvariant() + '  ' + (Split-Path $setup -Leaf)) | Set-Content -Encoding ascii ($setup + '.sha256')
