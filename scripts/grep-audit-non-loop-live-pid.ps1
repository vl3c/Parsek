param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

# Each forbidden pattern names a deleted non-loop live-PID reader so a
# reintroduction reds. The GhostMapPresence arm's TryResolveActiveReFly\w*Point
# covers the deleted active-Re-Fly anchor-PID shadow resolver under its
# original AbsoluteShadow name and under the BodyFixedPrimary vocabulary the
# rest of the code was renamed to.
# GhostMapPresence is split across partial files; the map arm repeats one row per
# file that came out of GhostMapPresence.cs so the scan covers the same code.
# GhostPlaybackEngine is a partial class: its row is repeated for each
# GhostPlaybackEngine.<Area>.cs file the main file was split into, so a member
# moved out of the main file stays under the same gate.
# The managed fallback in Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs
# must carry byte-identical Path / Pattern / Label rows and match them
# case-insensitively, as Select-String does here;
# NonLoopLivePidAudit_ManagedArmMatchesPwshArm parses this file and reds on drift.
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
        Path = "Source/Parsek/GhostPlaybackEngine.Completion.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.Diagnostics.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.GhostVisuals.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.InRangeRender.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.LoopPlayback.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.LoopUnits.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.ReentryFx.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.SectionPositioning.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/GhostPlaybackEngine.SpawnDestroy.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.Accessors.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.GhostPositioner.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.GhostPositioning.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.Spawning.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.TimelinePlayback.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.Flags.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.MergeFlow.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.PadEvidence.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.PostSwitchAutoRecord.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.RecorderSession.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.SplitBranching.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.SplitEvents.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.SwitchContinuation.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlight.TreeRestore.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlightGhostPositioner.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlightPostSwitchAutoRecord.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlightPlaybackHost.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/ParsekFlightSpawnHost.cs"
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
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.ProtoVesselLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.SourceResolution.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.StateVectors.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.ReFlySuppression.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.TrackingStationLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.TargetingAndSelection.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.OrbitLines.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.OverlapInstances.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/GhostMapPresence.FlightMapLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    }
)

$requiredChecks = @(
    @{
        Path = "Source/Parsek/ParsekFlightGhostPositioner.cs"
        Pattern = "relativeLoopLiveAnchor\s*=\s*true"
        Label = "loop-only LateUpdate live-anchor flag"
    },
    @{
        Path = "Source/Parsek/ParsekFlightPlaybackHost.cs"
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
