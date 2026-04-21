[CmdletBinding()]
param(
    [string]$TestProject = "Source/Parsek.Tests/Parsek.Tests.csproj",
    [string]$OutputDir = "TestResults/Coverage",
    [ValidateSet("json", "lcov", "opencover", "cobertura", "teamcity")]
    [string]$Format = "cobertura",
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $TestProject
$coverageDir = Join-Path $repoRoot $OutputDir

if (-not (Test-Path $projectPath)) {
    throw "Test project not found: $projectPath"
}

New-Item -ItemType Directory -Force -Path $coverageDir | Out-Null

$coverletOutput = Join-Path $coverageDir "coverage."
$args = @(
    "test",
    $projectPath,
    "--nologo",
    "-v", "minimal",
    "/p:CollectCoverage=true",
    "/p:CoverletOutput=$coverletOutput",
    "/p:CoverletOutputFormat=$Format",
    "/p:Include=[Parsek]*",
    "/p:IncludeTestAssembly=false"
)

if ($NoRestore) {
    $args += "--no-restore"
}

if ($NoBuild) {
    $args += "--no-build"
}

Write-Host "Running coverage for $projectPath"
Write-Host "Coverage output: $coverageDir"

& dotnet @args
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$coverageFiles = Get-ChildItem -Path $coverageDir -Filter "coverage.*" | Select-Object -ExpandProperty FullName
if ($coverageFiles.Count -eq 0) {
    throw "Coverage completed but no output file was produced in $coverageDir"
}

Write-Host "Coverage files:"
$coverageFiles | ForEach-Object { Write-Host "  $_" }
