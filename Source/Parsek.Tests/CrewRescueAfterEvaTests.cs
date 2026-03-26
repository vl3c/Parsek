using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #137: crew status corruption — reserved kerbals set to Missing
    /// after EVA vessel removal. ShouldRescueFromMissing is the pure decision method;
    /// RescueReservedCrewAfterEvaRemoval is the runtime rescue path.
    /// </summary>
    [Collection("Sequential")]
    public class CrewRescueAfterEvaTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewRescueAfterEvaTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        [Fact]
        public void ShouldRescueFromMissing_MissingAndReserved_ReturnsTrue()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Valentina", replacements);

            Assert.True(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_MissingButNotReserved_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Bob", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_AssignedAndReserved_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Assigned, "Valentina", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_AvailableAndReserved_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Available, "Valentina", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_DeadAndReserved_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Dead, "Valentina", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_NullName_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, null, replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_EmptyName_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string> { { "Valentina", "Agasel" } };

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_EmptyReplacements_ReturnsFalse()
        {
            var replacements = new Dictionary<string, string>();

            bool result = CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Valentina", replacements);

            Assert.False(result);
        }

        [Fact]
        public void ShouldRescueFromMissing_MultipleReserved_OnlyMatchingReturnsTrue()
        {
            var replacements = new Dictionary<string, string>
            {
                { "Valentina", "Agasel" },
                { "Jebediah", "Kirrim" }
            };

            Assert.True(CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Valentina", replacements));
            Assert.True(CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Jebediah", replacements));
            Assert.False(CrewReservationManager.ShouldRescueFromMissing(
                ProtoCrewMember.RosterStatus.Missing, "Bob", replacements));
        }
    }
}
