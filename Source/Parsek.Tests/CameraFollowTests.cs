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

        #endregion

        #region ComputeWatchIndexAfterDelete

        private List<RecordingStore.Recording> MakeRecordings(params string[] ids)
        {
            var list = new List<RecordingStore.Recording>();
            foreach (var id in ids)
            {
                var rec = new RecordingStore.Recording();
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

        #endregion
    }
}
