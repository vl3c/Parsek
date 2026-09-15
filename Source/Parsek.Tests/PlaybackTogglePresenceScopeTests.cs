using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GUI-P1 (operator ruling 2026-09-14): the per-recording playback tick box in the
    /// Recordings tab means "no ghost appears ANYWHERE" - the flight ghost (already honoured
    /// before this change), the map icon, the orbit line, the Tracking Station row, the TS
    /// non-proto atmospheric marker and the map trajectory polyline.
    ///
    /// <para>These cells pin the FIVE decision seams the suppression rides on, plus the single
    /// writer that every toggle affordance routes through. The live map / TS surfaces
    /// themselves are proven by the GUI-9 harness lane - a unit test cannot see them.</para>
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackTogglePresenceScopeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody kerbin;

        public PlaybackTogglePresenceScopeTests()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            kerbin = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            GhostMapPresence.FindBodyByNameForTesting = bodyName => kerbin;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.ResetCommittedListNotificationsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- The shared pure predicate ---

        [Fact]
        public void IsMapPresenceHiddenByPlaybackToggle_DefaultRecording_NotHidden()
        {
            // PlaybackEnabled defaults to true, so an untouched recording keeps every surface.
            Assert.False(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(new Recording()));
        }

        [Fact]
        public void IsMapPresenceHiddenByPlaybackToggle_Unticked_Hidden()
        {
            Assert.True(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(
                new Recording { PlaybackEnabled = false }));
        }

        [Fact]
        public void IsMapPresenceHiddenByPlaybackToggle_NullTrajectory_NotHidden()
        {
            // A null trajectory is somebody else's skip reason ("null"), never this one.
            Assert.False(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(null));
        }

        [Fact]
        public void IsMapPresenceHiddenByPlaybackToggle_IsPerRecording_NeighbourUnaffected()
        {
            // Mirror direction for the SupersedeCommit flip (SupersedeCommit.cs sets
            // PlaybackEnabled = true on the merged Re-Fly provisional): the predicate reads
            // only the recording handed to it, so re-enabling one recording can never
            // resurrect a DIFFERENT hidden row's presence.
            var hidden = new Recording { RecordingId = "a", PlaybackEnabled = false };
            var provisional = new Recording { RecordingId = "b", PlaybackEnabled = false };

            provisional.PlaybackEnabled = true; // what SupersedeCommit.MergeProvisional does

            Assert.True(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(hidden));
            Assert.False(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(provisional));
        }

        // --- Seam 1: the proto-vessel source resolver (icon + orbit line + TS row) ---

        [Fact]
        public void ResolveMapPresenceGhostSource_PlaybackDisabled_DeclinesWithPlaybackDisabledReason()
        {
            Recording rec = BuildOrbitingRecording();
            rec.PlaybackEnabled = false;

            int cached = -1;
            GhostMapPresence.TrackingStationGhostSource source =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec,
                    isSuppressed: false,
                    alreadyMaterialized: false,
                    currentUT: 135.7,
                    allowTerminalOrbitFallback: true,
                    logOperationName: "test-playback-toggle",
                    ref cached,
                    out OrbitSegment segment,
                    out _,
                    out string skipReason);

            Assert.Equal(GhostMapPresence.TrackingStationGhostSource.None, source);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled, skipReason);
            Assert.Equal(default(OrbitSegment).semiMajorAxis, segment.semiMajorAxis);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("Map presence hidden by the playback tick box")
                && l.Contains("surface=map-presence")
                && l.Contains(rec.RecordingId));
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_PlaybackReEnabled_ResolvesASourceAgain()
        {
            // The mirror of the cell above on the SAME recording: the gate is a live read of
            // the flag, so re-ticking the box restores the source on the next resolve - no
            // cached "hidden" state to clear, which is what makes the restore immediate.
            Recording rec = BuildOrbitingRecording();

            rec.PlaybackEnabled = false;
            int cachedOff = -1;
            GhostMapPresence.ResolveMapPresenceGhostSource(
                rec, false, false, 135.7, true, null, ref cachedOff,
                out _, out _, out string offReason);
            Assert.Equal(GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled, offReason);

            rec.PlaybackEnabled = true;
            int cachedOn = -1;
            GhostMapPresence.TrackingStationGhostSource onSource =
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec, false, false, 135.7, true, null, ref cachedOn,
                    out _, out _, out string onReason);

            Assert.NotEqual(GhostMapPresence.TrackingStationGhostSource.None, onSource);
            Assert.NotEqual(GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled, onReason);
        }

        [Fact]
        public void ResolveMapPresenceGhostSource_PlaybackDisabledSuppressionLogsOncePerChange()
        {
            Recording rec = BuildOrbitingRecording();
            rec.PlaybackEnabled = false;

            for (int i = 0; i < 5; i++)
            {
                int cached = -1;
                GhostMapPresence.ResolveMapPresenceGhostSource(
                    rec, false, false, 135.7 + i, true, null, ref cached,
                    out _, out _, out _);
            }

            int suppressionLines = 0;
            for (int i = 0; i < logLines.Count; i++)
                if (logLines[i].Contains("Map presence hidden by the playback tick box"))
                    suppressionLines++;
            Assert.Equal(1, suppressionLines);
        }

        // --- Seam 2: the retire side ---

        [Fact]
        public void GetTrackingStationGhostRemovalReason_PlaybackDisabled_RetiresTheProto()
        {
            Recording rec = BuildOrbitingRecording();
            rec.PlaybackEnabled = false;

            Assert.Equal(
                GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled,
                GhostMapPresence.GetTrackingStationGhostRemovalReason(
                    rec, isSuppressed: false, hasOrbitBounds: true, currentUT: 135.7));
        }

        [Fact]
        public void GetTrackingStationGhostRemovalReason_PlaybackEnabled_KeepsTheProto()
        {
            Recording rec = BuildOrbitingRecording();

            Assert.Null(GhostMapPresence.GetTrackingStationGhostRemovalReason(
                rec, isSuppressed: false, hasOrbitBounds: true, currentUT: 135.7));
        }

        [Fact]
        public void GetTrackingStationGhostRemovalReason_NullRecording_KeepsItsOwnReason()
        {
            // The playback gate must not swallow the degenerate-teardown reason, which
            // ShouldAssertTerminalOrbitBoundClamp keys off.
            Assert.Equal(
                "tracking-station-recording-missing",
                GhostMapPresence.GetTrackingStationGhostRemovalReason(
                    null, isSuppressed: false, hasOrbitBounds: true, currentUT: 135.7));
        }

        // --- Seam 3: the TS non-proto atmospheric marker ---

        [Fact]
        public void ClassifyAtmosphericMarkerSkip_PlaybackDisabled_SkipsWithItsOwnReason()
        {
            Recording rec = BuildOrbitingRecording();
            rec.PlaybackEnabled = false;

            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.PlaybackDisabled,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 300.0, suppressedIds: null));
        }

        [Fact]
        public void ClassifyAtmosphericMarkerSkip_PlaybackDisabledBeatsDebris()
        {
            // Ordering proof: the player's choice is reported over the structural reason, so a
            // log reader sees WHY the surface went away rather than an unrelated reason that
            // was already true before the box was unticked.
            Recording rec = BuildOrbitingRecording();
            rec.IsDebris = true;
            rec.PlaybackEnabled = false;

            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.PlaybackDisabled,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 300.0, suppressedIds: null));
        }

        [Fact]
        public void ClassifyAtmosphericMarkerSkip_PlaybackEnabled_DrawsTheMarker()
        {
            Recording rec = BuildOrbitingRecording();
            rec.OrbitSegments = null; // no covering conic, so no OrbitSegmentActive veto

            Assert.Equal(
                ParsekTrackingStation.AtmosphericMarkerSkipReason.None,
                ParsekTrackingStation.ClassifyAtmosphericMarkerSkip(
                    rec, recordingIndex: 0, currentUT: 300.0, suppressedIds: null));
        }

        [Fact]
        public void AtmosphericMarkerSkipReasonToken_PlaybackDisabled_MatchesTheSharedReasonString()
        {
            // One reason string across the map-presence resolver and the TS marker taxonomy,
            // so a single grep finds every surface the toggle hid.
            Assert.Equal(
                GhostMapPresence.TrackingStationGhostSkipPlaybackDisabled,
                ParsekTrackingStation.AtmosphericMarkerSkipReasonToken(
                    ParsekTrackingStation.AtmosphericMarkerSkipReason.PlaybackDisabled));
        }

        [Fact]
        public void FormatAtmosphericMarkerSummary_CarriesThePlaybackDisabledBucket()
        {
            var summary = default(ParsekTrackingStation.AtmosphericMarkerSummary);
            summary.EventTypeName = "test";
            summary.PlaybackDisabled = 7;

            Assert.True(summary.HasSignal);
            Assert.Contains(
                "playbackDisabled=7",
                ParsekTrackingStation.FormatAtmosphericMarkerSummary(summary));
        }

        // --- The flight-map marker aggregate's own bucket ---

        [Fact]
        public void FormatMapMarkerSummary_CarriesThePlaybackDisabledBucket()
        {
            var summary = new ParsekUI.MapMarkerSummary { IsMapView = true, PlaybackDisabled = 243 };

            // HasSignal is the whole point: the aggregate is suppressed when the pass has no
            // signal, and with EVERY recording hidden there is nothing else to count - so
            // without this bucket a `drawn=0` reading is unreachable and the line cannot say
            // that it drew nothing BECAUSE the player unticked them. Measured on harness run
            // 2026-09-14_2133, which red on exactly that unreachable claim.
            Assert.True(summary.HasSignal);
            string line = ParsekUI.FormatMapMarkerSummary(summary);
            Assert.Contains("view=map", line);
            Assert.Contains("drawn=0", line);
            Assert.Contains("playbackDisabled=243", line);
        }

        [Fact]
        public void FormatMapMarkerSummary_NoPlaybackDisabled_KeepsTheBucketAtZeroAndStaysSignalLess()
        {
            var summary = new ParsekUI.MapMarkerSummary { IsMapView = true };

            Assert.False(summary.HasSignal);
            Assert.Contains("playbackDisabled=0", ParsekUI.FormatMapMarkerSummary(summary));
        }

        // --- Seam 4: the map trajectory polyline ---

        [Fact]
        public void ClassifyPolylineStaticSkip_PlaybackDisabled_SkipsTheWholeRecordingsLine()
        {
            Recording rec = BuildOrbitingRecording();
            rec.PlaybackEnabled = false;

            Assert.Equal(
                Parsek.Display.GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.PlaybackDisabled,
                Parsek.Display.GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(rec, null));
        }

        [Fact]
        public void ClassifyPolylineStaticSkip_PlaybackEnabled_DrawsTheLine()
        {
            Assert.Equal(
                Parsek.Display.GhostTrajectoryPolylineRenderer.PolylineStaticSkipReason.None,
                Parsek.Display.GhostTrajectoryPolylineRenderer.ClassifyPolylineStaticSkip(
                    BuildOrbitingRecording(), null));
        }

        // --- The single writer ---

        [Fact]
        public void SetRecordingPlaybackEnabled_Flip_WritesNotifiesAndLogs()
        {
            Recording rec = BuildOrbitingRecording();
            var events = new List<(string id, bool enabled)>();
            RecordingStore.RecordingPlaybackEnabledChanged +=
                (r, enabled) => events.Add((r?.RecordingId, enabled));
            try
            {
                Assert.True(RecordingStore.SetRecordingPlaybackEnabled(rec, false));
            }
            finally
            {
                RecordingStore.ResetCommittedListNotificationsForTesting();
            }

            Assert.False(rec.PlaybackEnabled);
            Assert.Single(events);
            Assert.Equal(rec.RecordingId, events[0].id);
            Assert.False(events[0].enabled);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Recording playback disabled")
                && l.Contains(rec.RecordingId));
        }

        [Fact]
        public void SetRecordingPlaybackEnabled_UnchangedValue_IsASilentNoOp()
        {
            // The toggles sit in an IMGUI redraw; writing the same value every frame must not
            // log or notify.
            Recording rec = BuildOrbitingRecording();
            int notifications = 0;
            RecordingStore.RecordingPlaybackEnabledChanged += (r, enabled) => notifications++;
            try
            {
                Assert.False(RecordingStore.SetRecordingPlaybackEnabled(rec, true));
            }
            finally
            {
                RecordingStore.ResetCommittedListNotificationsForTesting();
            }

            Assert.True(rec.PlaybackEnabled);
            Assert.Equal(0, notifications);
            Assert.DoesNotContain(logLines, l => l.Contains("Recording playback"));
        }

        [Fact]
        public void SetRecordingPlaybackEnabled_NullRecording_IsASilentNoOp()
        {
            int notifications = 0;
            RecordingStore.RecordingPlaybackEnabledChanged += (r, enabled) => notifications++;
            try
            {
                Assert.False(RecordingStore.SetRecordingPlaybackEnabled(null, false));
            }
            finally
            {
                RecordingStore.ResetCommittedListNotificationsForTesting();
            }
            Assert.Equal(0, notifications);
        }

        [Fact]
        public void SetRecordingPlaybackEnabled_BulkFlip_HidesEveryMemberIdentically()
        {
            // Mirror direction for the four bulk affordances (select-all header, two group
            // headers, chain block): they all loop this one writer, so a group flip and a row
            // flip leave the recordings in the same state - no per-affordance behaviour.
            var members = new List<Recording>
            {
                BuildOrbitingRecording("rec_bulk_0"),
                BuildOrbitingRecording("rec_bulk_1"),
                BuildOrbitingRecording("rec_bulk_2")
            };
            members[1].PlaybackEnabled = false; // a mixed group before the flip

            int changed = 0;
            for (int i = 0; i < members.Count; i++)
                if (RecordingStore.SetRecordingPlaybackEnabled(members[i], false)) changed++;

            Assert.Equal(2, changed); // the already-off member was a no-op
            for (int i = 0; i < members.Count; i++)
                Assert.True(GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(members[i]));
        }

        // --- fixtures ---

        private static Recording BuildOrbitingRecording(
            string recordingId = "rec_4f1b9c5d2ae44e2a9d3b7c81ef0a6d33")
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = "Playback Toggle Probe",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 600.0,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 4_547_677.0,
                TerminalOrbitEccentricity = 0.822,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        altitude = 80_000.0,
                        velocity = new Vector3(1000f, 0f, 0f)
                    },
                    new TrajectoryPoint
                    {
                        ut = 600.0,
                        bodyName = "Kerbin",
                        altitude = 208_283.0,
                        velocity = new Vector3(296.0f, 3.8f, -2806.1f)
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Kerbin",
                        startUT = 120.0,
                        endUT = 500.0,
                        semiMajorAxis = 812_941.0,
                        eccentricity = 0.1746,
                        inclination = 0.0977,
                        longitudeOfAscendingNode = 75.6,
                        argumentOfPeriapsis = 342.3,
                        meanAnomalyAtEpoch = 1.6818,
                        epoch = 120.0
                    }
                }
            };
        }
    }
}
