<#
.SYNOPSIS
    Pre-build dependency fetcher for the Hotix Inno Setup installer.

.DESCRIPTION
    Downloads the third-party binaries the installer bundles (the Python 3.12
    installer and the Poppler Windows binaries) into installer/vendor/ and
    verifies each against a pinned SHA256 checksum before use.

    Run this before compiling the installer:

        powershell -ExecutionPolicy Bypass -File installer\fetch-vendor.ps1
        iscc.exe installer\Hotix.iss

    Nothing is stored in git: installer/vendor/ is gitignored (see
    installer/vendor/README.md). This script is the single source of truth
    for what gets bundled into the installer.

.PARAMETER Force
    Re-download and re-extract even if verified files already exist.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\fetch-vendor.ps1
#>

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$VendorDir  = Join-Path $PSScriptRoot 'vendor'
$PopplerDir = Join-Path $VendorDir 'poppler'
$TempDir    = Join-Path $env:TEMP 'hotix-vendor-fetch'

# ── Pinned sources & checksums ────────────────────────────────────────────────
# Python 3.12.6 (matches #define PythonInstallerName in Hotix.iss)
$PythonUrl       = 'https://www.python.org/ftp/python/3.12.6/python-3.12.6-amd64.exe'
$PythonFileName  = 'python-3.12.6-amd64.exe'
$PythonSha256    = '5914748E6580E70BEDEB7C537A0832B3071DE9E09A2E4E7E3D28060616045E0A'

# Poppler 26.02.0-0 Windows build (oschwartz10612/poppler-windows release)
$PopplerUrl      = 'https://github.com/oschwartz10612/poppler-windows/releases/download/v26.02.0-0/Release-26.02.0-0.zip'
$PopplerZipName  = 'Release-26.02.0-0.zip'
$PopplerSha256   = '993E4A94376ED712FAFC7058D724EA0B943D118BBD2305CD9ED55174EB85CDA5'

function Get-Sha256 {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToUpperInvariant()
}

function Assert-Verified {
    param([string]$Path, [string]$ExpectedHash)
    return ((Get-Sha256 $Path) -eq $ExpectedHash)
}

function Invoke-Download {
    param([string]$Url, [string]$OutFile, [string]$ExpectedHash, [string]$Label)
    Write-Host "Downloading $Label ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    if (-not (Assert-Verified $OutFile $ExpectedHash)) {
        $actual = Get-Sha256 $OutFile
        throw "SHA256 verification FAILED for $Label. Expected $($ExpectedHash.ToLower()), got $($actual.ToLower()). Refusing to use an unverified binary."
    }
    Write-Host "  verified: $($ExpectedHash.ToLower())" -ForegroundColor Green
}

# ── Directory setup ───────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null
New-Item -ItemType Directory -Force -Path $TempDir  | Out-Null

# ── Python installer ──────────────────────────────────────────────────────────
$pythonPath = Join-Path $VendorDir $PythonFileName
if ((Test-Path $pythonPath) -and (Assert-Verified $pythonPath $PythonSha256) -and -not $Force) {
    Write-Host "Python installer already present & verified." -ForegroundColor Green
} else {
    Invoke-Download $PythonUrl $pythonPath $PythonSha256 'Python 3.12.6 installer'
}

# ── Poppler zip + extraction ──────────────────────────────────────────────────
$popplerZipPath = Join-Path $VendorDir $PopplerZipName
if ((Test-Path $popplerZipPath) -and (Assert-Verified $popplerZipPath $PopplerSha256) -and -not $Force) {
    Write-Host "Poppler zip already present & verified." -ForegroundColor Green
} else {
    Invoke-Download $PopplerUrl $popplerZipPath $PopplerSha256 'Poppler 26.02.0-0'
}

# Extract into vendor/poppler/ matching what Hotix.iss bundles:
#   vendor\poppler\Library\...  and  vendor\poppler\share\...
$extractRoot = Join-Path $TempDir 'poppler-extract'
if (Test-Path $extractRoot) { Remove-Item $extractRoot -Recurse -Force }
Expand-Archive -Path $popplerZipPath -DestinationPath $extractRoot -Force

# The zip contains a single top-level folder "poppler-26.02.0/" with
# Library/ and share/ inside it.
$inner = Get-ChildItem -Path $extractRoot -Directory | Select-Object -First 1
if ($null -eq $inner) { throw "Unexpected poppler zip layout: no top-level directory found." }

if (Test-Path $PopplerDir) { Remove-Item $PopplerDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PopplerDir | Out-Null

foreach ($sub in @('Library', 'share')) {
    $src = Join-Path $inner.FullName $sub
    if (-not (Test-Path $src)) { throw "Unexpected poppler zip layout: missing '$sub' under $($inner.Name)." }
    Copy-Item -Path $src -Destination $PopplerDir -Recurse -Force
}

Remove-Item $extractRoot -Recurse -Force

Write-Host ''
Write-Host 'vendor/ ready:' -ForegroundColor Green
Get-ChildItem $VendorDir | Select-Object Name, Length | Format-Table -AutoSize
Write-Host 'Next: iscc.exe installer\Hotix.iss' -ForegroundColor Cyan
