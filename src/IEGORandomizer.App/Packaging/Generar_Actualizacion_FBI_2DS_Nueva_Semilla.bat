@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo ============================================================================
echo   IEGO RANDOMIZER v1.2.1 — GENERADOR 1-CLIC DE ACTUALIZACION FBI (2DS/3DS)
echo   Modo: Nueva Semilla Aleatoria (Version TMD: V.1.2.1 ^> V.1.1.2)
echo ============================================================================
echo.

set "SRC_CIA=%~1"

if "%SRC_CIA%"=="" (
    echo Abriendo selector de archivo Windows... Elige tu .CIA original de Inazuma Eleven GO.
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Title = 'Selecciona tu archivo .CIA original de Inazuma Eleven GO (Luz o Sombra)'; $f.Filter = 'Archivos CIA de Nintendo 3DS/2DS (*.cia)|*.cia|Todos los archivos (*.*)|*.*'; if ($f.ShowDialog() -eq 'OK') { Write-Output $f.FileName }"`) do (
        set "SRC_CIA=%%I"
    )
)

if "%SRC_CIA%"=="" (
    echo [AVISO] No se selecciono ningun archivo .cia. Operacion cancelada.
    pause
    exit /b 1
)

for %%F in ("%SRC_CIA%") do (
    set "OUT_DIR=%%~dpFActualizacion_FBI_v1.2.1"
    set "BASE_NAME=%%~nF"
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
set "OUT_CIA=%OUT_DIR%\%BASE_NAME%_Actualizacion_FBI_v1.2.1_Random.cia"

"%~dp0IEGORandomizer.exe" -i "%SRC_CIA%" --output-cia "%OUT_CIA%" --fbi-update --companion-luma
if errorlevel 1 (
    echo.
    echo [ERROR] Hubo un problema generando el .cia. Revisa el mensaje superior.
    pause
    exit /b 1
)

explorer "%OUT_DIR%"
pause
