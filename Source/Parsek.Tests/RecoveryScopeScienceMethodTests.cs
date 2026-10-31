using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECOVERY-RECONCILE-ERROR-ON-TWO-SCIENCE-CREDITS (found by harness lane RW-22, run
    /// 2026-10-31_0321). Recovering the pad lab under Patents Licensing pays two science
    /// credits in one window: stock's reverse-engineered <c>recovery@KerbinOrbited</c> (10)
    /// and the lab's stored science (12.5). Stock's CurrencyConverter takes 5 percent inside
    /// each AddScience (decompiled KSP 1.12.5: <c>ResearchAndDevelopment.OnCurrenciesModified</c>
    /// fires ScienceChanged under the query's reason with the take already applied), so the
    /// ScienceChanged(VesselRecovery) capture held 9.5 while OnScienceReceived reported 10:
    /// the amount match failed, the subject was filed with <c>reason=''</c> and read as a
    /// transmission. The post-walk reconcile then logged two ERROR dumps, a ScienceEarning
    /// "no matching ScienceChanged event keyed 'ScienceTransmission'" and the lab row
    /// "expected=12.5, observed=22.5" against the coalesced recovery event it shares with the
    /// subject. The callback now takes the recovery's reason from the open recovery scope of
    /// its own vessel; the reconcile already sums every science row keyed to one event.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryScopeScienceMethodTests : IDisposable
    {
        private const uint LabPid = 2200110055u;
        private const string LabName = "LABX Orbital Lab";
        private const string Subject = "recovery@KerbinOrbited";
        private const double RecoveryUt = 372.15;

        private readonly List<string> logLines = new List<string>();

        public RecoveryScopeScienceMethodTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            ResetState();
        }

        public void Dispose()
        {
            ResetState();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetState()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            RecoveryDispatchScope.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static void NoRecorder()
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => false;
        }

        private static void SeedScience(float initial)
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = initial
            });
        }

        /// <summary>
        /// Stock's recovery submission of <see cref="Subject"/> under a 5 percent converter:
        /// the ScienceChanged capture is the NET credit (9.5), the callback the GROSS 10.
        /// </summary>
        private static void CaptureRecoverySubjectUnderConverter(GameStateRecorder recorder)
        {
            recorder.SetLatestScienceChangeCaptureForTesting(
                new GameStateRecorder.RecentScienceChangeCapture
                {
                    Ut = RecoveryUt,
                    ReasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                    Delta = 9.5f,
                    RecordingId = "",
                    Valid = true
                });
            recorder.CaptureScienceSubject(
                10f, Subject, 10f, 12f, 1f, RecoveryUt, LabName, null, LabPid);
        }

        private static GameAction SubjectRow()
        {
            var rows = Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == Subject).ToList();
            Assert.Single(rows);
            return rows[0];
        }

        private static void OpenLabRecoveryScope(uint pid = LabPid)
        {
            RecoveryDispatchScope.Open(
                RecoveredVesselIdentity.FromRawName(LabName, null, pid), pid, "in-flight-recover");
        }

        private static GameStateEvent ScienceEvent(double before, double after, string reason)
        {
            return new GameStateEvent
            {
                ut = RecoveryUt,
                eventType = GameStateEventType.ScienceChanged,
                key = reason,
                valueBefore = before,
                valueAfter = after,
                recordingId = ""
            };
        }

        private static GameAction KscSubject(ScienceMethod method)
        {
            return new GameAction
            {
                UT = RecoveryUt,
                Type = GameActionType.ScienceEarning,
                SubjectId = Subject,
                Method = method,
                Effective = true,
                ScienceAwarded = 10f,
                EffectiveScience = 10f,
                StartUT = (float)RecoveryUt,
                EndUT = (float)RecoveryUt
            };
        }

        private static GameAction LabCredit()
        {
            return new GameAction
            {
                UT = RecoveryUt,
                Type = GameActionType.ScienceLabEarning,
                ScienceAwarded = 12.5f,
                Method = ScienceMethod.Recovered
            };
        }

        private static GameAction ConverterTake(float take)
        {
            return new GameAction
            {
                UT = RecoveryUt,
                Type = GameActionType.StrategyScienceDebit,
                Cost = take,
                ConversionSource = StrategyConversionSource.Converter
            };
        }

        /// <summary>
        /// RW-22's store and strategy rows: one coalesced ScienceChanged(VesselRecovery) of
        /// +21.375 (9.5 + 11.875 net) and one take row per credit (0.5 and 0.625).
        /// </summary>
        private static List<GameStateEvent> Rw22Events(double after = 121.375)
        {
            return new List<GameStateEvent>
            {
                ScienceEvent(100.0, after, LedgerOrchestrator.VesselRecoveryReasonKey)
            };
        }

        private static List<GameAction> Rw22Rows(GameAction subject)
        {
            return new List<GameAction>
            {
                subject,
                ConverterTake(0.5f),
                LabCredit(),
                ConverterTake(0.625f)
            };
        }

        private void ReconcileWithTracking(List<GameStateEvent> events, List<GameAction> actions)
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            logLines.Clear();
            PostWalkActionReconciler.ReconcilePostWalk(events, actions, null);
        }

        private string SummaryLine()
        {
            string found = null;
            foreach (var l in logLines)
            {
                if (l.Contains("Post-walk reconcile: actions="))
                    found = l;
            }
            Assert.NotNull(found);
            return found;
        }

        private void AssertNoWarnOrError()
        {
            Assert.DoesNotContain(logLines, l => l.Contains("][WARN]") || l.Contains("][ERROR]"));
            Assert.DoesNotContain(logLines, l => l.Contains("Earnings reconciliation (post-walk,"));
            Assert.DoesNotContain(logLines, l => l.Contains("Science reconcile dump (post-walk)"));
        }

        // ================================================================
        // The pure decision
        // ================================================================

        [Theory]
        [InlineData(true, LabPid, LabPid, "VesselRecovery")]
        [InlineData(false, 0u, LabPid, null)]       // no recovery in progress
        [InlineData(false, LabPid, LabPid, null)]   // closed scope, stale pid
        [InlineData(true, LabPid, 0u, null)]        // callback with no source vessel
        [InlineData(true, 0u, LabPid, null)]        // scope with no vessel pid
        [InlineData(true, LabPid, 1234u, null)]     // another vessel's science
        public void ResolveRecoveryScopeScienceReasonKey_OnlyTheScopeOwnVessel(
            bool scopeOpen, uint scopePid, uint sourcePid, string expected)
        {
            Assert.Equal(expected,
                GameStateRecorder.ResolveRecoveryScopeScienceReasonKey(scopeOpen, scopePid, sourcePid));
        }

        // ================================================================
        // The capture: the method follows the recovery scope
        // ================================================================

        [Fact]
        public void NetCaptureUnderConverter_InsideTheVesselsRecoveryScope_FilesRecovered()
        {
            NoRecorder();
            OpenLabRecoveryScope();

            CaptureRecoverySubjectUnderConverter(new GameStateRecorder());

            var row = SubjectRow();
            Assert.Equal(ScienceMethod.Recovered, row.Method);
            Assert.Equal(LedgerOrchestrator.VesselRecoveryReasonKey,
                LedgerOrchestrator.GetScienceChangedReasonKey(row));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("subject 'recovery@KerbinOrbited' amount=10 matched no ScienceChanged capture")
                && l.Contains("key='VesselRecovery' delta=9.5")
                && l.Contains("reason 'VesselRecovery' taken from the open recovery scope pid=2200110055"));
            Assert.Contains(logLines, l => l.Contains("Science subject captured: recovery@KerbinOrbited")
                && l.Contains("reason='VesselRecovery'"));
            Assert.Contains(logLines, l => l.Contains("KSC science recorded: subject='recovery@KerbinOrbited'")
                && l.Contains("method=Recovered"));
        }

        [Fact]
        public void NetCaptureUnderConverter_NoRecoveryScope_StaysTransmitted()
        {
            NoRecorder();

            CaptureRecoverySubjectUnderConverter(new GameStateRecorder());

            Assert.Equal(ScienceMethod.Transmitted, SubjectRow().Method);
            Assert.Contains(logLines, l => l.Contains("Science subject captured: recovery@KerbinOrbited")
                && l.Contains("reason=''"));
            Assert.DoesNotContain(logLines, l => l.Contains("taken from the open recovery scope"));
        }

        [Fact]
        public void NetCaptureUnderConverter_ScopeOfAnotherVessel_StaysTransmitted()
        {
            NoRecorder();
            OpenLabRecoveryScope(pid: 77u);

            CaptureRecoverySubjectUnderConverter(new GameStateRecorder());

            Assert.Equal(ScienceMethod.Transmitted, SubjectRow().Method);
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("recovery scope of another vessel (scopePid=77 sourcePid=2200110055)")
                && l.Contains("reason left empty"));
        }

        [Fact]
        public void MatchedCapture_InsideTheScope_KeepsTheCapturesReason()
        {
            // No strategy: the capture matches and its reason wins; the scope adds nothing.
            NoRecorder();
            OpenLabRecoveryScope();
            var recorder = new GameStateRecorder();
            recorder.SetLatestScienceChangeCaptureForTesting(
                new GameStateRecorder.RecentScienceChangeCapture
                {
                    Ut = RecoveryUt,
                    ReasonKey = LedgerOrchestrator.VesselRecoveryReasonKey,
                    Delta = 10f,
                    RecordingId = "",
                    Valid = true
                });
            recorder.CaptureScienceSubject(10f, Subject, 10f, 12f, 1f, RecoveryUt, LabName, null, LabPid);

            Assert.Equal(ScienceMethod.Recovered, SubjectRow().Method);
            Assert.DoesNotContain(logLines, l => l.Contains("taken from the open recovery scope"));
        }

        [Fact]
        public void DeployedScienceInsideAScope_IsNotStampedARecovery()
        {
            NoRecorder();
            OpenLabRecoveryScope();
            new GameStateRecorder().CaptureScienceSubject(
                4f, "deployedSeismicSensor@MunSrfLandedMidlands", 4f, 40f, 1f, RecoveryUt, LabName, null, LabPid);

            Assert.DoesNotContain(logLines, l => l.Contains("taken from the open recovery scope"));
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceEarning);
            Assert.Equal(ScienceMethod.Transmitted, row.Method);
        }

        [Fact]
        public void TheScopeStamp_MovesNoLedgerValue()
        {
            // The same award captured with and without the scope: the method differs and
            // nothing the walk credits does - row amount, subject cap, multiplier, the
            // subject's credited total and the science pool.
            var unscoped = CaptureAndWalk(scoped: false);
            ResetState();
            var scoped = CaptureAndWalk(scoped: true);

            Assert.Equal(ScienceMethod.Transmitted, unscoped.Method);
            Assert.Equal(ScienceMethod.Recovered, scoped.Method);
            Assert.Equal(unscoped.Awarded, scoped.Awarded);
            Assert.Equal(unscoped.Cap, scoped.Cap);
            Assert.Equal(unscoped.Multiplier, scoped.Multiplier);
            Assert.Equal(unscoped.Effective, scoped.Effective);
            Assert.Equal(unscoped.Credited, scoped.Credited, 6);
            Assert.Equal(unscoped.Pool, scoped.Pool, 6);
            Assert.Equal(10.0, scoped.Credited, 6);
        }

        private struct WalkedRow
        {
            public ScienceMethod Method;
            public float Awarded;
            public float Cap;
            public float Multiplier;
            public float Effective;
            public double Credited;
            public double Pool;
        }

        private WalkedRow CaptureAndWalk(bool scoped)
        {
            SeedScience(100f);
            NoRecorder();
            if (scoped) OpenLabRecoveryScope();
            CaptureRecoverySubjectUnderConverter(new GameStateRecorder());
            RecoveryDispatchScope.ResetForTesting();

            LedgerOrchestrator.RecalculateAndPatch();

            var row = SubjectRow();
            return new WalkedRow
            {
                Method = row.Method,
                Awarded = row.ScienceAwarded,
                Cap = row.SubjectMaxValue,
                Multiplier = row.ScienceGainMultiplier,
                Effective = row.EffectiveScience,
                Credited = LedgerOrchestrator.Science.GetSubjectCredited(Subject),
                Pool = LedgerOrchestrator.Science.GetRunningScience()
            };
        }

        // ================================================================
        // The post-walk reconcile on RW-22's shape
        // ================================================================

        [Fact]
        public void Rw22Shape_SubjectFiledAsTransmitted_ReproducesBothErrors()
        {
            // The pre-fix filing: the subject row keys 'ScienceTransmission', so the one
            // VesselRecovery event is compared against the lab row alone.
            ReconcileWithTracking(Rw22Events(), Rw22Rows(KscSubject(ScienceMethod.Transmitted)));

            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceEarning")
                && l.Contains("id=recovery@KerbinOrbited expected=10.0 but no matching ScienceChanged event keyed 'ScienceTransmission'"));
            Assert.Contains(logLines, l => l.Contains("Earnings reconciliation (post-walk, sci): ScienceLabEarning")
                && l.Contains("expected=12.5, observed=22.5")
                && l.Contains("keyed 'VesselRecovery'"));
            Assert.Equal(2, logLines.Count(l => l.Contains("[ERROR]")
                && l.Contains("Science reconcile dump (post-walk)")));
        }

        [Fact]
        public void Rw22Shape_SubjectFiledAsRecovered_SumsBothCreditsAndMatches()
        {
            ReconcileWithTracking(Rw22Events(), Rw22Rows(KscSubject(ScienceMethod.Recovered)));

            AssertNoWarnOrError();
            string summary = SummaryLine();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", summary);
            Assert.Contains("labCovered=1", summary);
            Assert.Contains(logLines, l => l.Contains("Post-walk match: ScienceEarning sci")
                && l.Contains("expected=22.5, observed=22.5")
                && l.Contains("keyed 'VesselRecovery'"));
            Assert.Contains(logLines, l => l.Contains("grossed the observed NET credit 21.375 keyed 'VesselRecovery'")
                && l.Contains("-> 22.5"));
        }

        [Fact]
        public void Rw22Shape_GenuineShortfall_StillLogsTheError()
        {
            // The recovery event is missing the subject's 9.5: only the lab's net 11.875 was
            // paid. Summing both rows must not hide that.
            ReconcileWithTracking(Rw22Events(after: 111.875), Rw22Rows(KscSubject(ScienceMethod.Recovered)));

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Earnings reconciliation (post-walk, sci): ScienceEarning")
                && l.Contains("expected=22.5, observed=13.0")
                && l.Contains("post-walk delta mismatch"));
            Assert.Contains(logLines, l => l.Contains("[ERROR]")
                && l.Contains("Science reconcile dump (post-walk)")
                && l.Contains("reason='VesselRecovery'"));
            Assert.Contains("mismatches(funds/rep/sci)=0/0/1", SummaryLine());
        }

        [Fact]
        public void CapturedInScope_ThenReconciled_AgainstRw22Store_NoError()
        {
            // The two halves joined: the subject row comes from the real capture path inside
            // the lab's recovery scope; the lab credit, the two takes and the coalesced event
            // are RW-22's.
            NoRecorder();
            OpenLabRecoveryScope();
            CaptureRecoverySubjectUnderConverter(new GameStateRecorder());
            RecoveryDispatchScope.ResetForTesting();
            var subject = SubjectRow();

            ReconcileWithTracking(Rw22Events(), Rw22Rows(subject));

            AssertNoWarnOrError();
            Assert.Contains("mismatches(funds/rep/sci)=0/0/0", SummaryLine());
        }
    }
}
