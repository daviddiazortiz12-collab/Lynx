@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo ============================================================================
echo   IEGO RANDOMIZER v1.2.2 — GENERADOR 1-CLIC DE ACTUALIZACION FBI (2DS/3DS)
echo   Semilla preconfigurada: TITAN-C45C-614C (Version TMD: V.1.2.2 ^> V.1.1.2)
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
    set "OUT_DIR=%%~dpFActualizacion_FBI_v1.2.2"
    set "BASE_NAME=%%~nF"
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
set "OUT_CIA=%OUT_DIR%\%BASE_NAME%_Actualizacion_FBI_v1.2.2_TITAN-C45C-614C.cia"

echo   Entrada (solo lectura) : "%SRC_CIA%"
echo   Salida Actualizacion   : "%OUT_CIA%"
echo.

"%~dp0IEGORandomizer.exe" -i "%SRC_CIA%" --output-cia "%OUT_CIA%" --seed TITAN-C45C-614C --fbi-update --companion-luma
if errorlevel 1 (
    echo.
    echo [ERROR] Hubo un problema generando el .cia. Revisa el mensaje superior.
    pause
    exit /b 1
)

echo.
echo ============================================================================
echo   LISTO PARA TU NINTENDO 2DS:
echo   1. Copia este archivo a la carpeta /cias/ de la tarjeta SD de tu 2DS:
echo      "%OUT_CIA%"
echo   2. En tu 2DS abre FBI -^> SD -^> cias -^> pulsa A sobre el .cia -^> "Install CIA".
echo      FBI detectara automaticamente V.1.2.2 y actualizara el juego encima
echo      conservando tu TitleID (0004000000112F00) y tu SaveDataSize (512 KB).
echo ============================================================================
explorer "%OUT_DIR%"
pause
