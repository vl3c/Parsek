param(
    [switch]$CleanStart,
    [string]$SaveName = "test career",
    [string]$TargetSave = "1.sfs",
    [switch]$Build
)

$ErrorActionPreference = "Stop"

# GNU-style compatibility:
#   --clean-start, --save-name "test career", --target-save 1.sfs, --build
for ($i = 0; $i -lt $args.Count; $i++) {
    switch ($args[$i]) {
        "--clean-start" { $CleanStart = $true; continue }
        "--build" { $Build = $true; continue }
        "--save-name" {
            if ($i + 1 -lt $args.Count) { $SaveName = $args[$i + 1]; $i++ }
            continue
        }
        "--target-save" {
            if ($i + 1 -lt $args.Count) { $TargetSave = $args[$i + 1]; $i++ }
            continue
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

$saveDir = Join-Path $repoRoot "Kerbal Space Program\saves\$SaveName"
$targetPath = Join-Path $saveDir $TargetSave
$persistentPath = Join-Path $saveDir "persistent.sfs"

if (!(Test-Path $saveDir)) {
    throw "Save folder not found: $saveDir"
}

if (!(Test-Path $persistentPath)) {
    throw "persistent.sfs not found in: $saveDir"
}

if (!(Test-Path $targetPath)) {
    Copy-Item $persistentPath $targetPath -Force
    Write-Host "Created missing $TargetSave from persistent.sfs"
}

$env:PARSEK_INJECT_SAVE_NAME = $SaveName
$env:PARSEK_INJECT_TARGET_SAVE = $TargetSave
$env:PARSEK_INJECT_CLEAN_START = if ($CleanStart) { "1" } else { "0" }

Write-Host "Injecting recordings into save '$SaveName' target '$TargetSave' (clean-start=$($CleanStart.IsPresent))"
$testArgs = @(
    "test",
    "Source/Parsek.Tests/Parsek.Tests.csproj",
    "--filter", "InjectAllRecordings",
    "-v", "minimal"
)
if (-not $Build) {
    # Default: avoid rebuilding/copying plugin dll while KSP is running.
    $testArgs += "--no-build"
}

dotnet @testArgs

if ($LASTEXITCODE -ne 0) {
    if (-not $Build) {
        throw "Injector test failed with exit code $LASTEXITCODE. If assemblies are stale, rerun with --build after closing KSP."
    }
    throw "Injector test failed with exit code $LASTEXITCODE"
}

# Keep persistent in lockstep with the target save so KSP's initial load state
# cannot diverge from the manually selected save slot.
if ($TargetSave -ne "persistent.sfs") {
    Copy-Item $targetPath $persistentPath -Force
    Write-Host "Synchronized persistent.sfs from $TargetSave"
}

Write-Host "Injection complete."
