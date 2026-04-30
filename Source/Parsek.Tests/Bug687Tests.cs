using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #687 (extends #608/#609): the dead-crew spawn-block
    /// guard no longer counts Missing crew as dead, even when those kerbals
    /// have no live <see cref="CrewReservationManager.CrewReplacements"/>
    /// entry. Missing is transient (KSP's natural respawn timer recovers it),
    /// and the spawn pipeline rescues Missing snapshot crew to Available
    /// before <c>ProtoVessel.Load</c>; only StrictlyDead permanently blocks.
    ///
    /// Live-roster behaviour (the actual unreserved-Missing rescue and the
    /// extended carve-out Verbose log) is exercised by an in-game test in
    /// <see cref="Parsek.InGameTests.RuntimeTests"/>; xUnit covers only the
    /// pure decision pieces here because they are reachable without the
    /// live KSP roster API.
    /// </summary>
    [Collection("Sequential")]
    public class Bug687Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug687Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldBlockSpawnForDeadCrew — post-#687 contract: a deadSet
        //  that excludes BOTH reserved+Missing AND unreserved+Missing
        //  (BuildDeadCrewSet only adds StrictlyDead) must not block
        //  when the snapshot is full of plain Missing names.
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldBlock_AllUnreservedMissing_DeadSetEmpty_DoesNotBlock()
        {
            // The #687 scenario: snapshot has [Jeb, Bill, Bob], all Missing
            // in the live roster, no reservations. After the bug fix
            // BuildDeadCrewSet drops the "Missing && !reserved" clause, so
            // the dead set is empty. ShouldBlockSpawnForDeadCrew must allow.
            var crew = new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" };
            var deadSet = new HashSet<string>(); // carve-out emptied it

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, deadSet);

            Assert.False(result);
        }

        [Fact]
        public void ShouldBlock_MixedMissingAndDead_OnlyDeadInSet_DoesNotBlock()
        {
            // Jeb + Bill: unreserved+Missing (carved out, NOT in dead set)
            // Bob: StrictlyDead (in dead set)
            // Two of three are spawnable → allow.
            var crew = new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" };
            var deadSet = new HashSet<string> { "Bob Kerman" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, deadSet);

            Assert.False(result);
        }

        [Fact]
        public void ShouldBlock_AllStrictlyDead_StillBlocks()
        {
            // Regression guard: Dead is permanent. If every snapshot crew
            // is StrictlyDead, the spawn must still be abandoned —
            // RescueReservedMissingCrewInSnapshot only handles Missing.
            var crew = new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman" };
            var deadSet = new HashSet<string>(crew);

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, deadSet);

            Assert.True(result);
        }

        // ────────────────────────────────────────────────────────────
        //  Classification format remains stable — the WARN body still
        //  reports MissingNotReserved, which downstream log audits read.
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void FormatSummary_PostFixUnreservedMissing_StillReportsMissingNotReserved()
        {
            // Even though MissingNotReserved no longer blocks spawn, the
            // category survives in the per-crew classification so the
            // Verbose carve-out log can break the snapshot down by reason.
            var classified = new List<KeyValuePair<string, VesselSpawner.SpawnableClassification>>
            {
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Jebediah Kerman", VesselSpawner.SpawnableClassification.MissingNotReserved),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bill Kerman", VesselSpawner.SpawnableClassification.MissingNotReserved),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bob Kerman", VesselSpawner.SpawnableClassification.MissingNotReserved),
            };

            string s = VesselSpawner.FormatSpawnableClassificationSummary(classified);

            Assert.Contains("total=3", s);
            Assert.Contains("strictlyDead=0", s);
            Assert.Contains("missingNotReserved=3", s);
            Assert.Contains("reservedMissing=0", s);
            Assert.Contains("alive=0", s);
            Assert.Contains("Jebediah Kerman: MissingNotReserved", s);
        }
    }
}
