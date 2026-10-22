# Publish a new CodeMaster Windows client release.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File CodeMaster.Vue/scripts/publish-client.ps1 [-Version 1.0.6]
#
# Prerequisites:
#   - Updater signing key pair at .tauri/codemaster-updater.key (.key.pub committed; private key stays local)
#   - The key password (default: codemaster-updater-2026) or set TAURI_SIGNING_PRIVATE_KEY_PASSWORD
#
# What it does:
#   1. Bumps package.json / Cargo.toml / tauri.conf.json versions (unless -SkipVersionBump)
#   2. Builds the client with `npm run tauri:build` (frontend + Rust + NSIS)
#   3. Copies the signed installer to wwwroot/downloads/client/windows/CodeMaster_v{version}.exe
#   4. Copies the updater signature to CodeMaster_v{version}.exe.sig
#   5. Prints the updater manifest URL for verification
param(
  [string]$Version = '',
  [switch]$SkipVersionBump
)

$ErrorActionPreference = 'Stop'
$vueRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$repoRoot = Resolve-Path (Join-Path $vueRoot '..')
$webApiRoot = Join-Path $repoRoot 'CodeMaster.WebApi'
$keyPath = Join-Path $repoRoot '.tauri\codemaster-updater.key'
$downloadDir = Join-Path $webApiRoot 'wwwroot\downloads\client\windows'

if (-not $env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD) {
  $env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD = 'codemaster-updater-2026'
}
if (-not $env:TAURI_SIGNING_PRIVATE_KEY) {
  if (-not (Test-Path -LiteralPath $keyPath)) {
    throw "Updater signing key not found: $keyPath`nGenerate it with: npx tauri signer generate -w $keyPath"
  }
  $env:TAURI_SIGNING_PRIVATE_KEY = Get-Content -LiteralPath $keyPath -Raw
}

if (-not $SkipVersionBump) {
  if (-not $Version) {
    $conf = Get-Content (Join-Path $vueRoot 'src-tauri\tauri.conf.json') -Raw | ConvertFrom-Json
    $Version = $conf.version
    Write-Host "Using existing version $Version from tauri.conf.json" -ForegroundColor Cyan
  }
  node (Join-Path $vueRoot 'scripts\set-tauri-release-version.mjs') $Version
}

$conf = Get-Content (Join-Path $vueRoot 'src-tauri\tauri.conf.json') -Raw | ConvertFrom-Json
$Version = $conf.version
Write-Host "Building CodeMaster Client v$Version ..." -ForegroundColor Cyan
Push-Location $vueRoot
try {
  npm run tauri:build
  if ($LASTEXITCODE -ne 0) { throw 'tauri build failed' }
} finally {
  Pop-Location
}

$nsisDir = Join-Path $vueRoot 'src-tauri\target\release\bundle\nsis'
$setupExe = Get-ChildItem -LiteralPath $nsisDir -Filter '*_x64-setup.exe' |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setupExe) { throw "No NSIS installer found in $nsisDir" }

$sigFile = Get-ChildItem -LiteralPath $nsisDir -Filter "$($setupExe.BaseName)*.sig" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $sigFile) { throw "No .sig signature found for $($setupExe.Name) - did the build sign the artifact?" }

New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
$targetExe = Join-Path $downloadDir "CodeMaster_v$Version.exe"
$targetSig = Join-Path $downloadDir "CodeMaster_v$Version.exe.sig"
Copy-Item -LiteralPath $setupExe.FullName -Destination $targetExe -Force
Copy-Item -LiteralPath $sigFile.FullName -Destination $targetSig -Force

$sizeMb = [math]::Round((Get-Item $targetExe).Length / 1MB, 1)
Write-Host ''
Write-Host "Published: $targetExe ($sizeMb MB)" -ForegroundColor Green
Write-Host "Signature: $targetSig" -ForegroundColor Green
Write-Host ''
Write-Host 'Verify the updater manifest:' -ForegroundColor Cyan
Write-Host '  http://localhost:5000/download/client/windows/latest.json'