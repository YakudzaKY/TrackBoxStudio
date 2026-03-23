@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "CONFIGURATION=%~1"

if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"

pushd "%ROOT_DIR%" >nul

echo Building TrackBoxStudio with configuration %CONFIGURATION%...
dotnet build "TrackBoxStudio.csproj" -c "%CONFIGURATION%"
set "EXIT_CODE=%ERRORLEVEL%"

popd >nul

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Build failed with exit code %EXIT_CODE%.
    exit /b %EXIT_CODE%
)

echo.
echo Build completed successfully.
exit /b 0
