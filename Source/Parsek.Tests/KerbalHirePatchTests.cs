using System;
using System.Collections.Generic;
using KSP.UI;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalHirePatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorGameStateStoreSuppress;
        private readonly bool priorRecordingStoreSuppress;
        private bool dialogHookCalled;

        public KerbalHirePatchTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorGameStateStoreSuppress = GameStateStore.SuppressLogging;
            priorRecordingStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
            // Every committed row these cells seed sits at UT >= 12345, ahead of this clock.
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 0.0;

            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogHookCalled = true;
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;

            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            GameStateStore.SuppressLogging = priorGameStateStoreSuppress;
            RecordingStore.SuppressLogging = priorRecordingStoreSuppress;
        }

        /// <summary>
        /// Allows hire when not committed. Fails if the patch blocks applicants with no committed future hire.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_AllowsHireWhenNotCommitted_NoDialogLogAndReturnsTrue()
        {
            bool allowed = KerbalHirePatch.ShouldAllowHire("Uncommitted Kerman");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
            Assert.DoesNotContain(logLines, line => line.Contains("[KerbalHirePatch]") && line.Contains("blocking"));
        }

        /// <summary>
        /// Blocks when committed. Fails if the patch does not return false or stops logging the block before showing the dialog.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_BlocksWhenCommitted_LogsAndReturnsFalse()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Future Kerman", ut: 12345.0));

            bool allowed = KerbalHirePatch.ShouldAllowHire("Future Kerman");

            Assert.False(allowed);
            Assert.True(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][KerbalHirePatch]") &&
                line.Contains("blocking hire for name=Future Kerman") &&
                line.Contains("committed future hire ut=12345"));
            Assert.Contains(logLines, line =>
                line.Contains("[INFO][CommittedAction]") &&
                line.Contains("Blocked action: Cannot hire \"Future Kerman\""));
        }

        /// <summary>
        /// Bypasses block when GameStateRecorder.IsReplayingActions is true. Fails if Parsek's own replay cannot hire committed kerbals.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_BypassesWhenReplayingActions_LogsAndReturnsTrue()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Replay Kerman", ut: 23456.0));
            GameStateRecorder.IsReplayingActions = true;

            bool allowed = KerbalHirePatch.ShouldAllowHire("Replay Kerman");

            Assert.True(allowed);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[VERBOSE][KerbalHirePatch]") &&
                line.Contains("bypass") &&
                line.Contains("replay in progress"));
            Assert.DoesNotContain(logLines, line => line.Contains("[CommittedAction]"));
        }

        /// <summary>
        /// Invariant test for §3/§8.3. Fails if the patch uses KerbalsModule.IsManaged instead of the committed-future hire rows.
        /// </summary>
        [Fact]
        public void KerbalHirePatch_PredicateUsesCommittedFutureHires_NotReservations()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Future Hire Kerman", ut: 34567.0));

            var reservedOnlyRec = MakeRecording(
                "Reservation Ship",
                new[] { "Reserved Only Kerman" },
                TerminalState.Recovered,
                2000.0);
            RecordingStore.AddRecordingWithTreeForTesting(reservedOnlyRec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            bool futureHireAllowed = KerbalHirePatch.ShouldAllowHire("Future Hire Kerman");
            bool reservedOnlyAllowed = KerbalHirePatch.ShouldAllowHire("Reserved Only Kerman");

            Assert.False(futureHireAllowed);
            Assert.True(reservedOnlyAllowed);
            Assert.True(kerbals.IsManaged("Reserved Only Kerman"));
            var futureHires = CommittedFutureIndexCache.Current.FutureKeys(CommittedFutureKind.KerbalHire, 0.0);
            Assert.Contains(futureHires, name => name == "Future Hire Kerman");
            Assert.DoesNotContain(futureHires, name => name == "Reserved Only Kerman");
        }

        /// <summary>
        /// Stock Astronaut Complex mutates its applicant/enlisted rows before KerbalRoster.HireApplicant(). Fails if the early stock-UI prefix can no longer find AstronautComplex.HireRecruit().
        /// </summary>
        [Fact]
        public void AstronautComplexHireRecruitPatch_TargetsStockHireRecruitBeforeUiMutation()
        {
            var method = AstronautComplexHireRecruitPatch.ResolveTargetMethodForTesting();

            Assert.NotNull(method);
            Assert.Equal(typeof(AstronautComplex), method.DeclaringType);
            Assert.Equal("HireRecruit", method.Name);

            var parameters = method.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(UIList), parameters[0].ParameterType);
            Assert.Equal(typeof(UIList), parameters[1].ParameterType);
            Assert.Equal(typeof(UIListItem), parameters[2].ParameterType);
        }

        // ---------- auto-hire applicant pick (Difficulty.AutoHireCrews) ----------

        [Fact]
        public void AutoHireApplicantPatch_TargetsStockGetNextApplicant()
        {
            var method = KerbalAutoHireApplicantPatch.ResolveTargetMethodForTesting();

            Assert.NotNull(method);
            Assert.Equal(typeof(KerbalRoster), method.DeclaringType);
            Assert.Equal("GetNextApplicant", method.Name);
            Assert.Empty(method.GetParameters());
            Assert.Equal(typeof(ProtoCrewMember), ((System.Reflection.MethodInfo)method).ReturnType);
        }

        // catches: auto-hire taking the applicant a committed future hires (stock seated
        // him unhired after KerbalHirePatch refused the hire).
        [Fact]
        public void AutoHireApplicant_SkipsTheCommittedFutureHire_PicksTheNextApplicant()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Future Kerman", ut: 12345.0));

            int skipped;
            int pick = KerbalAutoHireApplicantPatch.SelectApplicantIndex(
                new[] { "Future Kerman", "Free Kerman" },
                KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked,
                out skipped);

            Assert.Equal(1, pick);
            Assert.Equal(1, skipped);
            Assert.False(dialogHookCalled);
            Assert.Contains(logLines, line =>
                line.Contains("[KerbalHirePatch]") &&
                line.Contains("auto-hire skips applicant name=Future Kerman"));
        }

        // catches: the skip swallowing stock's fallback - with only the reserved applicant
        // left the pick is null, so stock generates a fresh applicant (its own path).
        [Fact]
        public void AutoHireApplicant_OnlyTheCommittedFutureHireLeft_ReturnsNoneForStockFallback()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Future Kerman", ut: 12345.0));

            int skipped;
            int pick = KerbalAutoHireApplicantPatch.SelectApplicantIndex(
                new[] { "Future Kerman" },
                KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked,
                out skipped);

            Assert.Equal(-1, pick);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void AutoHireApplicant_NoCommittedHire_FirstApplicantUnchanged_EmptyRosterNone()
        {
            int skipped;
            Assert.Equal(0, KerbalAutoHireApplicantPatch.SelectApplicantIndex(
                new[] { "Uncommitted Kerman", "Other Kerman" },
                KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked,
                out skipped));
            Assert.Equal(0, skipped);
            Assert.Equal(-1, KerbalAutoHireApplicantPatch.SelectApplicantIndex(
                new string[0], KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked, out skipped));
            Assert.Equal(0, skipped);
        }

        // Parsek's own replay hires the committed kerbal; the skip must not stand in its way,
        // and a past hire (clock beyond it) no longer blocks.
        [Fact]
        public void AutoHireApplicantBlocked_ReplayBypass_AndPastHireNotBlocked()
        {
            AddCommittedRow(Event(GameStateEventType.CrewHired, "Future Kerman", ut: 12345.0));
            Assert.True(KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked("Future Kerman"));

            GameStateRecorder.IsReplayingActions = true;
            Assert.False(KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked("Future Kerman"));
            GameStateRecorder.IsReplayingActions = false;

            try
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => 20000.0;
                CommittedFutureIndexCache.Invalidate("test clock moved past the hire");
                Assert.False(KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked("Future Kerman"));
                Assert.False(KerbalAutoHireApplicantPatch.IsAutoHireApplicantBlocked(null));
            }
            finally
            {
                CommittedFutureIndexCache.NowUtProviderForTesting = () => 0.0;
                CommittedFutureIndexCache.Invalidate("test clock restored");
            }
        }

        private static Recording MakeRecording(
            string vesselName,
            string[] crew,
            TerminalState terminal,
            double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
                CrewEndStates = new Dictionary<string, KerbalEndState>()
            };

            var endCrewSet = new HashSet<string>(crew);
            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
        }

        private static GameStateEvent Event(
            GameStateEventType type,
            string key,
            double ut = 100.0)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key
            };
        }

        /// <summary>A committed KSC-origin ledger row, converted the way the KSC door converts
        /// the captured event.</summary>
        private static void AddCommittedRow(GameStateEvent ev)
        {
            Ledger.AddAction(GameStateEventConverter.ConvertEvent(ev, null));
        }
    }
}
