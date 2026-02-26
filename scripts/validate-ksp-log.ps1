param(
    [string]$LogPath,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$attemptedPaths = @()

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $attemptedPaths += $LogPath
}

if (-not [string]::IsNullOrWhiteSpace($env:KSPDIR)) {
    $attemptedPaths += (Join-Path $env:KSPDIR "KSP.log")
}

$parentRoot = (Resolve-Path (Join-Path $repoRoot "..")).Path
$attemptedPaths += (Join-Path $parentRoot "Kerbal Space Program\KSP.log")

$resolvedLogPath = $null
foreach ($candidate in $attemptedPaths) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    try {
        $resolved = (Resolve-Path $candidate -ErrorAction Stop).Path
    } catch {
        continue
    }

    if (Test-Path $resolved -PathType Leaf) {
        $resolvedLogPath = $resolved
        break
    }
}

if ([string]::IsNullOrWhiteSpace($resolvedLogPath)) {
    $formattedAttempts = if ($attemptedPaths.Count -gt 0) {
        ($attemptedPaths | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    } else {
        " - (no paths were generated)"
    }

    Write-Error @"
KSP.log was not found. Validation cannot continue.
Attempted paths:
$formattedAttempts
"@
    exit 1
}

$env:PARSEK_LIVE_VALIDATE_REQUIRED = "1"
$env:PARSEK_LIVE_KSP_LOG_PATH = $resolvedLogPath

$testArgs = @(
    "test",
    "Source/Parsek.Tests/Parsek.Tests.csproj",
    "--filter", "FullyQualifiedName~LiveKspLogValidationTests.ValidateLatestSession",
    "-v", "minimal"
)

if ($NoBuild) {
    $testArgs += "--no-build"
}

Write-Host "Validating latest Parsek session in '$resolvedLogPath'"
dotnet @testArgs

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "KSP.log validation passed."
