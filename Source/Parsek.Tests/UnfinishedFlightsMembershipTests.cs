using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 5 of Rewind-to-Staging (design §5.11). Guards the
    /// <see cref="UnfinishedFlightsGroup.ComputeMembers"/> virtual-group
    /// classifier. Each test name states the regression it protects against.
    ///
    /// <para>
    /// Input surface for membership is <see cref="EffectiveState.ComputeERS"/>
    /// filtered through <see cref="EffectiveState.IsUnfinishedFlight"/>, so the
    /// tests assemble committed recordings + a scenario carrying
    /// <see cref="RewindPoint"/>s / <see cref="ReFlySessionMarker"/> as needed.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class UnfinishedFlightsMembershipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public UnfinishedFlightsMembershipTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.IsVerboseEnabled;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static Recording Rec(string id, MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string parentBranchPointId = null,
            string childBranchPointId = null,
            string treeId = null,
            bool isDebris = false,
            string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                TreeId = treeId,
                IsDebris = isDebris,
                EvaCrewName = evaCrewName
            };
        }

        private static ParsekScenario InstallScenario(
            List<RewindPoint> rps = null,
            ReFlySessionMarker marker = null,
            List<RecordingSupersedeRelation> supersedes = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static ChildSlot Slot(
            int slotIndex,
            string recordingId,
            bool sealedSlot = false,
            bool stashedSlot = false)
        {
            return new ChildSlot
            {
                SlotIndex = slotIndex,
                OriginChildRecordingId = recordingId,
                Controllable = true,
                Sealed = sealedSlot,
                SealedRealTime = sealedSlot ? "2026-04-28T12:00:00.0000000Z" : null,
                Stashed = stashedSlot,
                StashedRealTime = stashedSlot ? "2026-04-29T09:10:11.0000000Z" : null
            };
        }

        private static RewindPoint Rp(string rpId, string bpId, params string[] slotRecordingIds)
        {
            return RpWithFocus(rpId, bpId, -1, slotRecordingIds);
        }

        private static RewindPoint RpWithFocus(string rpId, string bpId, int focusSlotIndex, params string[] slotRecordingIds)
        {
            var slots = new List<ChildSlot>();
            if (slotRecordingIds != null)
            {
                for (int i = 0; i < slotRecordingIds.Length; i++)
                    slots.Add(Slot(i, slotRecordingIds[i]));
            }

            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 0.0,
                SessionProvisional = false,
                FocusSlotIndex = focusSlotIndex,
                ChildSlots = slots
            };
        }

        // =====================================================================
        // Membership rules
        // =====================================================================

        [Fact]
        public void ImmutableDestroyedUnderRP_IsMember()
        {
            // Regression: a committed Immutable recording whose terminal is
            // Destroyed AND whose parent BranchPoint has a RewindPoint MUST
            // appear in the virtual group (the definition of an unfinished
            // flight — design §3.1 / §5.11).
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            // AddRecordingWithTreeForTesting builds a single-node tree with a
            // fresh GUID id; we only need the recording itself in ERS for the
            // unfinished-flight check, which relies on the scenario's
            // RewindPoints list rather than tree.BranchPoints. rec.TreeId is
            // overwritten by the helper, but that does not affect
            // IsUnfinishedFlight.

            InstallScenario(rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") });

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_A", members[0].RecordingId);
        }

        [Fact]
        public void DestroyedStagesWithDownstreamCrashBranch_AreMembers()
        {
            // 2026-04-29 Kerbal X repro: upper/root and probe/booster both
            // crashed after the staging RP. The probe recording also carried a
            // later child BP for its destruction event; that downstream crash
            // bookkeeping must not suppress the already-conclusive Destroyed
            // terminal outcome.
            var upper = Rec(
                "rec_upper",
                MergeState.Immutable,
                TerminalState.Destroyed,
                childBranchPointId: "bp_stage",
                treeId: "tree_1");
            var probe = Rec(
                "rec_probe",
                MergeState.Immutable,
                TerminalState.Destroyed,
                parentBranchPointId: "bp_stage",
                childBranchPointId: "bp_probe_destroyed",
                treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(upper, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(probe, "tree_1");

            InstallScenario(rps: new List<RewindPoint>
            {
                RpWithFocus("rp_stage", "bp_stage", 0, "rec_upper", "rec_probe")
            });

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Equal(2, members.Count);
            Assert.Contains(members, r => r.RecordingId == "rec_upper");
            Assert.Contains(members, r => r.RecordingId == "rec_probe");
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") &&
                l.Contains("rec=rec_upper") &&
                l.Contains("reason=crashed") &&
                l.Contains("side=active-parent-child"));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") &&
                l.Contains("rec=rec_probe") &&
                l.Contains("reason=crashed"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[UnfinishedFlights]") &&
                l.Contains("rec=rec_probe") &&
                l.Contains("reason=downstreamBp"));
        }

        [Fact]
        public void DestroyedWithRealDownstreamRewindPoint_PrefersDownstreamRoute()
        {
            var rec = Rec(
                "rec_stage",
                MergeState.Immutable,
                TerminalState.Destroyed,
                parentBranchPointId: "bp_old",
                childBranchPointId: "bp_new",
                treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");

            InstallScenario(rps: new List<RewindPoint>
            {
                Rp("rp_old", "bp_old", "rec_stage"),
                Rp("rp_new", "bp_new", "rec_stage", "rec_sibling")
            });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                rec,
                out RewindPoint resolved,
                out int slotListIndex));
            Assert.Equal("rp_new", resolved.RewindPointId);
            Assert.Equal(0, slotListIndex);

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_stage", members[0].RecordingId);
        }

        [Fact]
        public void DestroyedAgainstOlderRewindPointWithRealDownstreamRoute_NotMemberForOlderPoint()
        {
            var rec = Rec(
                "rec_stage",
                MergeState.Immutable,
                TerminalState.Destroyed,
                parentBranchPointId: "bp_old",
                childBranchPointId: "bp_new",
                treeId: "tree_1");
            var olderRp = Rp("rp_old", "bp_old", "rec_stage");
            InstallScenario(rps: new List<RewindPoint>
            {
                olderRp,
                Rp("rp_new", "bp_new", "rec_stage", "rec_sibling")
            });

            Assert.False(UnfinishedFlightClassifier.TryQualify(
                rec,
                olderRp.ChildSlots[0],
                olderRp,
                considerSealed: true,
                out string reason));
            Assert.Equal("downstreamBp", reason);
        }

        [Fact]
        public void CommittedProvisionalDestroyedUnderRP_IsMember()
        {
            // Regression: crash-terminal re-fly attempts and newly stamped
            // RP children use CommittedProvisional so their rewind slot stays
            // open. They must still appear in the virtual Unfinished Flights
            // group.
            var rec = Rec("rec_A", MergeState.CommittedProvisional,
                TerminalState.Destroyed, parentBranchPointId: "bp_1",
                treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");

            InstallScenario(rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") });

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_A", members[0].RecordingId);
        }

        [Fact]
        public void DestroyedUnderRPWithoutSlot_NotMember()
        {
            // Regression for 2026-04-26_2228: a debris recording can share the
            // same BranchPoint/RewindPoint as a real controllable child, but if
            // it was never assigned an RP child slot it cannot be re-flown and
            // must not show as a disabled duplicate Unfinished Flight.
            var rec = Rec("rec_debris", MergeState.Immutable,
                TerminalState.Destroyed, parentBranchPointId: "bp_1",
                treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");

            InstallScenario(rps: new List<RewindPoint>
            {
                Rp("rp_1", "bp_1", "rec_active_parent", "rec_controlled_child")
            });

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=noMatchingRpSlot"));
        }

        [Fact]
        public void ImmutableLandedUnderRP_NotMember()
        {
            // Regression: stable terminal vessel outcomes are NOT unfinished
            // flights even when a parent RP exists.
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario(rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") });

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Empty(members);
        }

        [Fact]
        public void StashedImmutableLandedUnderRP_IsMember()
        {
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_A", stashedSlot: true)
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_A", members[0].RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=stashedStableLeaf"));
        }

        [Fact]
        public void OrbitingNonFocusUnderPostFeatureRP_IsMember()
        {
            var rec = Rec("rec_probe", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                RpWithFocus("rp_1", "bp_1", 0, "rec_focus", "rec_probe")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_probe", members[0].RecordingId);
        }

        [Fact]
        public void OrbitingFocusSlotUnderPostFeatureRP_NotMember()
        {
            var rec = Rec("rec_focus", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                RpWithFocus("rp_1", "bp_1", 0, "rec_focus", "rec_probe")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=stableTerminalFocusSlot"));
        }

        [Fact]
        public void StashedOrbitingFocusSlotUnderPostFeatureRP_IsMember()
        {
            var rec = Rec("rec_focus", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus", stashedSlot: true),
                    Slot(1, "rec_probe")
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_focus", members[0].RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=stashedStableLeaf"));
        }

        [Fact]
        public void OrbitingLegacyNoFocusSignal_NotMember()
        {
            var rec = Rec("rec_probe", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                Rp("rp_1", "bp_1", "rec_probe")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=noFocusSignalOrbiting"));
        }

        [Fact]
        public void StashedOrbitingLegacyNoFocusSignal_IsMember()
        {
            var rec = Rec("rec_probe", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = -1,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_probe", stashedSlot: true)
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_probe", members[0].RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=stashedStableLeaf"));
        }

        [Fact]
        public void OrbitingDebrisUnderPostFeatureRP_NotMember()
        {
            var rec = Rec("rec_debris", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1", isDebris: true);
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                RpWithFocus("rp_1", "bp_1", 0, "rec_focus", "rec_debris")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=notControllable"));
        }

        [Fact]
        public void SubOrbitalNonFocusUnderPostFeatureRP_IsMember()
        {
            var rec = Rec("rec_upper", MergeState.Immutable, TerminalState.SubOrbital,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                RpWithFocus("rp_1", "bp_1", 0, "rec_focus", "rec_upper")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_upper", members[0].RecordingId);
        }

        [Fact]
        public void StrandedEvaLegacyNoFocusSignal_IsMember()
        {
            var rec = Rec("rec_eva", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1", treeId: "tree_1", evaCrewName: "Jebediah Kerman");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            InstallScenario(rps: new List<RewindPoint>
            {
                Rp("rp_1", "bp_1", "rec_eva")
            });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Single(members);
            Assert.Equal("rec_eva", members[0].RecordingId);
        }

        [Fact]
        public void StashedBoardedEva_NotMember()
        {
            var rec = Rec("rec_eva", MergeState.Immutable, TerminalState.Boarded,
                parentBranchPointId: "bp_1", treeId: "tree_1", evaCrewName: "Jebediah Kerman");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = -1,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_eva", stashedSlot: true)
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=stableTerminal"));
        }

        [Fact]
        public void SealedSlot_NotMember()
        {
            var rec = Rec("rec_probe", MergeState.CommittedProvisional, TerminalState.Orbiting,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_focus"),
                    Slot(1, "rec_probe", sealedSlot: true)
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=slotSealed"));
        }

        [Fact]
        public void StashedSealedSlot_NotMember()
        {
            var rec = Rec("rec_probe", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "rec_probe", sealedSlot: true, stashedSlot: true)
                }
            };
            InstallScenario(rps: new List<RewindPoint> { rp });

            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Empty(members);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("reason=slotSealed"));
        }

        [Fact]
        public void ImmutableDestroyedNotUnderRP_NotMember()
        {
            // Regression: a crashed Immutable recording whose parent BP has no
            // RewindPoint MUST NOT appear (§5.11 requires an RP for the
            // rewind button to have a target — absent RP, the row is just a
            // historical recording).
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_unknown");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario(); // no RPs

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Empty(members);
        }

        [Fact]
        public void NotCommittedDestroyedUnderRP_NotMember()
        {
            // Regression: NotCommitted recordings are excluded from ERS
            // entirely, so even if they have a parent RP and crash terminal
            // they must not appear in the virtual group.
            var rec = Rec("rec_A", MergeState.NotCommitted, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            InstallScenario(rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") });

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Empty(members);
        }

        [Fact]
        public void SupersededDestroyedUnderRP_NotMember()
        {
            // Regression: walking supersedes filters a recording out of ERS
            // before the unfinished-flight classifier even sees it. Design
            // §3.1 makes ERS the canonical visible set.
            var recOld = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            var recNew = Rec("rec_B", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1");
            RecordingStore.AddRecordingWithTreeForTesting(recOld);
            RecordingStore.AddRecordingWithTreeForTesting(recNew);

            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_A_B",
                    OldRecordingId = "rec_A",
                    NewRecordingId = "rec_B",
                    UT = 0.0
                }
            };
            InstallScenario(
                rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") },
                supersedes: supersedes);

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Empty(members); // rec_A superseded out, rec_B is Landed
        }

        [Fact]
        public void ActiveReFlySessionSuppressed_NotInList()
        {
            // Regression: while a re-fly session is active, its suppressed
            // subtree drops out of ERS (design §3.3). An unfinished-flight
            // recording inside that subtree therefore must not surface in the
            // virtual group until the session ends.
            var recOrigin = Rec("rec_origin", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(recOrigin);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_active",
                OriginChildRecordingId = "rec_origin"
            };
            InstallScenario(
                rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_origin") },
                marker: marker);

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Empty(members);
        }

        [Fact]
        public void ComputeMembers_EmptyList_NoException_LogsRecompute()
        {
            // Regression: callers may hit ComputeMembers before any recording
            // has ever been committed. The method must return an empty list
            // without throwing, and still emit the recompute log for audit.
            InstallScenario();

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.NotNull(members);
            Assert.Empty(members);

            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("recompute") && l.Contains("0 entries"));
        }

        [Fact]
        public void ComputeMembers_LogsCount()
        {
            // Regression: the recompute Verbose line must reflect the member
            // count — design §10.5 uses it for post-hoc audits of virtual
            // group population.
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");

            InstallScenario(rps: new List<RewindPoint> { Rp("rp_1", "bp_1", "rec_A") });

            // Rate-limiter has state per ThreadStatic dict; clear so the
            // first recompute call below emits (the setup above did not
            // invoke ComputeMembers yet, so the key is absent, but we flush
            // here to be defensive against future setup paths that do).
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);

            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("recompute") && l.Contains("1 entries"));
        }
    }
}
