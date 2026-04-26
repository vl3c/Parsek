@echo off
setlocal

:: Parsek Build Script
:: Usage: build.bat [Debug|Release] [KSP_PATH]

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "KSPDIR_ARG=%~2"
if not "%KSPDIR_ARG%"=="" (
    set "KSPDIR=%KSPDIR_ARG%"
) else (
    if "%KSPDIR%"=="" (
        if exist "%~dp0Kerbal Space Program\KSP_x64_Data\Managed\Assembly-CSharp.dll" (
            set "KSPDIR=%~dp0Kerbal Space Program"
        ) else (
            if exist "%~dp0..\Kerbal Space Program\KSP_x64_Data\Managed\Assembly-CSharp.dll" (
                set "KSPDIR=%~dp0..\Kerbal Space Program"
            ) else (
                set "KSPDIR=%~dp0Kerbal Space Program"
            )
        )
    )
)
for %%I in ("%KSPDIR%") do set "KSPDIR=%%~fI"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK not found in PATH.
    exit /b 1
)

echo.
echo === Parsek Build ===
echo Config:  %CONFIG%
echo KSP Dir: %KSPDIR%
echo Engine:  dotnet build
echo.

copy /Y "%~dp0AGENTS.md" "%~dp0..\AGENTS.md" >nul

:: Build the mod project directly.
:: This avoids a flaky solution-level parallel restore/build path on the local .NET SDK
:: and keeps deployment behavior identical via Parsek.csproj's post-build copy target.
echo Building...
dotnet build "Source\Parsek\Parsek.csproj" -c %CONFIG% -m:1 /p:KSPDIR="%KSPDIR%" -v minimal --nologo

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo.
echo === Build Complete ===
echo Output: %KSPDIR%\GameData\Parsek\Plugins\Parsek.dll
