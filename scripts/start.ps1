param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Write-Info { param([string]$msg) Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok   { param([string]$msg) Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Fail { param([string]$msg) Write-Host "  [FAIL] $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "HOTIX — Starting" -ForegroundColor White
Write-Host "================" -ForegroundColor White
Write-Host ""

Set-Location $projectRoot

# ── Check venv exists ─────────────────────────────────────────────────────────
$python = Join-Path $projectRoot "venv\Scripts\python.exe"
if (-not (Test-Path $python)) {
    Write-Fail "Virtual environment not found. Run 'scripts\setup.ps1' first."
    pause
    exit 1
}

# ── Start Python server ───────────────────────────────────────────────────────
Write-Info "Starting OCR server..."
$serverProcess = Start-Process -FilePath $python `
    -ArgumentList "-m", "uvicorn", "server.main:app", "--host", "127.0.0.1", "--port", "8000" `
    -WorkingDirectory $projectRoot `
    -PassThru `
    -WindowStyle Hidden

# ── Wait for server to be healthy ─────────────────────────────────────────────
Write-Info "Waiting for server to be ready..."
$maxAttempts = 30
$attempt = 0
$ready = $false

while ($attempt -lt $maxAttempts) {
    Start-Sleep -Seconds 1
    $attempt++
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:8000/health" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {}
}

if (-not $ready) {
    Write-Fail "Server did not start within 30 seconds."
    $serverProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    pause
    exit 1
}

Write-Ok "Server is ready."

# ── Launch WPF client ─────────────────────────────────────────────────────────
Write-Info "Launching HOTIX client..."
$clientExe = Join-Path $projectRoot "client\publish\Hotix.InvoiceClient.exe"
if (-not (Test-Path $clientExe)) {
    Write-Fail "Client not built. Run 'scripts\setup.ps1' first."
    $serverProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    pause
    exit 1
}

$clientProcess = Start-Process -FilePath $clientExe -PassThru

Write-Ok "HOTIX is running."
Write-Host ""

# ── Keep server alive until client closes ────────────────────────────────────
$clientProcess.WaitForExit()
Write-Info "Client closed. Stopping server..."
$serverProcess | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Ok "Server stopped."
