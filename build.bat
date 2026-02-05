@echo off
setlocal

:: Parsek Build Script
:: Usage: build.bat [Debug|Release] [KSP_PATH]

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

set KSPDIR=%2
if "%KSPDIR%"=="" set KSPDIR=%~dp0Kerbal Space Program

:: Find MSBuild
set MSBUILD=
for %%i in (
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) do (
    if exist %%i set MSBUILD=%%i
)

if "%MSBUILD%"=="" (
    echo ERROR: MSBuild not found. Install Visual Studio Build Tools.
    exit /b 1
)

echo.
echo === Parsek Build ===
echo Config:  %CONFIG%
echo KSP Dir: %KSPDIR%
echo MSBuild: %MSBUILD%
echo.

:: Build
echo Building...
%MSBUILD% "Source\Parsek.sln" /p:Configuration=%CONFIG% /p:KSPDIR="%KSPDIR%" /verbosity:minimal /nologo

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    exit /b 1
)

echo.
echo === Build Complete ===
echo Output: %KSPDIR%\GameData\Parsek\Plugins\Parsek.dll
