using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #167: crew swap not executed for KSC-spawned vessels.
    /// SwapReservedCrewInSnapshot is the pure method that replaces reserved
    /// crew names in a vessel snapshot ConfigNode before spawning at KSC.
    /// </summary>
    [Collection("Sequential")]
    public class KscCrewSwapTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscCrewSwapTests()
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

        #region SwapReservedCrewInSnapshot — basic cases

        [Fact]
        public void SwapReservedCrewInSnapshot_NullSnapshot_ReturnsZero()
        {
            var replacements = new Dictionary<string, string> { { "Jeb", "Kirrim" } };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(null, replacements);

            Assert.Equal(0, result);
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_NullReplacements_ReturnsZero()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, null);

            Assert.Equal(0, result);
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_EmptyReplacements_ReturnsZero()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");
            var replacements = new Dictionary<string, string>();

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(0, result);
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_NoPartsWithCrew_ReturnsZero()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART"); // part with no crew
            var replacements = new Dictionary<string, string> { { "Jeb", "Kirrim" } };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(0, result);
        }

        #endregion

        #region SwapReservedCrewInSnapshot — single crew swap

        [Fact]
        public void SwapReservedCrewInSnapshot_SingleCrewMatch_SwapsAndReturnsOne()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(1, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Single(crew);
            Assert.Equal("Kirrim Kerman", crew[0]);
            Assert.Contains(logLines, l => l.Contains("[CrewReservation]") && l.Contains("Snapshot swap: 'Jebediah Kerman' -> 'Kirrim Kerman'"));
            Assert.Contains(logLines, l => l.Contains("[CrewReservation]") && l.Contains("Snapshot crew swap complete: 1 name(s) replaced"));
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_NoMatch_LeavesCrewUnchanged()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Bob Kerman");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(0, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Single(crew);
            Assert.Equal("Bob Kerman", crew[0]);
        }

        #endregion

        #region SwapReservedCrewInSnapshot — multi-crew scenarios

        [Fact]
        public void SwapReservedCrewInSnapshot_MultipleCrewInSamePart_SwapsOnlyMatches()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", "Bill Kerman");
            part.AddValue("crew", "Bob Kerman");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" },
                { "Bob Kerman", "Agasel Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(2, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Equal(3, crew.Count);
            Assert.Equal("Kirrim Kerman", crew[0]);
            Assert.Equal("Bill Kerman", crew[1]);
            Assert.Equal("Agasel Kerman", crew[2]);
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_MultiplePartsWithCrew_SwapsAcrossParts()
        {
            var snapshot = new ConfigNode("VESSEL");
            var pod = snapshot.AddNode("PART");
            pod.AddValue("crew", "Jebediah Kerman");
            var lab = snapshot.AddNode("PART");
            lab.AddValue("crew", "Valentina Kerman");
            lab.AddValue("crew", "Bill Kerman");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" },
                { "Valentina Kerman", "Agasel Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(2, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Equal(3, crew.Count);
            Assert.Equal("Kirrim Kerman", crew[0]);
            Assert.Equal("Agasel Kerman", crew[1]);
            Assert.Equal("Bill Kerman", crew[2]);
            Assert.Contains(logLines, l => l.Contains("Snapshot swap:") && l.Contains("PART[0]"));
            Assert.Contains(logLines, l => l.Contains("Snapshot swap:") && l.Contains("PART[1]"));
            Assert.Contains(logLines, l => l.Contains("Snapshot crew swap complete: 2 name(s) replaced"));
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_AllCrewSwapped_ReturnsCorrectCount()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", "Valentina Kerman");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" },
                { "Valentina Kerman", "Agasel Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(2, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Equal(2, crew.Count);
            Assert.Equal("Kirrim Kerman", crew[0]);
            Assert.Equal("Agasel Kerman", crew[1]);
        }

        #endregion

        #region SwapReservedCrewInSnapshot — order preservation

        [Fact]
        public void SwapReservedCrewInSnapshot_PreservesCrewOrder()
        {
            // Verify that non-swapped crew retain their original order
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Alice");
            part.AddValue("crew", "Bob");
            part.AddValue("crew", "Charlie");
            part.AddValue("crew", "Dave");
            var replacements = new Dictionary<string, string>
            {
                { "Bob", "Xavier" },
                { "Dave", "Zara" }
            };

            CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Equal(4, crew.Count);
            Assert.Equal("Alice", crew[0]);
            Assert.Equal("Xavier", crew[1]);
            Assert.Equal("Charlie", crew[2]);
            Assert.Equal("Zara", crew[3]);
        }

        #endregion

        #region SwapReservedCrewInSnapshot — empty snapshot edge case

        [Fact]
        public void SwapReservedCrewInSnapshot_EmptySnapshot_ReturnsZero()
        {
            var snapshot = new ConfigNode("VESSEL");
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(0, result);
        }

        [Fact]
        public void SwapReservedCrewInSnapshot_MixedPartsCrewAndNoCrew_OnlySwapsCrewedParts()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART"); // engine — no crew
            var pod = snapshot.AddNode("PART");
            pod.AddValue("crew", "Jebediah Kerman");
            snapshot.AddNode("PART"); // tank — no crew
            var replacements = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Kirrim Kerman" }
            };

            int result = CrewReservationManager.SwapReservedCrewInSnapshot(snapshot, replacements);

            Assert.Equal(1, result);
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            Assert.Single(crew);
            Assert.Equal("Kirrim Kerman", crew[0]);
        }

        #endregion
    }
}
