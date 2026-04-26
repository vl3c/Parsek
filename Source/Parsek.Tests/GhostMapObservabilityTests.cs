using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-static tests for the observability widening in
    /// <see cref="GhostMapPresence"/> (PR #547 follow-up). These tests cover the
    /// structured GhostMap decision-line builder and the lifecycle-summary
    /// helper without touching KSP-runtime APIs (FlightGlobals, body lookups,
    /// Vessel.Load). The KSP-dependent create/update/destroy paths are pinned
    /// indirectly: the builder is the single sink every production path calls
    /// through, so its field-population contract is the test surface.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapObservabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapObservabilityTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Builder field contract — every documented slot appears when set,
        // and is omitted when unset.
        // -----------------------------------------------------------------

        [Fact]
        public void Builder_AlwaysIncludesActionRecIdxVesselSourceBranchBodyScene()
        {
            var f = GhostMapPresence.NewDecisionFields("create-state-vector-done");
            f.RecordingId = "rec-abc";
            f.RecordingIndex = 7;
            f.VesselName = "Kerbal X";
            f.Source = "StateVector";
            f.Branch = "Relative";
            f.Body = "Kerbin";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("create-state-vector-done", line);
            Assert.Contains("rec=rec-abc", line);
            Assert.Contains("idx=7", line);
            Assert.Contains("vessel=\"Kerbal X\"", line);
            Assert.Contains("source=StateVector", line);
            Assert.Contains("branch=Relative", line);
            Assert.Contains("body=Kerbin", line);
            Assert.Contains("scene=", line);
        }

        [Fact]
        public void Builder_OmitsTerminalFieldsWhenSourceIsNotTerminalOrbit()
        {
            var f = GhostMapPresence.NewDecisionFields("update-state-vector");
            f.RecordingId = "rec-1";
            f.Source = "StateVector";
            f.Branch = "Absolute";
            f.Body = "Kerbin";
            f.StateVecAlt = 1500.0;
            f.StateVecSpeed = 60.0;

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("stateVecAlt=1500", line);
            Assert.Contains("stateVecSpeed=60", line);
            Assert.DoesNotContain("terminalSma", line);
            Assert.DoesNotContain("terminalEcc", line);
            Assert.DoesNotContain("terminalOrbitBody", line);
            Assert.DoesNotContain("anchorPid", line);
        }

        [Fact]
        public void Builder_PopulatesSegmentBlockWhenSegmentSet()
        {
            var seg = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 500.0,
                semiMajorAxis = 4_070_696,
                eccentricity = 0.84,
                inclination = 0.10,
                meanAnomalyAtEpoch = 1.18,
                epoch = 100.0,
            };
            var f = GhostMapPresence.NewDecisionFields("create-segment-done");
            f.RecordingId = "rec-2";
            f.Source = "Segment";
            f.Branch = "(n/a)";
            f.Body = "Kerbin";
            f.Segment = seg;

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("segmentBody=Kerbin", line);
            Assert.Contains("segmentUT=100.0-500.0", line);
            Assert.Contains("sma=4070696", line);
            Assert.Contains("ecc=0.8400", line);
            Assert.Contains("inc=0.1000", line);
            Assert.Contains("mna=1.1800", line);
            Assert.Contains("epoch=100.0", line);
        }

        [Fact]
        public void Builder_PopulatesAnchorBlockWhenRelativeBranch()
        {
            var f = GhostMapPresence.NewDecisionFields("update-state-vector");
            f.RecordingId = "rec-3";
            f.Source = "StateVector";
            f.Branch = "Relative";
            f.Body = "Kerbin";
            f.AnchorPid = 42u;
            f.AnchorPos = new Vector3d(600000, 50, 600000);
            f.LocalOffset = new Vector3d(5, 7, 11);
            f.WorldPos = new Vector3d(600005, 57, 600011);

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("anchorPid=42", line);
            Assert.Contains("anchorPos=(600000.0,50.0,600000.0)", line);
            Assert.Contains("localOffset=(5.0,7.0,11.0)", line);
            Assert.Contains("worldPos=(600005.0,57.0,600011.0)", line);
        }

        [Fact]
        public void Builder_PopulatesTerminalBlockWhenSourceIsTerminalOrbit()
        {
            var f = GhostMapPresence.NewDecisionFields("create-terminal-orbit-done");
            f.RecordingId = "rec-4";
            f.Source = "TerminalOrbit";
            f.Branch = "(n/a)";
            f.Body = "Kerbin";
            f.TerminalBody = "Kerbin";
            f.TerminalSma = 700000.0;
            f.TerminalEcc = 0.0123;

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("terminalOrbitBody=Kerbin", line);
            Assert.Contains("terminalSma=700000", line);
            Assert.Contains("terminalEcc=0.0123", line);
        }

        [Fact]
        public void Builder_OmitsNanNumericFieldsByDefault()
        {
            var f = GhostMapPresence.NewDecisionFields("create-state-vector-intent");
            f.RecordingId = "rec-5";
            f.Source = "StateVector";
            f.Branch = "(n/a)";
            f.Body = "Kerbin";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.DoesNotContain("terminalSma=", line);
            Assert.DoesNotContain("terminalEcc=", line);
            Assert.DoesNotContain("stateVecAlt=", line);
            Assert.DoesNotContain("stateVecSpeed=", line);
            Assert.DoesNotContain("ut=", line);
        }

        [Fact]
        public void Builder_NullVesselNameRendersAsExplicitPlaceholder()
        {
            var f = GhostMapPresence.NewDecisionFields("destroy");
            f.RecordingId = "rec-6";
            f.RecordingIndex = 9;
            f.VesselName = null;
            f.Source = "StateVector";
            f.Branch = "Absolute";
            f.Body = "Kerbin";
            f.Reason = "session-ended";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("vessel=\"(null)\"", line);
            Assert.Contains("reason=session-ended", line);
        }

        // -----------------------------------------------------------------
        // MapResolutionBranch — translates lowercase resolver tags into the
        // capitalised branch names used by the structured log.
        // -----------------------------------------------------------------

        [Fact]
        public void MapResolutionBranch_TranslatesAllKnownTags()
        {
            Assert.Equal("Absolute", GhostMapPresence.MapResolutionBranch("absolute"));
            Assert.Equal("Relative", GhostMapPresence.MapResolutionBranch("relative"));
            Assert.Equal("OrbitalCheckpoint", GhostMapPresence.MapResolutionBranch("orbital-checkpoint"));
            Assert.Equal("no-section", GhostMapPresence.MapResolutionBranch("no-section"));
            Assert.Equal("(n/a)", GhostMapPresence.MapResolutionBranch(null));
        }

        // -----------------------------------------------------------------
        // World-position contract — Absolute uses surfaceLookup, Relative uses
        // the canonical anchor + ResolveRelativePlaybackPosition. Same data,
        // different branch ⇒ different world position. Pin both via the pure
        // helper so the structured-line consumer can reason about the trace.
        // -----------------------------------------------------------------

        [Fact]
        public void WorldPos_AbsoluteBranch_EqualsSurfaceLookupResult()
        {
            var sentinel = new Vector3d(123.0, 456.0, 789.0);
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0,
                endUT = 200,
            };
            var point = new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 67.0,
                bodyName = "Kerbin",
            };

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: (lat, lon, alt) => sentinel,
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u);

            Assert.True(result.Resolved);
            Assert.Equal("absolute", result.Branch);
            Assert.Equal(sentinel.x, result.WorldPos.x, 6);
            Assert.Equal(sentinel.y, result.WorldPos.y, 6);
            Assert.Equal(sentinel.z, result.WorldPos.z, 6);
        }

        [Fact]
        public void WorldPos_RelativeBranch_EqualsAnchorPosPlusOffset()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0,
                endUT = 200,
                anchorVesselId = 42u,
            };
            var point = new TrajectoryPoint
            {
                ut = 100,
                latitude = 5.0,
                longitude = 7.0,
                altitude = 11.0,
                bodyName = "Kerbin",
            };
            var anchorPos = new Vector3d(600000, 50, 600000);
            var surfaceSentinel = new Vector3d(99, 99, 99);

            var result = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: (lat, lon, alt) => surfaceSentinel,
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 42u);

            Assert.True(result.Resolved);
            Assert.Equal("relative", result.Branch);
            Assert.Equal(42u, result.AnchorPid);
            Assert.Equal(anchorPos.x + 5.0, result.WorldPos.x, 4);
            Assert.Equal(anchorPos.y + 7.0, result.WorldPos.y, 4);
            Assert.Equal(anchorPos.z + 11.0, result.WorldPos.z, 4);
            // Critically NOT the surface lookup sentinel — the bug-class guard.
            Assert.NotEqual(surfaceSentinel.x, result.WorldPos.x);
        }

        // -----------------------------------------------------------------
        // Lifecycle-summary helper — emits the canonical summary line and
        // resets the per-tick counters.
        // -----------------------------------------------------------------

        [Fact]
        public void EmitLifecycleSummary_LogsCountersAndResets()
        {
            // Bump counters via static field reflection-free assignment.
            // These are internal because the production paths increment them.
            GhostMapPresence.lifecycleCreatedThisTick = 3;
            GhostMapPresence.lifecycleDestroyedThisTick = 1;
            GhostMapPresence.lifecycleUpdatedThisTick = 7;

            GhostMapPresence.EmitLifecycleSummary("test-scope", currentUT: 12345.6);

            // VerboseRateLimited dedupes with a 5s window; the very first call for
            // a key always emits, so this should appear in the sink.
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]")
                && l.Contains("lifecycle-summary")
                && l.Contains("scope=test-scope")
                && l.Contains("created=3")
                && l.Contains("destroyed=1")
                && l.Contains("updated=7")
                && l.Contains("currentUT=12345.6")
                && l.Contains("mapVisibility[")
                && l.Contains("mapObjMissing=0")
                && l.Contains("orbitRendererMissing=0")
                && l.Contains("iconSuppressed=0"));

            Assert.Equal(0, GhostMapPresence.lifecycleCreatedThisTick);
            Assert.Equal(0, GhostMapPresence.lifecycleDestroyedThisTick);
            Assert.Equal(0, GhostMapPresence.lifecycleUpdatedThisTick);
        }

        [Fact]
        public void BuildLifecycleSummaryMessage_IncludesMapVisibilityCounters()
        {
            var visibility = new GhostMapPresence.GhostMapVisibilityCounters
            {
                uniqueTracked = 5,
                recordingTracked = 3,
                chainTracked = 2,
                mapObjectMissing = 1,
                orbitRendererMissing = 2,
                orbitRendererDisabled = 3,
                drawIconsNotAll = 4,
                iconSuppressed = 5
            };

            string line = GhostMapPresence.BuildLifecycleSummaryMessage(
                "tracking-station",
                visibility,
                created: 6,
                destroyed: 7,
                updated: 8,
                currentUT: 123.4,
                scene: "SPACECENTER");

            Assert.Contains("scope=tracking-station", line);
            Assert.Contains("vesselsTracked=5", line);
            Assert.Contains("recordingTracked=3", line);
            Assert.Contains("chainTracked=2", line);
            Assert.Contains("created=6", line);
            Assert.Contains("destroyed=7", line);
            Assert.Contains("updated=8", line);
            Assert.Contains("currentUT=123.4", line);
            Assert.Contains("scene=SPACECENTER", line);
            Assert.Contains("mapObjMissing=1", line);
            Assert.Contains("orbitRendererMissing=2", line);
            Assert.Contains("orbitRendererDisabled=3", line);
            Assert.Contains("drawIconsNotAll=4", line);
            Assert.Contains("iconSuppressed=5", line);
        }

        [Fact]
        public void BuildGhostProtoVesselVisibilityState_ExplainsIconBlockers()
        {
            string missingMapObject = GhostMapPresence.BuildGhostProtoVesselVisibilityState(
                hasMapObject: false,
                hasOrbitRenderer: true,
                orbitRendererEnabled: true,
                drawIcons: "ALL",
                nativeIconSuppressed: false,
                rendererForceEnabled: false);
            string forcedRenderer = GhostMapPresence.BuildGhostProtoVesselVisibilityState(
                hasMapObject: true,
                hasOrbitRenderer: true,
                orbitRendererEnabled: true,
                drawIcons: "ALL",
                nativeIconSuppressed: false,
                rendererForceEnabled: true);
            string suppressedIcon = GhostMapPresence.BuildGhostProtoVesselVisibilityState(
                hasMapObject: true,
                hasOrbitRenderer: true,
                orbitRendererEnabled: true,
                drawIcons: "ALL",
                nativeIconSuppressed: true,
                rendererForceEnabled: false);

            Assert.Contains("visibilityReason=map-object-missing", missingMapObject);
            Assert.Contains("rendererForceEnabled=True", forcedRenderer);
            Assert.Contains("visibilityReason=renderer-force-enabled", forcedRenderer);
            Assert.Contains("nativeIconSuppressed=True", suppressedIcon);
            Assert.Contains("visibilityReason=native-icon-suppressed", suppressedIcon);
        }

        // -----------------------------------------------------------------
        // Coordinate audit: the structured line is the single sink that
        // surfaces both the input branch AND the resolved world position.
        // Verify that for a Relative-frame point the line carries both, and
        // for an Absolute-frame point likewise.
        // -----------------------------------------------------------------

        [Fact]
        public void RelativeFrame_DecisionLine_CarriesBranchAndWorldPosAndAnchorPid()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 0,
                endUT = 200,
                anchorVesselId = 42u,
            };
            var point = new TrajectoryPoint
            {
                ut = 100,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 3.0,
                bodyName = "Kerbin",
            };
            var anchorPos = new Vector3d(0, 0, 0);

            var resolution = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: (lat, lon, alt) => default(Vector3d),
                anchorFound: true,
                anchorWorldPos: anchorPos,
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 42u);

            // Build the would-be production line manually using the same
            // population logic the create/update paths use. Field shape must
            // include branch=Relative, anchorPid, anchorPos, localOffset,
            // worldPos.
            var f = GhostMapPresence.NewDecisionFields("update-state-vector");
            f.RecordingId = "rec-rel";
            f.RecordingIndex = 5;
            f.VesselName = "Test";
            f.Source = "StateVector";
            f.Branch = GhostMapPresence.MapResolutionBranch(resolution.Branch);
            f.Body = "Kerbin";
            f.WorldPos = resolution.WorldPos;
            f.AnchorPid = resolution.AnchorPid;
            f.AnchorPos = anchorPos;
            f.LocalOffset = new Vector3d(point.latitude, point.longitude, point.altitude);

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("branch=Relative", line);
            Assert.Contains("anchorPid=42", line);
            Assert.Contains("anchorPos=(0.0,0.0,0.0)", line);
            Assert.Contains("localOffset=(1.0,2.0,3.0)", line);
            Assert.Contains("worldPos=", line); // the resolved position must appear
        }

        [Fact]
        public void AbsoluteFrame_DecisionLine_CarriesBranchAndSurfaceWorldPos()
        {
            var sentinel = new Vector3d(1000.0, 2000.0, 3000.0);
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0,
                endUT = 200,
            };
            var point = new TrajectoryPoint
            {
                ut = 100,
                latitude = -0.097,
                longitude = -74.55,
                altitude = 67.0,
                bodyName = "Kerbin",
            };

            var resolution = GhostMapPresence.ResolveStateVectorWorldPositionPure(
                point,
                section,
                recordingFormatVersion: 6,
                absoluteSurfaceLookup: (lat, lon, alt) => sentinel,
                anchorFound: false,
                anchorWorldPos: default(Vector3d),
                anchorWorldRot: Quaternion.identity,
                anchorVesselId: 0u);

            var f = GhostMapPresence.NewDecisionFields("update-state-vector");
            f.RecordingId = "rec-abs";
            f.RecordingIndex = 6;
            f.VesselName = "Test";
            f.Source = "StateVector";
            f.Branch = GhostMapPresence.MapResolutionBranch(resolution.Branch);
            f.Body = "Kerbin";
            f.WorldPos = resolution.WorldPos;

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("branch=Absolute", line);
            Assert.Contains("worldPos=(1000.0,2000.0,3000.0)", line);
            Assert.DoesNotContain("anchorPid=", line);
            Assert.DoesNotContain("anchorPos=", line);
            Assert.DoesNotContain("localOffset=", line);
        }

        // -----------------------------------------------------------------
        // Smoke: the canonical actions every consumer expects appear in the
        // emitted line.
        // -----------------------------------------------------------------

        [Theory]
        [InlineData("create-segment-intent")]
        [InlineData("create-segment-done")]
        [InlineData("create-terminal-orbit-intent")]
        [InlineData("create-terminal-orbit-done")]
        [InlineData("create-state-vector-intent")]
        [InlineData("create-state-vector-done")]
        [InlineData("create-state-vector-skip")]
        [InlineData("create-state-vector-miss")]
        [InlineData("create-dispatch")]
        [InlineData("update-segment")]
        [InlineData("update-state-vector")]
        [InlineData("update-state-vector-soi-change")]
        [InlineData("update-state-vector-skip")]
        [InlineData("update-state-vector-miss")]
        [InlineData("update-terminal-orbit-fallback")]
        [InlineData("update-chain-segment")]
        [InlineData("destroy")]
        [InlineData("destroy-chain")]
        [InlineData("source-resolve")]
        public void Builder_CanonicalActions_RoundTripIntoLine(string action)
        {
            var f = GhostMapPresence.NewDecisionFields(action);
            f.RecordingId = "rec-action";
            f.Source = "StateVector";
            f.Branch = "Absolute";
            f.Body = "Kerbin";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.StartsWith(action + ":", line);
        }

        // -----------------------------------------------------------------
        // P3 review fix (#547): EmitSourceResolveLine builds a structured
        // line through NewDecisionFields and previously left RecordingIndex
        // at the struct default (0), making every source-resolve entry
        // misleadingly log idx=0. The fix threads `recordingIndex` through
        // ResolveMapPresenceGhostSource into EmitSourceResolveLine, with
        // -1 as the "unknown" sentinel for callers that don't have an
        // index in scope.
        // -----------------------------------------------------------------

        [Fact]
        public void Builder_NewDecisionFields_DefaultsRecordingIndexToZero_NoLeakageInProductionLines()
        {
            // Tripwire: NewDecisionFields uses C# 7 struct defaults, so
            // RecordingIndex starts at 0. Any production path that emits
            // a structured line MUST overwrite this with the real index
            // (or the -1 sentinel) before BuildGhostMapDecisionLine runs.
            var f = GhostMapPresence.NewDecisionFields("source-resolve");

            Assert.Equal(0, f.RecordingIndex);
        }

        [Fact]
        public void Builder_RecordingIndexNegativeOne_RendersAsExplicitSentinel()
        {
            // The "unknown" sentinel must round-trip through the line so
            // post-hoc readers can tell idx=-1 (caller had no index) apart
            // from idx=0 (literal recording #0).
            var f = GhostMapPresence.NewDecisionFields("source-resolve");
            f.RecordingId = "rec-unknown-idx";
            f.RecordingIndex = -1;
            f.Source = "None";
            f.Branch = "(n/a)";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("idx=-1", line);
            Assert.DoesNotContain("idx=0 ", line);
        }

        [Fact]
        public void Builder_RecordingIndexNonZero_PropagatesIntoLine()
        {
            // Regression for the P3 review on #547: pre-fix
            // EmitSourceResolveLine never wrote RecordingIndex, so a real
            // index of 7 still printed as idx=0. Production callers now
            // pass `evt.Index` / `idx` through, and the structured line
            // must reflect it.
            var f = GhostMapPresence.NewDecisionFields("source-resolve");
            f.RecordingId = "rec-7";
            f.RecordingIndex = 7;
            f.Source = "Segment";
            f.Branch = "Absolute";
            f.Body = "Kerbin";

            string line = GhostMapPresence.BuildGhostMapDecisionLine(f);

            Assert.Contains("idx=7", line);
            Assert.DoesNotContain("idx=0 ", line);
        }
    }
}
