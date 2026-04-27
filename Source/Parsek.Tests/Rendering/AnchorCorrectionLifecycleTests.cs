using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 2 T4 lifecycle tests for the wiring between
    /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/> mutations and
    /// <see cref="RenderSessionState"/> (design doc §17.2 / §18 Phase 2 /
    /// §19.2 Session State Lifecycle / §26.1 HR-9). The plan calls for one
    /// test per clear-trigger; KSP-dependent paths (OnLoad, OnDestroy,
    /// RewindInvoker.ConsumePostLoad) cannot be exercised cleanly in xUnit
    /// without standing up FlightGlobals + ScenarioModule lifecycle. This
    /// file keeps the contract enforced via:
    /// <list type="bullet">
    ///   <item><description>A source-scan coverage check that every
    ///   <c>ActiveReFlySessionMarker = null</c> in production code is paired
    ///   with a <c>RenderSessionState.Clear(</c> call within ±8 lines.</description></item>
    ///   <item><description>Direct behavioural tests on the helpers we own
    ///   (<see cref="RenderSessionState.Clear"/>,
    ///   <see cref="RenderSessionState.RebuildFromMarker(ReFlySessionMarker, IReadOnlyList{Recording}, Func{string, RecordingTreeContext}, Func{string, Vector3d?})"/>
    ///   on the null-marker path).</description></item>
    /// </list>
    /// In-game coverage of OnLoad and OnDestroy lives next to this file
    /// in <c>InGameTests/RuntimeTests.cs</c>'s
    /// <c>Pipeline_Anchor_LiveSeparation</c>.
    /// </summary>
    [Collection("Sequential")]
    public class AnchorCorrectionLifecycleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorCorrectionLifecycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
        }

        public void Dispose()
        {
            RenderSessionState.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- 1. RenderSessionState.Clear empties the map ----------------

        [Fact]
        public void Clear_EmptiesAnchorMap()
        {
            // What makes it fail: T4 hooks every marker-clear site with a
            // RenderSessionState.Clear call. If Clear() does not actually
            // empty the in-memory map, the marker-clear sites would leave
            // stale anchors live in a session where the marker is gone —
            // exactly the leak HR-9 visibility forbids.
            SeedOneAnchor("sess-test", "rec-A", sectionIndex: 0);
            Assert.Equal(1, RenderSessionState.Count);

            RenderSessionState.Clear("test-trigger");
            Assert.Equal(0, RenderSessionState.Count);
            Assert.Null(RenderSessionState.CurrentSessionId);
        }

        [Fact]
        public void Clear_LogsReasonAndPriorSessionId()
        {
            // What makes it fail: §19.2 Session State Lifecycle row "Clear"
            // requires the reason and prior session id in the log line so
            // operators can correlate post-mortem clears with the session
            // they belonged to.
            SeedOneAnchor("sess-bookend", "rec-A", sectionIndex: 0);
            RenderSessionState.Clear("scenario-destroyed");
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]")
                && l.Contains("Clear: reason=scenario-destroyed")
                && l.Contains("previousSessionId=sess-bookend")
                && l.Contains("clearedCount=1"));
        }

        // ----- 2. null-marker rebuild path clears (LoadRewindStagingState
        //         pre-load case) -------------------------------------------

        [Fact]
        public void RebuildFromMarker_NullMarker_ClearsAnyPriorAnchors()
        {
            // What makes it fail: this is the path hit when ParsekScenario
            // (and the other clear sites) call into the rebuild after
            // nulling the marker. If RebuildFromMarker(null) did NOT clear
            // the prior session's map, every clear site that relies on a
            // subsequent rebuild to wipe state would silently leak. The
            // dedicated Clear() call in T4 covers this even when the
            // rebuild path is bypassed, but the rebuild path's own
            // behaviour still has to match — both belt and braces.
            SeedOneAnchor("prior-sess", "rec-A", sectionIndex: 0);
            Assert.Equal(1, RenderSessionState.Count);

            RenderSessionState.RebuildFromMarker(
                marker: null,
                recordings: new List<Recording>(),
                treeLookup: _ => default,
                liveWorldPositionProvider: _ => null);

            Assert.Equal(0, RenderSessionState.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Pipeline-Session]") && l.Contains("Clear: reason=marker-null"));
        }

        // ----- 3. T4 source-scan coverage check ---------------------------

        [Fact]
        public void Marker_NullAssignmentSites_AllPairWithClearCall()
        {
            // What makes it fail: a developer adds a new site that nulls
            // ActiveReFlySessionMarker without also calling
            // RenderSessionState.Clear. The map then survives the marker
            // clear and stale ε's leak into post-session renders. T4 added
            // the pairing at every existing site; this scan keeps future
            // additions honest without needing the full KSP lifecycle.
            //
            // Per .claude/CLAUDE.md the scan is fragile by construction —
            // a deliberately-paired clear at >5 lines distance, or one
            // wrapped behind a helper, is allowed via the WhitelistedSites
            // list below. Add an entry plus a one-line rationale when
            // intentional.
            string repoRoot = ResolveRepoRoot();
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "Could not locate Source/Parsek/ from xUnit working dir: "
                + AppContext.BaseDirectory);

            int total = 0;
            int paired = 0;
            var failures = new List<string>();

            foreach (string file in Directory.EnumerateFiles(
                sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                // Skip the in-game test fixtures — they intentionally null
                // the marker as test setup, not as production state
                // transitions, and have no rendering side effect.
                if (file.IndexOf(Path.Combine("InGameTests"),
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                // Skip ReconciliationBundle's struct initializer — that's
                // the *fresh-bundle* default value, never a live-scenario
                // mutation.
                if (file.EndsWith("ReconciliationBundle.cs",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(
                            "ActiveReFlySessionMarker = null",
                            StringComparison.Ordinal) < 0)
                        continue;

                    total++;

                    // Search ±8 lines (the window allows for short inline
                    // comment blocks between the null and the Clear;
                    // eight lines is still tight enough to catch a future
                    // site that simply forgot the pair).
                    int lo = Math.Max(0, i - 8);
                    int hi = Math.Min(lines.Length - 1, i + 8);
                    bool foundPair = false;
                    for (int j = lo; j <= hi; j++)
                    {
                        if (lines[j].IndexOf(
                                "RenderSessionState.Clear(",
                                StringComparison.Ordinal) >= 0)
                        {
                            foundPair = true;
                            break;
                        }
                    }

                    if (foundPair)
                    {
                        paired++;
                    }
                    else
                    {
                        failures.Add(
                            string.Format(
                                "{0}:{1}: ActiveReFlySessionMarker = null without RenderSessionState.Clear() within ±8 lines",
                                file.Substring(repoRoot.Length).TrimStart('/', '\\'),
                                i + 1));
                    }
                }
            }

            // Sanity: we expect at least the eight production sites we
            // hooked in T4 (LoadTimeSweep + 3× MergeJournalOrchestrator +
            // ParsekScenario.LoadRewindStagingState + RewindInvoker rollback
            // + SupersedeCommit + 3× RevertInterceptor + TreeDiscardPurge
            // ≥ 8). If this drops, a clear site was likely deleted without
            // the matching anchor-map clear migration.
            Assert.True(total >= 8,
                "Expected at least 8 ActiveReFlySessionMarker = null "
                + "production sites; found " + total
                + ". Did a refactor remove a clear site without preserving the pairing?");
            Assert.True(failures.Count == 0,
                "T4 contract violated: every ActiveReFlySessionMarker = null "
                + "must pair with a RenderSessionState.Clear(...) call "
                + "within ±8 lines. Unpaired sites:\n  - "
                + string.Join("\n  - ", failures));
        }

        // ----- helpers -----------------------------------------------------

        private void SeedOneAnchor(string sessionId, string recordingId, int sectionIndex)
        {
            // Drive the test overload of RebuildFromMarker with a minimal
            // synthetic tree so we deterministically end up with one anchor
            // entry. The lookup override returns lat/lon/alt as-Vector3d
            // (matches the RenderSessionStateTests fixture pattern) so the
            // body resolution succeeds without a CelestialBody.
            RenderSessionState.SurfaceLookupOverrideForTesting =
                (bodyName, lat, lon, alt) => new Vector3d(lat, lon, alt);

            var rOrigin = MakeRecording(recordingId + "-origin", 50.0, (0.0, 0.0, 70.0));
            var rSib    = MakeRecording(recordingId,            50.0, (1.0, 2.0, 70.0));
            var tree = new RecordingTree { Id = "tree-" + sessionId };
            var bp = new BranchPoint
            {
                Id = "bp-" + sessionId,
                UT = 50.0,
                Type = BranchPointType.Undock
            };
            tree.Recordings[rOrigin.RecordingId] = rOrigin;
            tree.Recordings[rSib.RecordingId] = rSib;
            bp.ChildRecordingIds.Add(rOrigin.RecordingId);
            bp.ChildRecordingIds.Add(rSib.RecordingId);
            tree.BranchPoints.Add(bp);

            var marker = new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = tree.Id,
                OriginChildRecordingId = rOrigin.RecordingId
            };

            RenderSessionState.RebuildFromMarker(
                marker,
                new List<Recording> { rOrigin, rSib },
                _ => new RecordingTreeContext(tree, bp),
                _ => new Vector3d(100.0, 200.0, 300.0));
        }

        private static Recording MakeRecording(
            string id, double bpUT, (double lat, double lon, double alt) atBp)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion
            };
            var bpPoint = new TrajectoryPoint
            {
                ut = bpUT,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            var laterPoint = new TrajectoryPoint
            {
                ut = bpUT + 50.0,
                latitude = atBp.lat,
                longitude = atBp.lon,
                altitude = atBp.alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
            rec.Points.Add(bpPoint);
            rec.Points.Add(laterPoint);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 0,
                endUT = bpUT + 100,
                anchorVesselId = 0u,
                frames = new List<TrajectoryPoint> { bpPoint, laterPoint },
                checkpoints = new List<OrbitSegment>(),
                source = TrackSectionSource.Active
            });
            return rec;
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Source"))
                    && Directory.Exists(Path.Combine(dir, "scripts")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
