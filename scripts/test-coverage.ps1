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

function Set-MissingProcessEnvVar {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $currentValue = [System.Environment]::GetEnvironmentVariable(
        $Name,
        [System.EnvironmentVariableTarget]::Process)

    if (-not [string]::IsNullOrWhiteSpace($currentValue)) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($Value) -or -not (Test-Path -LiteralPath $Value)) {
        return $null
    }

    [System.Environment]::SetEnvironmentVariable(
        $Name,
        $Value,
        [System.EnvironmentVariableTarget]::Process)

    return "$Name=$Value"
}

function Initialize-DotnetPathEnvironment {
    $systemDrive = [System.Environment]::GetEnvironmentVariable(
        "SystemDrive",
        [System.EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($systemDrive)) {
        $systemDrive = "C:"
    }

    $userProfile = [System.Environment]::GetEnvironmentVariable(
        "USERPROFILE",
        [System.EnvironmentVariableTarget]::Process)

    $bootstrapped = @()
    $bootstrapped += Set-MissingProcessEnvVar -Name "ProgramData" -Value (Join-Path $systemDrive "ProgramData")
    $bootstrapped += Set-MissingProcessEnvVar -Name "ProgramFiles" -Value (Join-Path $systemDrive "Program Files")
    $bootstrapped += Set-MissingProcessEnvVar -Name "ProgramFiles(x86)" -Value (Join-Path $systemDrive "Program Files (x86)")

    if (-not [string]::IsNullOrWhiteSpace($userProfile) -and (Test-Path -LiteralPath $userProfile)) {
        $bootstrapped += Set-MissingProcessEnvVar -Name "APPDATA" -Value (Join-Path $userProfile "AppData\Roaming")
        $bootstrapped += Set-MissingProcessEnvVar -Name "LOCALAPPDATA" -Value (Join-Path $userProfile "AppData\Local")
    }

    return @($bootstrapped | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Remove-CoverageArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CoverageDir
    )

    Get-ChildItem -Path $CoverageDir -Filter "coverage.*" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    foreach ($artifactName in @("coverage-summary.txt", "dotnet-test.log")) {
        $artifactPath = Join-Path $CoverageDir $artifactName
        if (Test-Path -LiteralPath $artifactPath) {
            Remove-Item -LiteralPath $artifactPath -Force
        }
    }
}

function Get-ValidatedCoverageFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CoverageDir
    )

    $coverageFiles = @(Get-ChildItem -Path $CoverageDir -Filter "coverage.*" -File |
        Sort-Object Name)

    if ($coverageFiles.Count -eq 0) {
        throw "Coverage completed but no coverage.* file was produced in $CoverageDir"
    }

    foreach ($coverageFile in $coverageFiles) {
        if ($coverageFile.Length -le 0) {
            throw "Coverage file is empty: $($coverageFile.FullName)"
        }
    }

    return $coverageFiles
}

function Write-CoberturaSummary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CoverageFile,

        [Parameter(Mandatory = $true)]
        [string]$SummaryPath
    )

    [xml]$coverageXml = Get-Content -LiteralPath $CoverageFile -Raw
    $coverage = $coverageXml.coverage
    if ($null -eq $coverage) {
        throw "Cobertura coverage file is missing the root <coverage> element: $CoverageFile"
    }

    $requiredAttributes = @(
        "line-rate",
        "branch-rate",
        "lines-covered",
        "lines-valid",
        "branches-covered",
        "branches-valid"
    )

    foreach ($attribute in $requiredAttributes) {
        if ([string]::IsNullOrWhiteSpace($coverage.$attribute)) {
            throw "Cobertura coverage file is missing required '$attribute' metadata: $CoverageFile"
        }
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $lineRate = [double]::Parse($coverage."line-rate", $culture)
    $branchRate = [double]::Parse($coverage."branch-rate", $culture)
    $linesCovered = [int]::Parse($coverage."lines-covered", $culture)
    $linesValid = [int]::Parse($coverage."lines-valid", $culture)
    $branchesCovered = [int]::Parse($coverage."branches-covered", $culture)
    $branchesValid = [int]::Parse($coverage."branches-valid", $culture)
    $packages = @($coverageXml.SelectNodes("/coverage/packages/package")).Count
    $classes = @($coverageXml.SelectNodes("/coverage/packages/package/classes/class")).Count

    $summaryLines = @(
        "Coverage summary",
        "Format: cobertura",
        "File: $CoverageFile",
        "Line coverage: $linesCovered/$linesValid ($($lineRate.ToString('P2', $culture)))",
        "Branch coverage: $branchesCovered/$branchesValid ($($branchRate.ToString('P2', $culture)))",
        "Packages: $packages",
        "Classes: $classes"
    )

    Set-Content -LiteralPath $SummaryPath -Value $summaryLines -Encoding UTF8
    return $summaryLines
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $TestProject
$coverageDir = Join-Path $repoRoot $OutputDir
$coverageLogPath = Join-Path $coverageDir "dotnet-test.log"
$coverageSummaryPath = Join-Path $coverageDir "coverage-summary.txt"

if (-not (Test-Path $projectPath)) {
    throw "Test project not found: $projectPath"
}

New-Item -ItemType Directory -Force -Path $coverageDir | Out-Null
Remove-CoverageArtifacts -CoverageDir $coverageDir

$bootstrappedEnv = Initialize-DotnetPathEnvironment

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
Write-Host "Coverage log: $coverageLogPath"

if ($bootstrappedEnv.Count -gt 0) {
    Write-Host "Bootstrapped missing Windows path environment for dotnet:"
    $bootstrappedEnv | ForEach-Object { Write-Host "  $_" }
}

& dotnet @args 2>&1 | Tee-Object -FilePath $coverageLogPath
$dotnetExitCode = $LASTEXITCODE
if ($dotnetExitCode -ne 0) {
    throw "Coverage run failed before report validation (dotnet exit code $dotnetExitCode). See $coverageLogPath for restore/build/test output."
}

$coverageFiles = Get-ValidatedCoverageFiles -CoverageDir $coverageDir

Write-Host "Coverage files:"
$coverageFiles | ForEach-Object { Write-Host "  $($_.FullName)" }

if ($Format -eq "cobertura") {
    $summaryLines = Write-CoberturaSummary `
        -CoverageFile $coverageFiles[0].FullName `
        -SummaryPath $coverageSummaryPath

    Write-Host "Coverage summary:"
    $summaryLines | ForEach-Object { Write-Host "  $_" }
    Write-Host "Coverage summary file: $coverageSummaryPath"
}
