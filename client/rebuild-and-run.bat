@echo off
setlocal enabledelayedexpansion

cd /d "%~dp0\.."

echo Pulling latest changes...
git pull
if errorlevel 1 (
    echo Error: git pull failed
    exit /b 1
)

echo Cleaning build artifacts...
if exist "client\bin" rmdir /s /q "client\bin"
if exist "client\obj" rmdir /s /q "client\obj"

echo Building client...
cd client
dotnet build
if errorlevel 1 (
    echo Error: dotnet build failed
    exit /b 1
)

echo Build succeeded. Launching application...
cd bin\Debug\net8.0-windows
start Hotix.InvoiceClient.exe
