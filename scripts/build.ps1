<#
  The one way to build LaunchReel Studio.
  Output: artifacts\studio\LaunchReel Studio.exe  (the only build "Start Studio.cmd" ever launches)

  -IfStale   Rebuild only when the sources changed since the last build (used by Start Studio.cmd).
#>
param([switch]$IfStale)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $studioRoot 'artifacts\studio'
$exe = Join-Path $output 'LaunchReel Studio.exe'
$stamp = Join-Path $output 'build-info.json'

function Get-SourceHash {
    $files = @(
        Get-ChildItem -LiteralPath (Join-Path $studioRoot 'src\Studio.Host') -Recurse -File -Include *.cs,*.xaml,*.csproj,*.manifest |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
        Get-ChildItem -LiteralPath (Join-Path $studioRoot 'web') -File
        Get-ChildItem -LiteralPath (Join-Path $studioRoot 'renderers') -File
        Get-Item -LiteralPath (Join-Path $studioRoot 'package.json')
        Get-Item -LiteralPath (Join-Path $studioRoot 'scripts\build.mjs')
    ) | Sort-Object FullName
    $sha = [Security.Cryptography.SHA256]::Create()
    foreach ($file in $files) {
        $name = [Text.Encoding]::UTF8.GetBytes($file.FullName.Substring($studioRoot.Length).ToLowerInvariant())
        [void]$sha.TransformBlock($name, 0, $name.Length, $null, 0)
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        [void]$sha.TransformBlock($bytes, 0, $bytes.Length, $null, 0)
    }
    [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
    return [BitConverter]::ToString($sha.Hash).Replace('-', '')
}

function Get-RunningFrom([string]$folder) {
    $full = [IO.Path]::GetFullPath($folder).TrimEnd('\') + '\'
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase) }
}

$hash = Get-SourceHash
if ($IfStale -and (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $stamp)) {
    $info = Get-Content -LiteralPath $stamp -Raw | ConvertFrom-Json
    if ($info.sourceHash -eq $hash) { Write-Host 'Studio is up to date.'; exit 0 }
}
if (Get-RunningFrom $output) { throw 'LaunchReel Studio is open. Close it, then build again.' }

Push-Location -LiteralPath $studioRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $studioRoot 'node_modules'))) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'Installing the editor dependencies failed.' }
    }
    npm run check
    if ($LASTEXITCODE -ne 0) { throw 'TypeScript check failed.' }
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'Editor tests failed.' }
    npm run build:web
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    dotnet build src/Studio.Host/Studio.Host.csproj -c Release -p:NuGetAudit=false -o $output -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $version = (Get-Content -LiteralPath (Join-Path $studioRoot 'package.json') -Raw | ConvertFrom-Json).version
    [ordered]@{ version = $version; built = (Get-Date).ToString('o'); sourceHash = $hash } | ConvertTo-Json | Set-Content -LiteralPath $stamp -Encoding utf8

    # Earlier prototypes were built to other folders. Remove them so nobody opens stale software by accident.
    foreach ($old in @('src\Studio.Host\bin\Release\net10.0-windows10.0.19041.0', 'artifacts\app-view')) {
        $path = Join-Path $studioRoot $old
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if (Get-RunningFrom $path) { Write-Warning "An old Studio build is still open from $path. Close it; the next build removes that folder." }
        else { Remove-Item -LiteralPath $path -Recurse -Force; Write-Host "Removed old build: $path" }
    }
    # Builds before 0.3 were called FlyShelf Ad Studio.
    Get-ChildItem -LiteralPath $output -Filter 'FlyShelf Ad Studio.*' -File | Remove-Item -Force
    Write-Host "Ready: $exe"
} finally { Pop-Location }
