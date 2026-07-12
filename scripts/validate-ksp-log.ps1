# KSP.log contract validation CLI driver (module M-A1 + the M-A5 harness
# suppression seam). Resolves the target KSP.log, sets the env the live-validation
# xUnit test reads, drives that single test via `dotnet test`, and propagates the
# exit code.
#
# Run-shape rule suppression (M-A5): -KilledRun and -NoRecordingRun set
# PARSEK_LIVE_SUPPRESS_RULES to the exact marker-pairing rule codes a run of that
# shape legitimately trips, so a truncated (killed) tail or a recording-free run
# validates clean instead of redding on those rules. FMT-001/FMT-002/WRN-001 stay
# MANDATORY (the C# checker rejects any request to suppress them). The env var is
# CLEARED in a finally block so a crash mid-validation never leaks the suppression
# into a later invocation.
#   -KilledRun       suppress SES-000,SES-001,REC-001,REC-003 (kill truncates the tail).
#   -NoRecordingRun  suppress REC-001,REC-003 (a recording-free scenario has no
#                    Recording started/stopped lines). Orthogonal to -KilledRun;
#                    both may be passed and the suppressed sets union.
param(
    [string]$LogPath,
    [switch]$NoBuild,
    [switch]$KilledRun,
    [switch]$NoRecordingRun
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

# Build the run-shape suppression list from the switches. Order does not matter
# (the C# checker normalizes + dedupes); FMT/FMT/WRN are never suppressible.
$suppressRules = [System.Collections.Generic.List[string]]::new()
if ($KilledRun) { @("SES-000", "SES-001", "REC-001", "REC-003") | ForEach-Object { $suppressRules.Add($_) } }
if ($NoRecordingRun) { @("REC-001", "REC-003") | ForEach-Object { if (-not $suppressRules.Contains($_)) { $suppressRules.Add($_) } } }

$suppressWasSet = $false
$exitCode = 0
try {
    if ($suppressRules.Count -gt 0) {
        $env:PARSEK_LIVE_SUPPRESS_RULES = ($suppressRules -join ",")
        $suppressWasSet = $true
        Write-Host "Suppressing marker-pairing rules: $($env:PARSEK_LIVE_SUPPRESS_RULES) (KilledRun=$KilledRun NoRecordingRun=$NoRecordingRun)"
    }

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
    $exitCode = $LASTEXITCODE
}
finally {
    # Never leak the suppression into the caller's session or a later invocation.
    if ($suppressWasSet) { Remove-Item Env:\PARSEK_LIVE_SUPPRESS_RULES -ErrorAction SilentlyContinue }
    Remove-Item Env:\PARSEK_LIVE_VALIDATE_REQUIRED -ErrorAction SilentlyContinue
    Remove-Item Env:\PARSEK_LIVE_KSP_LOG_PATH -ErrorAction SilentlyContinue
}

if ($exitCode -ne 0) {
    exit $exitCode
}

Write-Host "KSP.log validation passed."
