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
# ParsekFlightGhostPositioner is a partial class too: its recorded-relative row
# is repeated for each ParsekFlightGhostPositioner.<Group>.cs file, and the
# required loop-only live-anchor flag is read in the .Loop.cs file that holds it.
# The managed fallback in Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs
# must carry byte-identical Path / Pattern / Label rows and match them
# case-insensitively, as Select-String does here;
# NonLoopLivePidAudit_ManagedArmMatchesPwshArm parses this file and reds on drift.
$checks = @(
    @{
        Path = "Source/Parsek/Controllers/IGhostPositioner.cs"
        Pattern = "TryGetLiveAnchorWorldPosition"
        Label = "IGhostPositioner live anchor API"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.Completion.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.Diagnostics.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.GhostVisuals.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.InRangeRender.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.LoopPlayback.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.LoopUnits.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.ReentryFx.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.SectionPositioning.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostPlaybackEngine.SpawnDestroy.cs"
        Pattern = "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid"
        Label = "engine live-anchor appearance diagnostics"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.GhostPositioner.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.GhostPositioning.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.TimelinePlayback.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.MergeFlow.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.PadEvidence.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.RecorderSession.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.SplitBranching.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.SwitchContinuation.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlight.TreeRestore.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Points.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Orbit.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.OrbitTailContinuity.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Checkpoint.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.WorldPosition.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.RecordedRelative.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.RelativeAnchor.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.BodyFixedPrimary.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Trace.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Loop.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.TailLift.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightPostSwitchAutoRecord.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightPlaybackHost.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightSpawnHost.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightSurfaceCapture.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightSplitBrancher.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightMergeFlow.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightSwitchContinuation.cs"
        Pattern = "target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid"
        Label = "recorded-relative flight playback live PID read"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostRenderTrace.cs"
        Pattern = "context\.AnchorVesselId\s*=|section\.AnchorVesselId"
        Label = "recorded-relative trace section PID propagation"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekKSC.cs"
        Pattern = "KscAnchorLookup|TryLookupKscAnchorFrame|FindVesselByPid\(anchorVesselId|anchorPid=|section\.anchorVesselId"
        Label = "KSC Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.ProtoVesselLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.SourceResolution.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.StateVectors.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.ReFlySuppression.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.TrackingStationLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.TargetingAndSelection.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.OrbitLines.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.OverlapInstances.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapPresence.FlightMapLifecycle.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    },
    @{
        Path = "Source/Parsek/Ghost/GhostMapVessels.cs"
        Pattern = "ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId"
        Label = "map Relative live PID playback"
    }
)

$requiredChecks = @(
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightGhostPositioner.Loop.cs"
        Pattern = "relativeLoopLiveAnchor\s*=\s*true"
        Label = "loop-only LateUpdate live-anchor flag"
    },
    @{
        Path = "Source/Parsek/Controllers/ParsekFlightPlaybackHost.cs"
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
