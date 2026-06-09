param(
    [switch]$SmokeTest,
    [switch]$SkipVerify,
    [switch]$NoReload
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "[HOTIX] $Message"
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectRoot

$activateScript = Join-Path $projectRoot 'venv\Scripts\Activate.ps1'
if (-not (Test-Path $activateScript)) {
    throw "Virtual environment not found: $activateScript"
}

Write-Step 'Activating virtual environment'
. $activateScript

if (-not $SkipVerify) {
    Write-Step 'Running system verification'
    python server/verify_system.py
    if ($LASTEXITCODE -ne 0) {
        throw 'System verification failed.'
    }
}

if ($SmokeTest) {
    Write-Step 'Running OCR smoke test'
    python server/test_ocr.py
    if ($LASTEXITCODE -ne 0) {
        throw 'OCR smoke test failed.'
    }
}

$reloadArg = if ($NoReload) { @() } else { @('--reload') }
Write-Step 'Starting FastAPI server'
python -m uvicorn server.main:app @reloadArg
