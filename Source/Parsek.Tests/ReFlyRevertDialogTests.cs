using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7 + §6.14 + §10.6): guards the
    /// three callback handlers on <see cref="RevertInterceptor"/> and the prefix
    /// gate that decides whether the stock
    /// <see cref="FlightDriver.RevertToLaunch"/> runs.
    ///
    /// <para>
    /// The actual <see cref="PopupDialog"/> rendering cannot be exercised
    /// from xUnit (no live Unity UI canvas), so the tests drive the
    /// callback-wiring side directly via <see cref="RevertInterceptor.RetryHandler"/>,
    /// <see cref="RevertInterceptor.DiscardReFlyHandler"/>,
    /// <see cref="RevertInterceptor.CancelHandler"/>, plus the prefix gate
    /// via <see cref="RevertInterceptor.ShouldBlock"/>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyRevertDialogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public ReFlyRevertDialogTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            TreeDiscardPurge.ResetTestOverrides();
            TreeDiscardPurge.ResetCallCountForTesting();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
        }

        public void Dispose()
        {
            // Defense-in-depth: no test in this file should trigger PurgeTree.
            // An unexpected increment here catches a future regression even if
            // a new test forgets to assert on the counter directly.
            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);

            ReFlyRevertDialog.ResetForTesting();
            RevertInterceptor.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers ---------------------------------------------

        private const string ProvisionalRecId = "rec_provisional_p12";

        private static ReFlySessionMarker MakeMarker(
            string sessionId = "sess_p12_test",
            string treeId = "tree_p12_test",
            string rpId = "rp_p12_test",
            string originId = "rec_origin_p12",
            string activeReFlyRecordingId = ProvisionalRecId)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = activeReFlyRecordingId,
                OriginChildRecordingId = originId,
                RewindPointId = rpId,
                InvokedUT = 42.0,
                InvokedRealTime = "2026-04-18T00:00:00.000Z",
            };
        }

        private static RewindPoint MakeRewindPoint(
            string rpId, string originId,
            bool sessionProvisional = true,
            string creatingSessionId = "sess_p12_test")
        {
            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp_p12",
                UT = 0.0,
                QuicksaveFilename = rpId + ".sfs",
                SessionProvisional = sessionProvisional,
                CreatingSessionId = creatingSessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = originId,
                        Controllable = true,
                    },
                },
            };
        }

        private static ParsekScenario InstallScenario(
            ReFlySessionMarker marker = null,
            List<RewindPoint> rps = null,
            List<RecordingSupersedeRelation> supersedes = null,
            List<LedgerTombstone> tombstones = null,
            MergeJournal journal = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = rps ?? new List<RewindPoint>(),
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = tombstones ?? new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
                ActiveMergeJournal = journal,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static Recording AddProvisional(string sessionId, string recId = ProvisionalRecId)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                VesselName = "p12_provisional",
            };
            RecordingStore.AddProvisional(rec);
            return rec;
        }

        private static void InstallQuicksaveExistsOverride(bool exists = true)
        {
            RevertInterceptor.DiscardReFlyQuicksaveExistsForTesting = _ => exists;
        }

        private sealed class DiscardCaptures
        {
            public RewindPoint LoadGameRp;
            public string LoadGameTempName;
            public int LoadGameCalls;

            public GameScenes? SceneTarget;
            public EditorFacility SceneFacility;
            public int SceneCalls;

            public List<string> ScreenMessages = new List<string>();
        }

        private static DiscardCaptures WireDiscardSeams()
        {
            var caps = new DiscardCaptures();
            RevertInterceptor.DiscardReFlyLoadGameForTesting = (rp, name) =>
            {
                caps.LoadGameRp = rp;
                caps.LoadGameTempName = name;
                caps.LoadGameCalls++;
            };
            RevertInterceptor.DiscardReFlyLoadSceneForTesting = (scene, facility) =>
            {
                caps.SceneTarget = scene;
                caps.SceneFacility = facility;
                caps.SceneCalls++;
            };
            RevertInterceptor.ScreenMessagePostForTesting = msg => caps.ScreenMessages.Add(msg);
            return caps;
        }

        // ---------- Retry -----------------------------------------------

        [Fact]
        public void RetryCallback_GeneratesFreshSession_ClearsMarker_ReinvokesRewind()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });

            RewindPoint capturedRp = null;
            ChildSlot capturedSlot = null;
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) =>
            {
                capturedRp = r;
                capturedSlot = s;
            };

            RevertInterceptor.RetryHandler(marker);

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.NotNull(capturedRp);
            Assert.Equal(rp.RewindPointId, capturedRp.RewindPointId);
            Assert.NotNull(capturedSlot);
            Assert.Equal(0, capturedSlot.SlotIndex);
            Assert.Equal(marker.OriginChildRecordingId, capturedSlot.OriginChildRecordingId);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=retry")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void RetryCallback_UnresolvedRp_AbortsWithoutInvokingRewind()
        {
            var marker = MakeMarker();
            var scenario = InstallScenario(marker: marker);

            bool invoked = false;
            RevertInterceptor.RewindInvokeStartForTesting = (_, __) => invoked = true;

            RevertInterceptor.RetryHandler(marker);

            Assert.False(invoked);
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("RetryHandler: cannot resolve rp="));
        }

        // ---------- Discard Re-fly --------------------------------------

        [Fact]
        public void DiscardReFly_LaunchContext_ClearsSessionArtifacts_LoadsRpQuicksave_TransitionsToKSC()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var rec = AddProvisional(marker.SessionId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Session artifacts cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);

            // Provisional gone.
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r?.RecordingId == rec.RecordingId);

            // Origin RP promoted.
            Assert.False(rp.SessionProvisional);
            Assert.Null(rp.CreatingSessionId);

            // LoadGame + LoadScene seams hit.
            Assert.Equal(1, caps.LoadGameCalls);
            Assert.Same(rp, caps.LoadGameRp);
            Assert.StartsWith("Parsek_Rewind_discard_", caps.LoadGameTempName);
            Assert.Equal(1, caps.SceneCalls);
            Assert.Equal(GameScenes.SPACECENTER, caps.SceneTarget);

            // No error toast.
            Assert.Empty(caps.ScreenMessages);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFly")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("target=Launch")
                && l.Contains("dispatched=true"));
        }

        [Fact]
        public void DiscardReFly_PrelaunchContext_VAB_TransitionsToVAB_StartupCleanSetOnEditor()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Prelaunch, EditorFacility.VAB);

            Assert.Equal(1, caps.SceneCalls);
            Assert.Equal(GameScenes.EDITOR, caps.SceneTarget);
            Assert.Equal(EditorFacility.VAB, caps.SceneFacility);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFly")
                && l.Contains("target=Prelaunch")
                && l.Contains("facility=VAB")
                && l.Contains("dispatched=true"));
        }

        [Fact]
        public void DiscardReFly_PrelaunchContext_SPH_TransitionsToSPH_StartupCleanSetOnEditor()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Prelaunch, EditorFacility.SPH);

            Assert.Equal(1, caps.SceneCalls);
            Assert.Equal(GameScenes.EDITOR, caps.SceneTarget);
            Assert.Equal(EditorFacility.SPH, caps.SceneFacility);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFly")
                && l.Contains("target=Prelaunch")
                && l.Contains("facility=SPH")
                && l.Contains("dispatched=true"));
        }

        [Fact]
        public void DiscardReFly_DoesNotCallTreeDiscardPurge()
        {
            // Install state that would be wiped if TreeDiscardPurge ran.
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var sib = new RewindPoint
            {
                RewindPointId = "rp_sibling",
                BranchPointId = "bp_sibling",
                UT = 1.0,
                SessionProvisional = false,
                QuicksaveFilename = "rp_sibling.sfs",
            };

            var superRel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_preserve",
                OldRecordingId = "rec_old",
                NewRecordingId = "rec_new",
                UT = 0.0,
            };
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_preserve",
                ActionId = "act_preserve",
                RetiringRecordingId = "rec_retiring",
                UT = 0.0,
            };

            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp, sib },
                supersedes: new List<RecordingSupersedeRelation> { superRel },
                tombstones: new List<LedgerTombstone> { tomb });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            // Trip a test seam on TreeDiscardPurge to prove no invocation.
            // Defense-in-depth: even if the counter wiring drifted, an
            // unexpected body execution would still set this flag (the
            // DeleteQuicksave hook only fires from PurgeTree's RP pass).
            bool purgeInvoked = false;
            TreeDiscardPurge.DeleteQuicksaveForTesting = _ => { purgeInvoked = true; return true; };

            // Baseline the counter so this test reads its own delta rather
            // than picking up some prior resetter miss.
            TreeDiscardPurge.ResetCallCountForTesting();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.False(purgeInvoked,
                "DiscardReFly must not call TreeDiscardPurge.PurgeTree");

            // Primary assertion: direct counter check. PurgeTreeCountForTesting
            // increments at the VERY top of PurgeTree before any guards, so
            // this catches an attempted call even if every internal pass
            // early-returned.
            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);

            // Sibling RP + supersede + tombstone preserved.
            Assert.Contains(scenario.RewindPoints, r => r?.RewindPointId == "rp_sibling");
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Single(scenario.LedgerTombstones);
        }

        [Fact]
        public void DiscardReFly_SupersedeRelationsForOtherSplitsInTree_Preserved()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);

            var rel1 = new RecordingSupersedeRelation
            {
                RelationId = "rsr_other1",
                OldRecordingId = "rec_split_a_old",
                NewRecordingId = "rec_split_a_new",
                UT = 0.0,
            };
            var rel2 = new RecordingSupersedeRelation
            {
                RelationId = "rsr_other2",
                OldRecordingId = "rec_split_c_old",
                NewRecordingId = "rec_split_c_new",
                UT = 1.0,
            };

            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp },
                supersedes: new List<RecordingSupersedeRelation> { rel1, rel2 });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            int versionBefore = scenario.SupersedeStateVersion;

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Both relations preserved.
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.Contains(scenario.RecordingSupersedes, r => r?.RelationId == "rsr_other1");
            Assert.Contains(scenario.RecordingSupersedes, r => r?.RelationId == "rsr_other2");

            Assert.True(scenario.SupersedeStateVersion > versionBefore,
                "SupersedeStateVersion must be bumped by DiscardReFly");
        }

        [Fact]
        public void DiscardReFly_TombstonesForOtherSplitsInTree_Preserved()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);

            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_other",
                ActionId = "act_other",
                RetiringRecordingId = "rec_retiring_other",
                UT = 0.0,
            };

            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp },
                tombstones: new List<LedgerTombstone> { tomb });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            int tombVersionBefore = scenario.TombstoneStateVersion;

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.Single(scenario.LedgerTombstones);
            Assert.Equal("tomb_other", scenario.LedgerTombstones[0].TombstoneId);

            Assert.Equal(tombVersionBefore, scenario.TombstoneStateVersion);
        }

        [Fact]
        public void DiscardReFly_OtherRPsInTree_Preserved()
        {
            var marker = MakeMarker();
            var originRp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var other = new RewindPoint
            {
                RewindPointId = "rp_other",
                BranchPointId = "bp_other",
                UT = 5.0,
                SessionProvisional = false,
                QuicksaveFilename = "rp_other.sfs",
            };

            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { originRp, other });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.Equal(2, scenario.RewindPoints.Count);
            Assert.Contains(scenario.RewindPoints, r => r?.RewindPointId == "rp_other");
            Assert.Contains(scenario.RewindPoints, r => r?.RewindPointId == marker.RewindPointId);
        }

        [Fact]
        public void DiscardReFly_OriginRp_SurvivesLoadTimeSweep()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Simulate the post-load sweep in the fresh scenario. Marker is
            // null; the sweep's RP-discard pass would reap any
            // SessionProvisional=true RP not in the spare set.
            LoadTimeSweep.Run();

            Assert.Single(scenario.RewindPoints);
            var survivor = scenario.RewindPoints[0];
            Assert.Equal(marker.RewindPointId, survivor.RewindPointId);
            Assert.False(survivor.SessionProvisional,
                "Origin RP must stay persistent after LoadTimeSweep");
        }

        [Fact]
        public void DiscardReFly_OriginRp_PersistentWithStaleCreatingSessionId_CreatingSessionIdCleared()
        {
            // Defensive invariant test: an RP that is already persistent
            // (SessionProvisional=false) but still carries a stale
            // CreatingSessionId from a crashed prior session must have that
            // id cleared by Discard Re-fly regardless of the promotion branch.
            var marker = MakeMarker();
            var rp = MakeRewindPoint(
                marker.RewindPointId,
                marker.OriginChildRecordingId,
                sessionProvisional: false,
                creatingSessionId: "stale_session_id");
            AddProvisional(marker.SessionId);
            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            // Preconditions.
            Assert.False(rp.SessionProvisional);
            Assert.Equal("stale_session_id", rp.CreatingSessionId);

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Always-clear assertion: even though SessionProvisional was
            // already false (no promotion branch), CreatingSessionId must be
            // null after the handler runs.
            Assert.False(rp.SessionProvisional);
            Assert.Null(rp.CreatingSessionId);
        }

        [Fact]
        public void DiscardReFly_UnfinishedFlightsEntryForThisSplit_StaysVisible()
        {
            // Build a minimal Immutable origin recording whose ParentBranchPointId
            // points at the RP's BranchPoint. IsUnfinishedFlight returns true iff
            // (rec.MergeState == Immutable) && crashed && ParentBranchPointId
            // present && an RP resolving to that BranchPointId is in
            // scenario.RewindPoints.
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            rp.BranchPointId = "bp_origin";

            var origin = new Recording
            {
                RecordingId = marker.OriginChildRecordingId,
                VesselName = "origin_vessel",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp_origin",
            };
            RecordingStore.AddCommittedInternal(origin);
            AddProvisional(marker.SessionId);

            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            Assert.True(EffectiveState.IsUnfinishedFlight(origin),
                "Precondition: origin should be Unfinished before Discard");

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.True(EffectiveState.IsUnfinishedFlight(origin),
                "Origin should still satisfy IsUnfinishedFlight after Discard");
        }

        [Fact]
        public void DiscardReFly_RemovesProvisionalFromCommittedRecordings()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var rec = AddProvisional(marker.SessionId);
            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            Assert.Contains(RecordingStore.CommittedRecordings, r => r?.RecordingId == rec.RecordingId);

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r?.RecordingId == rec.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("Removed provisional rec=" + rec.RecordingId));
        }

        [Fact]
        public void DiscardReFly_BumpsSupersedeStateVersion()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            int before = scenario.SupersedeStateVersion;

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.True(scenario.SupersedeStateVersion > before,
                "SupersedeStateVersion must be bumped");
        }

        [Fact]
        public void DiscardReFly_RpQuicksaveMissing_LogsErrorAndShowsToast()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(false); // simulate missing file
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Marker + journal cleared (session artifacts still drop).
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // Origin RP still promoted (so the next Rewind click works).
            Assert.False(rp.SessionProvisional);

            // LoadGame + LoadScene NOT called.
            Assert.Equal(0, caps.LoadGameCalls);
            Assert.Equal(0, caps.SceneCalls);

            // Error toast shown.
            Assert.Contains(caps.ScreenMessages, m => m.Contains("quicksave missing"));

            // Error log emitted.
            Assert.Contains(logLines, l =>
                l.Contains("[RewindSave]")
                && l.Contains("rewind point quicksave missing"));

            // End line still logs, with dispatched=false.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFly")
                && l.Contains("dispatched=false"));
        }

        [Fact]
        public void DiscardReFly_JournalActive_HandlerRefuses_ShowsToast()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var rec = AddProvisional(marker.SessionId);
            var journal = new MergeJournal
            {
                JournalId = "journal_p12",
                SessionId = marker.SessionId,
                Phase = MergeJournal.Phases.Begin,
            };
            var scenario = InstallScenario(
                marker: marker,
                rps: new List<RewindPoint> { rp },
                journal: journal);
            InstallQuicksaveExistsOverride(true);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            // Nothing touched: marker stays, journal stays, provisional stays,
            // origin RP stays session-provisional, scene not dispatched.
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Same(journal, scenario.ActiveMergeJournal);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r?.RecordingId == rec.RecordingId);
            Assert.True(rp.SessionProvisional);
            Assert.Equal(0, caps.LoadGameCalls);
            Assert.Equal(0, caps.SceneCalls);

            Assert.Contains(caps.ScreenMessages, m => m.Contains("merge in progress"));
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("refusing")
                && l.Contains("merge journal active"));
        }

        [Fact]
        public void DiscardReFly_JournalActive_DialogHidesDiscardButton()
        {
            var marker = MakeMarker();
            var journal = new MergeJournal
            {
                JournalId = "journal_gate",
                SessionId = marker.SessionId,
                Phase = MergeJournal.Phases.Begin,
            };
            InstallScenario(marker: marker, journal: journal);

            bool? includeDiscardSeen = null;
            ReFlyRevertDialog.ButtonsHookForTesting = (_, include) => includeDiscardSeen = include;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Launch,
                () => { }, () => { }, () => { });

            Assert.Equal(false, includeDiscardSeen);

            // Log-assertion: journal-gate log fires.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("merge journal active")
                && l.Contains("Discard Re-fly button hidden"));
        }

        [Fact]
        public void DiscardReFly_NoJournal_DialogShowsDiscardButton()
        {
            var marker = MakeMarker();
            InstallScenario(marker: marker);

            bool? includeDiscardSeen = null;
            ReFlyRevertDialog.ButtonsHookForTesting = (_, include) => includeDiscardSeen = include;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Launch,
                () => { }, () => { }, () => { });

            Assert.Equal(true, includeDiscardSeen);
        }

        [Fact]
        public void DiscardReFly_NullMarker_LogsWarn_NoOp()
        {
            var scenario = InstallScenario(marker: null);
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(null, RevertTarget.Launch);

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("DiscardReFlyHandler: null marker"));
        }

        [Fact]
        public void DiscardReFly_UnresolvableRpId_ClearsMarker_NoSceneDispatch()
        {
            var marker = MakeMarker(rpId: "rp_missing_forever");
            // No matching RP in scenario.
            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(marker: marker);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Equal(0, caps.LoadGameCalls);
            Assert.Equal(0, caps.SceneCalls);
            Assert.Contains(caps.ScreenMessages, m => m.Contains("rewind point missing"));
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("unresolvable rp=rp_missing_forever"));
        }

        [Fact]
        public void DiscardReFly_EmptyTreeId_StillClearsSessionArtifacts_StillDispatchesScene()
        {
            var marker = MakeMarker();
            marker.TreeId = null; // empty tree id no longer branches behavior
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            var scenario = InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            var caps = WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Launch);

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(1, caps.LoadGameCalls);
            Assert.Equal(1, caps.SceneCalls);
            Assert.Equal(GameScenes.SPACECENTER, caps.SceneTarget);
        }

        [Fact]
        public void DiscardReFly_LogsEndReasonDiscardReFly()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            AddProvisional(marker.SessionId);
            InstallScenario(marker: marker, rps: new List<RewindPoint> { rp });
            InstallQuicksaveExistsOverride(true);
            WireDiscardSeams();

            RevertInterceptor.DiscardReFlyHandler(marker, RevertTarget.Prelaunch, EditorFacility.SPH);

            int endLineCount = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("[ReFlySession]")
                    && l.Contains("End reason=discardReFly")
                    && l.Contains("sess=" + marker.SessionId)
                    && l.Contains("target=Prelaunch")
                    && l.Contains("facility=SPH")
                    && l.Contains("dispatched=true"))
                {
                    endLineCount++;
                }
            }
            Assert.Equal(1, endLineCount);

            // Stale reason must NOT appear.
            Assert.DoesNotContain(logLines, l => l.Contains("End reason=fullRevert"));
        }

        // ---------- Cancel ----------------------------------------------

        [Fact]
        public void CancelCallback_LogsAndLeavesStateAlone()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });

            int rpCountBefore = scenario.RewindPoints.Count;
            var markerBefore = scenario.ActiveReFlySessionMarker;

            RevertInterceptor.CancelHandler(marker);

            Assert.Same(markerBefore, scenario.ActiveReFlySessionMarker);
            Assert.Equal(rpCountBefore, scenario.RewindPoints.Count);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog cancelled")
                && l.Contains("sess=" + marker.SessionId));
        }

        // ---------- Interceptor gate ------------------------------------

        [Fact]
        public void Interceptor_NoActiveSession_AllowsStockRevert()
        {
            InstallScenario(marker: null);

            ReFlySessionMarker resolved;
            bool block = RevertInterceptor.ShouldBlock(out resolved);

            Assert.False(block);
            Assert.Null(resolved);
        }

        [Fact]
        public void Interceptor_NoScenario_AllowsStockRevert()
        {
            ReFlySessionMarker resolved;
            bool block = RevertInterceptor.ShouldBlock(out resolved);

            Assert.False(block);
            Assert.Null(resolved);
        }

        [Fact]
        public void Interceptor_ActiveSession_BlocksStockRevert_SpawnsDialog()
        {
            var marker = MakeMarker();
            InstallScenario(marker: marker);

            ReFlySessionMarker dialogMarker = null;
            RevertInterceptor.DialogShowForTesting = m => dialogMarker = m;

            bool result = RevertInterceptor.Prefix();

            Assert.False(result);
            Assert.Same(marker, dialogMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[RevertInterceptor]")
                && l.Contains("blocking stock RevertToLaunch")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void Dialog_Show_FiresHook_AndLogsShownTag()
        {
            var marker = MakeMarker();

            string hookSession = null;
            ReFlyRevertDialog.ShowHookForTesting = s => hookSession = s;

            ReFlyRevertDialog.Show(marker, () => { }, () => { }, () => { });

            Assert.Equal(marker.SessionId, hookSession);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog shown")
                && l.Contains("sess=" + marker.SessionId));
        }

        [Fact]
        public void Dialog_Show_NullMarker_DoesNotFireHook()
        {
            bool hookFired = false;
            ReFlyRevertDialog.ShowHookForTesting = _ => hookFired = true;

            ReFlyRevertDialog.Show(null, () => { }, () => { }, () => { });

            Assert.False(hookFired);
            Assert.Contains(logLines, l =>
                l.Contains("[RewindUI]") && l.Contains("marker is null"));
        }

        // ---------- Body-copy context variants --------------------------

        [Fact]
        public void Show_LaunchTarget_BodySummarizesEachButton()
        {
            var marker = MakeMarker();
            string capturedBody = null;
            ReFlyRevertDialog.BodyHookForTesting = (_, body) => capturedBody = body;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Launch,
                () => { }, () => { }, () => { });

            Assert.NotNull(capturedBody);
            Assert.Contains("You are in a Re-Fly session", capturedBody);
            Assert.Contains(
                "Retry from Rewind Point: restart this Re-Fly from the split moment in FLIGHT",
                capturedBody);
            Assert.Contains(
                "Discard Re-Fly: abandon this attempt, restore the rewind-point save, and return to the Space Center",
                capturedBody);
            Assert.Contains("The STASH entry stays available", capturedBody);
            Assert.Contains("Continue Flying: close this dialog and keep flying", capturedBody);
            Assert.Contains("Space Center", capturedBody);
            Assert.DoesNotContain("VAB or SPH", capturedBody);
            Assert.DoesNotContain("Unfinished Flight entry", capturedBody);
            Assert.DoesNotContain("tree's other Rewind Points", capturedBody);
            Assert.DoesNotContain("supersede", capturedBody);
            Assert.DoesNotContain("tombstone", capturedBody);
        }

        [Fact]
        public void Show_LegacyOverload_DefaultsToLaunchBody()
        {
            var marker = MakeMarker();
            string capturedBody = null;
            ReFlyRevertDialog.BodyHookForTesting = (_, body) => capturedBody = body;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, () => { }, () => { }, () => { });

            Assert.NotNull(capturedBody);
            Assert.Contains("Space Center", capturedBody);
        }

        [Fact]
        public void Show_PrelaunchTarget_BodyContainsVABorSPH()
        {
            var marker = MakeMarker();
            string capturedBody = null;
            ReFlyRevertDialog.BodyHookForTesting = (_, body) => capturedBody = body;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Prelaunch,
                () => { }, () => { }, () => { });

            Assert.NotNull(capturedBody);
            Assert.Contains("VAB or SPH", capturedBody);
            Assert.Contains("FLIGHT", capturedBody);
            Assert.Contains(
                "Discard Re-Fly: abandon this attempt, restore the rewind-point save, and return to the VAB or SPH",
                capturedBody);
            Assert.DoesNotContain("Space Center", capturedBody);
        }

        [Fact]
        public void Show_JournalActive_BodyExplainsDiscardUnavailable()
        {
            var marker = MakeMarker();
            var journal = new MergeJournal
            {
                JournalId = "journal_body",
                SessionId = marker.SessionId,
                Phase = MergeJournal.Phases.Begin,
            };
            InstallScenario(marker: marker, journal: journal);

            string capturedBody = null;
            ReFlyRevertDialog.BodyHookForTesting = (_, body) => capturedBody = body;
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Launch,
                () => { }, () => { }, () => { });

            Assert.NotNull(capturedBody);
            Assert.Contains(
                "Discard Re-Fly: unavailable while a merge is in progress",
                capturedBody);
            Assert.Contains("Finish the merge or reload the save", capturedBody);
            Assert.DoesNotContain("return to the Space Center", capturedBody);
        }

        [Fact]
        public void Show_PrelaunchTarget_LogsTargetOnShownLine()
        {
            var marker = MakeMarker();
            ReFlyRevertDialog.ShowHookForTesting = _ => { };

            ReFlyRevertDialog.Show(marker, RevertTarget.Prelaunch,
                () => { }, () => { }, () => { });

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog shown")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("target=Prelaunch"));
        }

        // ---------- Prelaunch interceptor prefix -------------------------

        [Fact]
        public void Prefix_RevertToPrelaunch_NoActiveSession_ReturnsTrue()
        {
            InstallScenario(marker: null);

            bool result = RevertInterceptor.Prefix(RevertTarget.Prelaunch);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[RevertInterceptor]")
                && l.Contains("allowing stock RevertToPrelaunch"));
        }

        [Fact]
        public void Prefix_RevertToPrelaunch_ActiveSession_ReturnsFalse_ShowsDialog()
        {
            var marker = MakeMarker();
            InstallScenario(marker: marker);

            ReFlySessionMarker dialogMarker = null;
            RevertInterceptor.DialogShowForTesting = m => dialogMarker = m;

            bool result = RevertInterceptor.Prefix(RevertTarget.Prelaunch);

            Assert.False(result);
            Assert.Same(marker, dialogMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[RevertInterceptor]")
                && l.Contains("blocking stock RevertToPrelaunch")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("target=Prelaunch"));
        }

        // ---------- Retry context coverage ------------------------------

        [Fact]
        public void RetryHandler_PrelaunchContext_LogsTarget_StillReinvokesRewind()
        {
            var marker = MakeMarker();
            var rp = MakeRewindPoint(marker.RewindPointId, marker.OriginChildRecordingId);
            var scenario = InstallScenario(marker: marker,
                rps: new List<RewindPoint> { rp });

            RewindPoint capturedRp = null;
            ChildSlot capturedSlot = null;
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) =>
            {
                capturedRp = r;
                capturedSlot = s;
            };

            RevertInterceptor.RetryHandler(marker, RevertTarget.Prelaunch);

            Assert.NotNull(capturedRp);
            Assert.NotNull(capturedSlot);
            Assert.Equal(rp.RewindPointId, capturedRp.RewindPointId);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=retry")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("target=Prelaunch"));
        }

        [Fact]
        public void CancelHandler_PrelaunchContext_LogsTarget()
        {
            var marker = MakeMarker();
            InstallScenario(marker: marker);

            RevertInterceptor.CancelHandler(marker, RevertTarget.Prelaunch);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Revert dialog cancelled")
                && l.Contains("sess=" + marker.SessionId)
                && l.Contains("target=Prelaunch"));
        }
    }
}
