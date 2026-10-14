@echo off
setlocal
echo ========================================================
echo     Building & Publishing Lancom for Windows (win-x64)
echo ========================================================

rem Check dotnet
where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo Error: .NET SDK 9.0 is not installed or not in PATH.
    echo Please install .NET SDK from https://dot.net
    pause
    exit /b 1
)

echo.
echo [1/2] Publishing Lancom (Cyber-Tactical HUD)...
dotnet publish src\Lancom.App\Lancom.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o dist\app

echo.
echo [2/2] Publishing Lancom.Relay (Corporate AP Isolation Bridge)...
dotnet publish src\Lancom.Relay\Lancom.Relay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\relay

echo.
echo ========================================================
echo   Build Successful! Executables located in:
echo     - Application: dist\app\Lancom.exe
echo     - Relay Server: dist\relay\Lancom.Relay.exe
echo ========================================================
pause
