using System.Collections.Generic;
using System.Security;
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
    }
}
