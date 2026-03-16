using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CameraFollowTests
    {
        public CameraFollowTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region IsVesselSituationSafe

        [Fact]
        public void IsVesselSituationSafe_Landed_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.LANDED, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Splashed_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.SPLASHED, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Prelaunch_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.PRELAUNCH, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Docked_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.DOCKED, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_SafePeriapsis_ReturnsTrue()
        {
            // Periapsis above atmosphere: safe
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 100000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_LowPeriapsis_ReturnsFalse()
        {
            // Periapsis inside atmosphere: unsafe
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 50000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_ExactlyAtAtmosphere_ReturnsFalse()
        {
            // Periapsis exactly at atmosphere boundary: unsafe (not strictly above)
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 70000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Flying_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.FLYING, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_SubOrbital_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.SUB_ORBITAL, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Escaping_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ESCAPING, 0, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_NoAtmosphere_ReturnsTrue()
        {
            // Body without atmosphere (atmoHeight=0), any periapsis > 0 is safe
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 10000, 0));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_NegativePeriapsis_ReturnsFalse()
        {
            // Periapsis below surface (negative altitude): unsafe regardless of atmosphere
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, -1000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_JustAboveAtmosphere_ReturnsTrue()
        {
            // Periapsis just barely above atmosphere boundary: safe
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 70001, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_ZeroPeZeroAtmo_ReturnsFalse()
        {
            // Airless body with zero periapsis: 0 > 0 is false, so unsafe
            // (orbit grazes the surface)
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 0, 0));
        }

        [Fact]
        public void IsVesselSituationSafe_Landed_IgnoresPeriapsisAndAtmo()
        {
            // Surface situations are safe regardless of periapsis/atmosphere values
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.LANDED, -50000, 70000));
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.LANDED, double.NaN, double.NaN));
        }

        [Fact]
        public void IsVesselSituationSafe_Orbiting_VeryHighPeriapsis_ReturnsTrue()
        {
            // Very high orbit (e.g., Minmus transfer): clearly safe
            Assert.True(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ORBITING, 46000000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Flying_IgnoresPeriapsis()
        {
            // Flying is always unsafe, even with high periapsis values
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.FLYING, 100000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_SubOrbital_IgnoresPeriapsis()
        {
            // Sub-orbital is always unsafe, even with high periapsis values
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.SUB_ORBITAL, 100000, 70000));
        }

        [Fact]
        public void IsVesselSituationSafe_Escaping_IgnoresPeriapsis()
        {
            // Escaping is always unsafe, even with high periapsis values
            Assert.False(ParsekFlight.IsVesselSituationSafe(
                Vessel.Situations.ESCAPING, 100000, 70000));
        }

        #endregion

        #region ComputeWatchIndexAfterDelete

        private List<Recording> MakeRecordings(params string[] ids)
        {
            var list = new List<Recording>();
            foreach (var id in ids)
            {
                var rec = new Recording();
                rec.RecordingId = id;
                rec.VesselName = "Vessel_" + id;
                list.Add(rec);
            }
            return list;
        }

        [Fact]
        public void ComputeWatchIndex_DeleteBelow_ShiftsDown()
        {
            // Watching index 3 ("rec_d"), delete index 1 ("rec_b")
            // After deletion, list is [a, c, d, e] — "rec_d" is now at index 2
            var recordings = MakeRecordings("rec_a", "rec_c", "rec_d", "rec_e");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(3, "rec_d", 1, recordings);
            Assert.Equal(2, result.newIndex);
            Assert.Equal("rec_d", result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_DeleteAtWatched_ExitsToMinusOne()
        {
            // Watching index 2, delete index 2 — should exit watch mode
            var recordings = MakeRecordings("rec_a", "rec_b", "rec_d");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(2, "rec_c", 2, recordings);
            Assert.Equal(-1, result.newIndex);
            Assert.Null(result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_DeleteAbove_NoChange()
        {
            // Watching index 1 ("rec_b"), delete index 3 ("rec_d")
            // After deletion, list is [a, b, c] — "rec_b" still at index 1
            var recordings = MakeRecordings("rec_a", "rec_b", "rec_c");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(1, "rec_b", 3, recordings);
            Assert.Equal(1, result.newIndex);
            Assert.Equal("rec_b", result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_IdStableAfterShift()
        {
            // Watching index 3 ("rec_d"), delete index 0 ("rec_a")
            // After deletion, list is [b, c, d, e] — "rec_d" at index 2
            var recordings = MakeRecordings("rec_b", "rec_c", "rec_d", "rec_e");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(3, "rec_d", 0, recordings);
            Assert.Equal(2, result.newIndex);
            Assert.Equal("rec_d", result.newId);
            // Verify the recording at the returned index actually has the right ID
            Assert.Equal("rec_d", recordings[result.newIndex].RecordingId);
        }

        [Fact]
        public void ComputeWatchIndex_IdMismatch_ScansForCorrect()
        {
            // Simulate a scenario where the index-based lookup would give wrong ID
            // Watching index 2 ("rec_c"), delete index 1
            // After deletion, list is [a, X, c] where X is some other recording
            // The index-1=1 gives "X", not "rec_c". Should scan and find at index 2.
            var recordings = MakeRecordings("rec_a", "rec_x", "rec_c");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(2, "rec_c", 1, recordings);
            Assert.Equal(2, result.newIndex);
            Assert.Equal("rec_c", result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_IdNotFound_ExitsToMinusOne()
        {
            // Watched recording no longer in the list at all
            var recordings = MakeRecordings("rec_a", "rec_b");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(2, "rec_gone", 0, recordings);
            Assert.Equal(-1, result.newIndex);
            Assert.Null(result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_DeleteFirstWhenWatchingFirst_ExitsToMinusOne()
        {
            // Watching index 0, delete index 0 — watched recording itself is deleted
            var recordings = MakeRecordings("rec_b", "rec_c");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(0, "rec_a", 0, recordings);
            Assert.Equal(-1, result.newIndex);
            Assert.Null(result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_EmptyListAfterDelete_ExitsToMinusOne()
        {
            // After deletion, no recordings remain — ID not found, should exit
            var recordings = new List<Recording>();
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(1, "rec_a", 0, recordings);
            Assert.Equal(-1, result.newIndex);
            Assert.Null(result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_WatchingLast_DeleteFirst_ShiftsDown()
        {
            // Watching last element (index 1, "rec_b"), delete first (index 0, "rec_a")
            // After deletion, list is [b] — "rec_b" at index 0
            var recordings = MakeRecordings("rec_b");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(1, "rec_b", 0, recordings);
            Assert.Equal(0, result.newIndex);
            Assert.Equal("rec_b", result.newId);
            Assert.Equal("rec_b", recordings[result.newIndex].RecordingId);
        }

        [Fact]
        public void ComputeWatchIndex_AdjacentDeleteBelow_ShiftsDownByOne()
        {
            // Watching index 1 ("rec_b"), delete index 0 ("rec_a")
            // After deletion, list is [b, c] — "rec_b" at index 0
            var recordings = MakeRecordings("rec_b", "rec_c");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(1, "rec_b", 0, recordings);
            Assert.Equal(0, result.newIndex);
            Assert.Equal("rec_b", result.newId);
            Assert.Equal("rec_b", recordings[result.newIndex].RecordingId);
        }

        [Fact]
        public void ComputeWatchIndex_DeleteFarAbove_NoChange()
        {
            // Watching index 0, delete index 10 (far above)
            // After deletion, list is [a, b, c] — "rec_a" still at index 0
            var recordings = MakeRecordings("rec_a", "rec_b", "rec_c");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(0, "rec_a", 10, recordings);
            Assert.Equal(0, result.newIndex);
            Assert.Equal("rec_a", result.newId);
        }

        [Fact]
        public void ComputeWatchIndex_LargeList_DeleteFirst_ShiftsCorrectly()
        {
            // Watching index 4 ("rec_e") in a 5-item list, delete index 0 ("rec_a")
            // After deletion, list is [b, c, d, e] — "rec_e" at index 3
            var recordings = MakeRecordings("rec_b", "rec_c", "rec_d", "rec_e");
            var result = ParsekFlight.ComputeWatchIndexAfterDelete(4, "rec_e", 0, recordings);
            Assert.Equal(3, result.newIndex);
            Assert.Equal("rec_e", result.newId);
            Assert.Equal("rec_e", recordings[result.newIndex].RecordingId);
        }

        #endregion

        #region ComputeWatchCycleOnLoopRebuild

        [Fact]
        public void WatchCycleOnRebuild_NotWatching_Unchanged()
        {
            // Not watching — value passes through unchanged
            Assert.Equal(5, ParsekFlight.ComputeWatchCycleOnLoopRebuild(5, false, true, false));
            Assert.Equal(-1, ParsekFlight.ComputeWatchCycleOnLoopRebuild(-1, false, false, false));
        }

        [Fact]
        public void WatchCycleOnRebuild_Watching_NeedsExplosion_NotPaused_ReturnsHold()
        {
            // Watching, fresh explosion needed, not in pause window → -2 (hold)
            Assert.Equal(-2, ParsekFlight.ComputeWatchCycleOnLoopRebuild(3, true, true, false));
        }

        [Fact]
        public void WatchCycleOnRebuild_Watching_NeedsExplosion_InPauseWindow_ReturnsReady()
        {
            // Watching, explosion needed but we're in pause window → -1 (ready for re-target)
            Assert.Equal(-1, ParsekFlight.ComputeWatchCycleOnLoopRebuild(3, true, true, true));
        }

        [Fact]
        public void WatchCycleOnRebuild_Watching_ExplosionAlreadyFired_ReturnsReady()
        {
            // Watching, explosion already fired (e.g. during pause window with time warp) → -1
            Assert.Equal(-1, ParsekFlight.ComputeWatchCycleOnLoopRebuild(3, true, false, false));
        }

        [Fact]
        public void WatchCycleOnRebuild_Watching_NoExplosionTerminal_ReturnsReady()
        {
            // Watching, non-explosion terminal state (e.g. Landed) → -1
            Assert.Equal(-1, ParsekFlight.ComputeWatchCycleOnLoopRebuild(0, true, false, false));
        }

        [Fact]
        public void WatchCycleOnRebuild_Watching_AlreadyInHold_StaysInHold()
        {
            // Already in hold (-2) — don't restart, let existing hold expire
            Assert.Equal(-2, ParsekFlight.ComputeWatchCycleOnLoopRebuild(-2, true, false, false));
            // Even if a new explosion is needed, don't restart the hold
            Assert.Equal(-2, ParsekFlight.ComputeWatchCycleOnLoopRebuild(-2, true, true, false));
        }

        #endregion
    }
}
