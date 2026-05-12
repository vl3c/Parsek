using System.Collections.Generic;
using System.Security;
using Parsek.Patches;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TrackingStationControlSurfaceUITests
    {
        [Fact]
        public void BuildControlSurfaceState_CountsRecordingsAndMaterializedVessels()
        {
            var committed = new List<Recording>
            {
                new Recording(),
                new Recording { VesselSpawned = true },
                new Recording { SpawnedVesselPersistentId = 42 },
                null
            };

            ParsekTrackingStation.TrackingStationControlSurfaceState state =
                ParsekTrackingStation.BuildControlSurfaceState(
                    committed,
                    visibleGhostVessels: 3,
                    suppressedRecordings: 2,
                    showGhosts: true);

            Assert.Equal(4, state.CommittedRecordings);
            Assert.Equal(3, state.VisibleGhostVessels);
            Assert.Equal(2, state.SuppressedRecordings);
            Assert.Equal(2, state.MaterializedRecordings);
            Assert.True(state.ShowGhosts);
        }

        [Fact]
        public void BuildControlSurfaceState_ClampsNegativeExternalCounts()
        {
            ParsekTrackingStation.TrackingStationControlSurfaceState state =
                ParsekTrackingStation.BuildControlSurfaceState(
                    committed: null,
                    visibleGhostVessels: -4,
                    suppressedRecordings: -2,
                    showGhosts: false);

            Assert.Equal(0, state.CommittedRecordings);
            Assert.Equal(0, state.VisibleGhostVessels);
            Assert.Equal(0, state.SuppressedRecordings);
            Assert.Equal(0, state.MaterializedRecordings);
            Assert.False(state.ShowGhosts);
        }

        [Fact]
        public void FormatControlSurfaceLines_UsesCompactStableLabels()
        {
            var state = new ParsekTrackingStation.TrackingStationControlSurfaceState
            {
                CommittedRecordings = 7,
                VisibleGhostVessels = 3,
                SuppressedRecordings = 2,
                MaterializedRecordings = 1,
                ShowGhosts = true
            };

            Assert.Equal(
                "Recordings: 7 | Map ghosts: 3",
                ParsekTrackingStation.FormatControlSurfaceCountsLine(state));
            Assert.Equal(
                "Suppressed: 2 | Materialized: 1",
                ParsekTrackingStation.FormatControlSurfaceLifecycleLine(state));
        }

        [Fact]
        public void BuildGhostPopupText_UsesNativeMenuStatusLabels()
        {
            var selection = new TrackingStationGhostSelectionInfo(
                42u,
                "Ghost: Mun Return",
                1,
                "rec-popup",
                100.0,
                200.0,
                TerminalState.Landed,
                false,
                0u,
                hasRecording: true);

            string text = ParsekTrackingStation.BuildGhostPopupText(selection, currentUT: 250.0);

            Assert.Contains("Name: Mun Return", text);
            Assert.Contains("Recording: endpoint reached", text);
            Assert.Contains("End state: Landed", text);
            Assert.DoesNotContain("Ghost: Ghost:", text);
            Assert.DoesNotContain("Terminal", text);
        }

        [Fact]
        public void FormatGhostPopupVesselName_StripsStockGhostPrefixes()
        {
            Assert.Equal(
                "Mun Return",
                ParsekTrackingStation.FormatGhostPopupVesselName("Ghost: Ghost: Mun Return"));
            Assert.Equal("(ghost)", ParsekTrackingStation.FormatGhostPopupVesselName("Ghost: "));
        }

        [Fact]
        public void BuildGhostPopupText_BeforeEndpoint_ShowsStableEndpointStatus()
        {
            var selection = new TrackingStationGhostSelectionInfo(
                0u,
                "Plane Ghost",
                2,
                "rec-atmo",
                10.0,
                25.5,
                TerminalState.Destroyed,
                false,
                0u,
                hasRecording: true);

            string text = ParsekTrackingStation.BuildGhostPopupText(selection, currentUT: 20.0);

            Assert.Contains("Recording: before endpoint", text);
            Assert.Contains("End state: Destroyed", text);
        }

        [Fact]
        public void BuildMaterializeButtonLabel_FastForwardMaterialize_ShowsLiveWarpDuration()
        {
            var selection = new TrackingStationGhostSelectionInfo(
                0u,
                "Plane Ghost",
                2,
                "rec-atmo",
                10.0,
                85.5,
                TerminalState.Destroyed,
                false,
                0u,
                hasRecording: true);
            var context = new TrackingStationGhostActionContext(
                hasGhostVessel: false,
                canFocus: true,
                canSetTarget: false,
                recordingIndex: 2,
                hasRecording: true,
                materializeEligible: false,
                materializeReason: GhostMapPresence.TrackingStationSpawnSkipBeforeEnd,
                alreadyMaterialized: false,
                materializeFastForwardEligible: true);

            string label = ParsekTrackingStation.BuildMaterializeButtonLabel(
                "Materialize",
                selection,
                context,
                currentUT: 20.0);

            Assert.Equal("Materialize (1m 5s)", label);
        }

        [Fact]
        public void BuildGhostPopupStatusPhase_ChangesAtEndpoint()
        {
            var selection = new TrackingStationGhostSelectionInfo(
                0u,
                "Plane Ghost",
                2,
                "rec-atmo",
                10.0,
                25.5,
                TerminalState.Destroyed,
                false,
                0u,
                hasRecording: true);

            Assert.Equal(
                "before-end",
                ParsekTrackingStation.BuildGhostPopupStatusPhase(selection, 25.4));
            Assert.Equal(
                "endpoint",
                ParsekTrackingStation.BuildGhostPopupStatusPhase(selection, 25.5));
        }

        [Fact]
        public void FormatAtmosphericMarkerStockActionLockLine_IncludesDisabledButtonsAndClearError()
        {
            string line = ParsekTrackingStation.FormatAtmosphericMarkerStockActionLockLine(
                clearedSelection: true,
                hadPreviousSelection: true,
                flyDisabled: true,
                deleteDisabled: false,
                recoverDisabled: true,
                clearError: "selectedVessel field not found",
                source: "atmospheric marker");

            Assert.Contains("clearedSelection=True", line);
            Assert.Contains("hadPreviousSelection=True", line);
            Assert.Contains("flyDisabled=True", line);
            Assert.Contains("deleteDisabled=False", line);
            Assert.Contains("recoverDisabled=True", line);
            Assert.Contains("clearError=selectedVessel field not found", line);
            Assert.Contains("source=atmospheric marker", line);
        }

        [Fact]
        public void ShouldOpenSelectedGhostPopup_UsesSelectionIntent()
        {
            var popupSelection = new TrackingStationGhostSelectionInfo(
                42u,
                "Ghost: Icon",
                1,
                "rec-icon",
                0.0,
                10.0,
                TerminalState.Landed,
                false,
                0u,
                hasRecording: true,
                showPopup: true);
            var focusOnlySelection = new TrackingStationGhostSelectionInfo(
                43u,
                "Ghost: List",
                2,
                "rec-list",
                0.0,
                10.0,
                TerminalState.Landed,
                false,
                0u,
                hasRecording: true,
                showPopup: false);

            Assert.True(ParsekTrackingStation.ShouldOpenSelectedGhostPopup(popupSelection));
            Assert.False(ParsekTrackingStation.ShouldOpenSelectedGhostPopup(focusOnlySelection));
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_RelativeSectionUsesBodyFixedPrimaryFrames()
        {
            var shadowFrames = new List<TrajectoryPoint>
            {
                Point(100.0, 1.0, 2.0, 1000.0),
                Point(110.0, 1.5, 2.5, 1200.0)
            };
            var rec = new Recording
            {
                RecordingFormatVersion = RecordingStore.RelativeBodyFixedPrimaryFormatVersion,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100.0,
                        endUT = 110.0,
                        frames = new List<TrajectoryPoint>
                        {
                            Point(100.0, 500.0, 600.0, 700.0),
                            Point(110.0, 510.0, 610.0, 710.0)
                        },
                        bodyFixedFrames = shadowFrames
                    }
                }
            };

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                105.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.True(selected);
            Assert.Same(shadowFrames, frames);
            Assert.Null(reason);
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_RelativeSectionWithoutBodyFixedPrimaryFailsClosed()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    Point(100.0, 1.0, 2.0, 1000.0),
                    Point(110.0, 1.5, 2.5, 1200.0)
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100.0,
                        endUT = 110.0,
                        frames = new List<TrajectoryPoint>
                        {
                            Point(100.0, 500.0, 600.0, 700.0),
                            Point(110.0, 510.0, 610.0, 710.0)
                        }
                    }
                }
            };

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                105.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.False(selected);
            Assert.Null(frames);
            Assert.Equal("relative-body-fixed-primary-out-of-range", reason);
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_AfterRelativeSectionRejectsStaleBodyFixedPrimaryFrames()
        {
            var shadowFrames = new List<TrajectoryPoint>
            {
                Point(100.0, 1.0, 2.0, 1000.0),
                Point(110.0, 1.5, 2.5, 1200.0)
            };
            var rec = new Recording
            {
                RecordingFormatVersion = RecordingStore.RelativeBodyFixedPrimaryFormatVersion,
                Points = new List<TrajectoryPoint>
                {
                    Point(100.0, 500.0, 600.0, 700.0),
                    Point(110.0, 510.0, 610.0, 710.0)
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100.0,
                        endUT = 110.0,
                        frames = new List<TrajectoryPoint>
                        {
                            Point(100.0, 500.0, 600.0, 700.0),
                            Point(110.0, 510.0, 610.0, 710.0)
                        },
                        bodyFixedFrames = shadowFrames
                    }
                }
            };

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                120.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.False(selected);
            Assert.Null(frames);
            Assert.Equal("relative-body-fixed-primary-out-of-range", reason);
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_BeforeRelativeSectionWithoutShadowFailsClosed()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    Point(100.0, 1.0, 2.0, 1000.0),
                    Point(110.0, 1.5, 2.5, 1200.0)
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100.0,
                        endUT = 110.0,
                        frames = new List<TrajectoryPoint>
                        {
                            Point(100.0, 500.0, 600.0, 700.0),
                            Point(110.0, 510.0, 610.0, 710.0)
                        }
                    }
                }
            };

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                90.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.False(selected);
            Assert.Null(frames);
            Assert.Equal("relative-body-fixed-primary-out-of-range", reason);
        }

        [Fact]
        public void TrySelectTrackingStationFocusFrames_OrbitalCheckpointFailsClosed()
        {
            var rec = new Recording
            {
                Points = new List<TrajectoryPoint>
                {
                    Point(100.0, 1.0, 2.0, 1000.0),
                    Point(110.0, 1.5, 2.5, 1200.0)
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                        startUT = 100.0,
                        endUT = 110.0,
                        checkpoints = new List<OrbitSegment>()
                    }
                }
            };

            bool selected = ParsekTrackingStation.TrySelectTrackingStationFocusFrames(
                rec,
                105.0,
                out List<TrajectoryPoint> frames,
                out string reason);

            Assert.False(selected);
            Assert.Null(frames);
            Assert.Equal("checkpoint-section", reason);
        }

        [Fact]
        public void TryApplyGhostVisibilitySetting_UpdatesLiveSettingsWhenPresent()
        {
            var settings = new ParsekSettings { showGhostsInTrackingStation = true };

            bool applied = ParsekTrackingStation.TryApplyGhostVisibilitySetting(
                settings,
                showGhosts: false);

            Assert.True(applied);
            Assert.False(settings.showGhostsInTrackingStation);
        }

        [Fact]
        public void TryApplyGhostVisibilitySetting_NullSettings_ReturnsFalse()
        {
            bool applied = ParsekTrackingStation.TryApplyGhostVisibilitySetting(
                null,
                showGhosts: false);

            Assert.False(applied);
        }

        [Theory]
        [InlineData(EventType.Repaint, false, true)]
        [InlineData(EventType.MouseDown, false, true)]
        [InlineData(EventType.MouseUp, false, true)]
        [InlineData(EventType.MouseDown, true, false)]
        [InlineData(EventType.MouseUp, true, false)]
        [InlineData(EventType.Layout, false, false)]
        public void ShouldProcessAtmosphericMarkerEvent_OnlyPassesAllowedCases(
            EventType eventType,
            bool pointerOverParsekWindow,
            bool expected)
        {
            Assert.Equal(
                expected,
                ParsekTrackingStation.ShouldProcessAtmosphericMarkerEvent(
                    eventType,
                    pointerOverParsekWindow));
        }

        [Theory]
        [InlineData(EventType.MouseDown, true, true)]
        [InlineData(EventType.MouseUp, true, true)]
        [InlineData(EventType.Repaint, true, false)]
        [InlineData(EventType.MouseDown, false, false)]
        [InlineData(EventType.Layout, true, false)]
        public void ShouldBlockAtmosphericMarkerClickForGhostPopup_OnlyBlocksClickEvents(
            EventType eventType,
            bool pointerOverGhostPopup,
            bool expected)
        {
            Assert.Equal(
                expected,
                ParsekTrackingStation.ShouldBlockAtmosphericMarkerClickForGhostPopup(
                    eventType,
                    pointerOverGhostPopup));
        }

        [Fact]
        public void IsPointerOverOpenWindow_RequiresOpenSizedRectContainingMouse()
        {
            Rect rect = new Rect(20f, 40f, 120f, 80f);
            Vector2 inside = new Vector2(60f, 70f);
            Vector2 outside = new Vector2(200f, 70f);

            Assert.True(ParsekUI.IsPointerOverOpenWindow(true, rect, inside));
            Assert.False(ParsekUI.IsPointerOverOpenWindow(false, rect, inside));
            Assert.False(ParsekUI.IsPointerOverOpenWindow(true, default, inside));
            Assert.False(ParsekUI.IsPointerOverOpenWindow(true, rect, outside));
        }

        [Theory]
        [InlineData(UIMode.Flight, true)]
        [InlineData(UIMode.KSC, true)]
        [InlineData(UIMode.TrackingStation, false)]
        public void CanOfferGhostOnlyDelete_MatchesSceneCompatibility(UIMode mode, bool expected)
        {
            Assert.Equal(expected, RecordingsTableUI.CanOfferGhostOnlyDelete(mode));
        }

        [Fact]
        public void ParsekUI_TrackingStationCtor_ExposesReusableRecordingsAndSettingsWindows()
        {
            var ui = new ParsekUI(UIMode.TrackingStation);
            try
            {
                Assert.NotNull(ui.GetRecordingsTableUI());
                Assert.NotNull(ui.GetSettingsWindowUI());

                Assert.False(ui.GetRecordingsTableUI().IsOpen);
                Assert.False(ui.GetSettingsWindowUI().IsOpen);

                ui.ToggleRecordingsWindow();
                ui.ToggleSettingsWindow();

                Assert.True(ui.GetRecordingsTableUI().IsOpen);
                Assert.True(ui.GetSettingsWindowUI().IsOpen);
            }
            finally
            {
                try
                {
                    ui.Cleanup();
                }
                catch (SecurityException)
                {
                    // Headless xUnit can still lack Unity GUI teardown; this test only
                    // cares about Tracking Station window wiring.
                }
            }
        }

        private static TrajectoryPoint Point(
            double ut,
            double latitude,
            double longitude,
            double altitude)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                bodyName = "Kerbin",
                latitude = latitude,
                longitude = longitude,
                altitude = altitude
            };
        }
    }
}
