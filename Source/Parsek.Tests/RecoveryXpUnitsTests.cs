using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests for the units <c>LedgerOrchestrator.TryRecordRecoveryKerbalExperience</c>
    /// calls: the ambiguous-refusal line, the event conversion and the recorded line. The
    /// whole-method behavior is pinned by
    /// <see cref="TryRecordRecoveryKerbalExperienceCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryXpUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecoveryXpUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameStateEvent XpEvent(string kerbal, double ut)
        {
            var entries = new List<KerbalCareerLogEntry> { new KerbalCareerLogEntry(1, "Recover", "Kerbin") };
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ExperienceGained,
                key = kerbal,
                detail = $"flight=1;entries={KerbalCareerLogEntry.FormatSet(entries)};trait=Pilot"
            };
        }

        private static GameStateEvent NoRowEvent(string kerbal, double ut) =>
            new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.CrewStatusChanged,
                key = kerbal,
                detail = "from=Assigned;to=Available",
            };

        // ---- ConvertRecoveryExperienceEvents ----------------------------------------

        [Fact]
        public void Convert_KeepsOrder_StampsTheOwner_AndCountsEventsWithNoRow()
        {
            var events = new List<GameStateEvent>
            {
                NoRowEvent("Valentina Kerman", 10.0),
                XpEvent("Jebediah Kerman", 10.0),
                NoRowEvent("Bill Kerman", 10.0),
                XpEvent("Bob Kerman", 10.0),
            };

            var actions = LedgerOrchestrator.ConvertRecoveryExperienceEvents(events, "rec-owner", out int noAction);

            Assert.Equal(2, noAction);
            Assert.Equal(new[] { "Jebediah Kerman", "Bob Kerman" }, actions.Select(a => a.KerbalName).ToArray());
            Assert.All(actions, a => Assert.Equal(GameActionType.KerbalExperience, a.Type));
            Assert.All(actions, a => Assert.Equal("rec-owner", a.RecordingId));
        }

        [Fact]
        public void Convert_Empty_ReturnsEmptyAndZero()
        {
            var actions = LedgerOrchestrator.ConvertRecoveryExperienceEvents(
                new List<GameStateEvent>(), "rec-owner", out int noAction);

            Assert.Empty(actions);
            Assert.Equal(0, noAction);
        }

        [Fact]
        public void Convert_NothingConverts_CountsEveryEvent()
        {
            var actions = LedgerOrchestrator.ConvertRecoveryExperienceEvents(
                new List<GameStateEvent> { NoRowEvent("A", 1.0), NoRowEvent("B", 2.0) }, "rec-owner",
                out int noAction);

            Assert.Empty(actions);
            Assert.Equal(2, noAction);
        }

        // ---- LogRecoveryXpRecorded --------------------------------------------------

        [Fact]
        public void Recorded_NamesEveryRow_NullNameAsEmpty_AndDerivesTheDedupCount()
        {
            var actions = new List<GameAction>
            {
                new GameAction { Type = GameActionType.KerbalExperience, KerbalName = "Jebediah Kerman" },
                new GameAction { Type = GameActionType.KerbalExperience, KerbalName = null },
                new GameAction { Type = GameActionType.KerbalExperience, KerbalName = "Bob Kerman" },
            };

            LedgerOrchestrator.LogRecoveryXpRecorded(
                RecoveredVesselIdentity.FromRawName("Jumping Flea"), "347.5", "rec-flea", actions, 5, 1);

            var line = Assert.Single(logLines);
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP recorded: vessel='Jumping Flea' " +
                "ut=347.5 recordingId=rec-flea rows=3 deduped=2 noAction=1 " +
                "kerbals='Jebediah Kerman,,Bob Kerman'",
                line);
        }

        // ---- LogRecoveryXpAmbiguousRefusal -------------------------------------------

        [Fact]
        public void AmbiguousRefusal_NamesTheSurvivorsTierAndCorroboration()
        {
            var survivors = new List<Recording>
            {
                new Recording { RecordingId = "rec-launch-1" },
                new Recording { RecordingId = "rec-launch-2" },
            };
            var picked = new RecoveryPickResult
            {
                RecordingId = "rec-launch-2",
                Tier = RecoveryPickTier.MostRecentEnded,
                Survivors = survivors,
                NameMatchCount = 3,
                GuidDropped = 1,
            };
            var ambiguity = new RecoveryPickAmbiguityResult
            {
                IsAmbiguous = true,
                Reason = RecoveryPickAmbiguity.AmbiguousReason,
                SurvivorCount = 2,
                Tier = RecoveryPickTier.GlobalLatest,
                Corroboration = SurvivorLaunchCorroboration.UnknownLaunchGuid,
            };

            LedgerOrchestrator.LogRecoveryXpAmbiguousRefusal(
                RecoveredVesselIdentity.FromRawName("Hopper"), "1000.0", "2", ambiguity, picked, "rec-launch-2");

            var line = Assert.Single(logLines);
            // The tier printed is the ambiguity result's, not the pick's.
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] Recovery kerbal XP refused: vessel='Hopper' " +
                "ut=1000.0 kerbals=2 reason=ambiguous-recovery-recording survivors=2 nameMatches=3 " +
                "guidDropped=1 tier=global-latest corroboration=unknown-launch-guid " +
                "wouldHavePicked=rec-launch-2 survivorIds=rec-launch-1,rec-launch-2",
                line);
        }
    }
}
