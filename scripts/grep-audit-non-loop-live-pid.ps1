param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$checks = @(
    @{
        Path = "Source/Parsek/IGhostPositioner.cs"
        Pattern = "TryGetLiveAnchorWorldPosition"
        Label = "IGhostPositioner live anchor API"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/GhostRenderTrace.cs"
        Pattern = "context\.AnchorVesselId\s*=|section\.AnchorVesselId"
        Label = "recorded-relative trace section PID propagation"
    },
    @{
        Path = "Source/Parsek/ParsekKSC.cs"
        Pattern = "KscAnchorLookup|TryLookupKscAnchorFrame|FindVesselByPid\(anchorVesselId|anchorPid=|section\.anchorVesselId"
        Label = "KSC Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFlyAbsoluteShadowPoint|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    }
)

$requiredChecks = @(
    @{
        Path = "Source/Parsek/ParsekFlight.cs"
        Pattern = "relativeLoopLiveAnchor\s*=\s*true"
        Label = "loop-only LateUpdate live-anchor flag"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.cs"
        Pattern = "NonLoopLivePidGuard\.NonLoopRelativeLivePidLookupAttempted"
        Label = "non-loop LateUpdate live-PID DEBUG guard"
    }
)

$violations = New-Object System.Collections.Generic.List[string]
foreach ($check in $checks) {
    $file = Join-Path $RepoRoot $check.Path
    if (!(Test-Path -LiteralPath $file)) {
        $violations.Add("missing file: $($check.Path)")
        continue
    }

    $matches = Select-String -Path $file -Pattern $check.Pattern
    foreach ($match in $matches) {
        $violations.Add("$($check.Label): $($check.Path):$($match.LineNumber): $($match.Line.Trim())")
    }
}

foreach ($check in $requiredChecks) {
    $file = Join-Path $RepoRoot $check.Path
    if (!(Test-Path -LiteralPath $file)) {
        $violations.Add("missing file: $($check.Path)")
        continue
    }

    $matches = Select-String -Path $file -Pattern $check.Pattern
    if ($matches.Count -eq 0) {
        $violations.Add("missing required guard: $($check.Label): $($check.Path): pattern '$($check.Pattern)'")
    }
}

if ($violations.Count -gt 0) {
    Write-Error ("Non-loop live-PID audit failed:`n" + ($violations -join "`n"))
    exit 1
}

Write-Host "Non-loop live-PID audit passed."
