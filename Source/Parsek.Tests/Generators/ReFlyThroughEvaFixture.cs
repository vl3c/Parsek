using System;
using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The shapes behind the owner ruling of 2026-09-27 (Re-Fly follows the separated
    /// vessel through its own EVA and re-board): a staging split with a Rewind Point
    /// whose upper-stage slot later puts a crew member out on EVA.
    ///
    /// <code>
    ///   root --split(JointBreak, RP)--+-- booster  B  (slot 0)
    ///                                 +-- upper    U  (slot 1)
    ///   U --EVA-- U1 (vessel, same pid) + K (Jebediah on EVA)
    ///   K + U1 --Board-- U2 (vessel)          [Reboard]
    /// </code>
    ///
    /// Variants change only what happens after the EVA; see
    /// <see cref="ReFlyThroughEvaVariant"/>. <see cref="MaterializeTree"/> returns the
    /// in-memory <see cref="RecordingTree"/> exactly as
    /// <see cref="ScenarioWriter.MaterializeTree"/> builds it (branch links stamped from
    /// the branch points) plus two trajectory points per recording, so the splitter's
    /// sampled-bounds tests have content to read.
    /// </summary>
    public enum ReFlyThroughEvaVariant
    {
        /// <summary>Jeb EVAs from U and re-boards U; U2 ends <see cref="ReFlyThroughEvaFixture.UpperTerminal"/>.</summary>
        Reboard,
        /// <summary>Jeb EVAs from U and boards the BOOSTER (a foreign vessel); U1 ends the upper stage.</summary>
        KerbalBoardsForeignVessel,
        /// <summary>Jeb dies on the EVA (terminal Destroyed); U1 ends the upper stage.</summary>
        KerbalDiesOnEva,
        /// <summary>Jeb is left standing on EVA (terminal Landed); U1 ends the upper stage.</summary>
        KerbalStaysOnEva,
        /// <summary>Reboard, and Jeb placed a ground part while out.</summary>
        ReboardWithPlacedPart,
        /// <summary>Bill EVAs from the BOOSTER and boards the upper stage's U1 (a Board by a kerbal
        /// from another vessel); U2 ends the upper stage. U itself has its own EVA as in Reboard.</summary>
        ForeignKerbalBoardsUpper,
        /// <summary>Two EVAs: Jeb re-boards, Bill (second EVA) boards the booster.</summary>
        TwoEvasOneLeavesForForeign,
    }

    /// <summary>
    /// What the player did with the re-flown upper stage during a Re-Fly session
    /// (<see cref="ReFlyThroughEvaFixture.AddReFlySession"/>).
    /// </summary>
    public enum ReFlySessionShape
    {
        /// <summary>The fork flies to its end with no branch point.</summary>
        NoBranch,
        /// <summary>Jeb EVAs from the fork and re-boards it: fork -EVA- U1 + Jeb -Board- U2.</summary>
        EvaReboard,
        /// <summary>Jeb EVAs from the fork and stays out; U1 carries the vessel's end.</summary>
        EvaKerbalStaysOut,
        /// <summary>Jeb EVAs from the fork and boards a different vessel.</summary>
        EvaKerbalBoardsForeignVessel,
        /// <summary>A stock Switch-To away and back: fork -VesselSwitchContinuation- segment.</summary>
        SwitchContinuation,
        /// <summary>The fork docks with something (closed Docked by the recorder).</summary>
        Dock,
        /// <summary>The fork undocks: the parent ends at the Undock split, terminal untouched.</summary>
        Undock,
        /// <summary>The fork stages (tree-branching split: the fork keeps its id).</summary>
        Stage,
    }

    public static class ReFlyThroughEvaFixture
    {
        public const string RewindPointId = "rp_thru_eva";
        public const string SplitBranchPointId = "bp_thru_eva_split";
        public const string EvaBranchPointId = "bp_thru_eva_eva";
        public const string BoardBranchPointId = "bp_thru_eva_board";
        public const string ForeignBoardBranchPointId = "bp_thru_eva_foreign_board";
        public const string PlaceBranchPointId = "bp_thru_eva_place";
        public const string BoosterEvaBranchPointId = "bp_thru_eva_booster_eva";
        public const string SecondEvaBranchPointId = "bp_thru_eva_eva2";

        public const string RootId = "te-root";
        public const string BoosterId = "te-booster";
        public const string BoosterAfterBoardId = "te-booster-b2";
        public const string BoosterAfterEvaId = "te-booster-b1";
        public const string UpperId = "te-upper";
        public const string UpperAfterEvaId = "te-upper-u1";
        public const string UpperAfterSecondEvaId = "te-upper-u1b";
        public const string UpperAfterBoardId = "te-upper-u2";
        public const string KerbalId = "te-jeb-eva";
        public const string SecondKerbalId = "te-bill-eva";
        public const string ForeignKerbalId = "te-bill-from-booster";
        public const string PlacedPartId = "te-placed-part";

        public const string EvaCrewName = "Jebediah Kerman";
        public const string SecondEvaCrewName = "Bill Kerman";

        public const uint UpperPid = 4100001u;
        public const uint BoosterPid = 4100002u;
        public const uint KerbalPid = 4100003u;
        public const uint SecondKerbalPid = 4100004u;
        public const uint ForeignKerbalPid = 4100005u;
        public const uint PlacedPartPid = 4100006u;

        public const int BoosterSlotIndex = 0;
        public const int UpperSlotIndex = 1;

        public const double LaunchUT = 10.0;
        public const double SplitUT = 100.0;
        public const double EvaUT = 200.0;
        public const double PlaceUT = 220.0;
        public const double BoardUT = 260.0;
        public const double SecondEvaUT = 280.0;
        public const double EndUT = 400.0;

        /// <summary>The upper stage's real ending in every variant: a crash.</summary>
        public const TerminalState UpperTerminal = TerminalState.Destroyed;

        /// <summary>
        /// The Rewind Point at the staging split: booster slot 0, upper-stage slot 1,
        /// focus on the upper stage (the vessel the player was flying).
        /// </summary>
        public static RewindPoint BuildRewindPoint(bool upperIsTreeBranchingParent = false)
        {
            return new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = SplitBranchPointId,
                UT = SplitUT,
                FocusSlotIndex = UpperSlotIndex,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = BoosterSlotIndex,
                        OriginChildRecordingId = BoosterId,
                        Controllable = true,
                    },
                    new ChildSlot
                    {
                        SlotIndex = UpperSlotIndex,
                        OriginChildRecordingId = upperIsTreeBranchingParent ? RootId : UpperId,
                        Controllable = true,
                    },
                },
                PidSlotMap = new Dictionary<uint, int>
                {
                    [BoosterPid] = BoosterSlotIndex,
                    [UpperPid] = UpperSlotIndex,
                },
            };
        }

        /// <summary>
        /// The committed tree for <paramref name="variant"/>. With
        /// <paramref name="upperIsTreeBranchingParent"/> the upper stage is the ROOT
        /// recording that keeps flying through the separation (the controlled-decoupled
        /// booster hangs off the split as a child, the root does not end there), so it
        /// spans the rewind point - the shape <c>RecordingTreeSplitter</c> must cut.
        /// </summary>
        public static RecordingTree MaterializeTree(
            ReFlyThroughEvaVariant variant, bool upperIsTreeBranchingParent = false)
        {
            var builders = new List<RecordingBuilder>();
            var spans = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var bps = new List<BranchPoint>();
            string upperOriginId = upperIsTreeBranchingParent ? RootId : UpperId;

            if (upperIsTreeBranchingParent)
            {
                // The root IS the upper stage: pid UpperPid, flying from launch through
                // the split to the EVA.
                builders.Add(Vessel(RootId, "TE Upper", UpperPid));
                spans[RootId] = new[] { LaunchUT, EvaUT };
                bps.Add(ScenarioWriter.SeparationBranch(
                    SplitBranchPointId, RootId, new[] { BoosterId }, SplitUT));
            }
            else
            {
                builders.Add(Vessel(RootId, "TE Stack", 4100000u));
                spans[RootId] = new[] { LaunchUT, SplitUT };
                builders.Add(Vessel(UpperId, "TE Upper", UpperPid));
                spans[UpperId] = new[] { SplitUT, EvaUT };
                bps.Add(ScenarioWriter.SeparationBranch(
                    SplitBranchPointId, RootId, new[] { BoosterId, UpperId }, SplitUT));
            }

            // The upper stage's own EVA: U -> U1 (vessel) + K (Jeb).
            builders.Add(Vessel(UpperAfterEvaId, "TE Upper", UpperPid));
            builders.Add(Kerbal(KerbalId, EvaCrewName, KerbalPid));
            bps.Add(ScenarioWriter.EvaBranch(
                EvaBranchPointId, upperOriginId, UpperAfterEvaId, KerbalId, EvaUT));

            bool boosterEndsAtForeignBoard =
                variant == ReFlyThroughEvaVariant.KerbalBoardsForeignVessel
                || variant == ReFlyThroughEvaVariant.TwoEvasOneLeavesForForeign;
            builders.Add(Vessel(BoosterId, "TE Booster", BoosterPid,
                boosterEndsAtForeignBoard
                    || variant == ReFlyThroughEvaVariant.ForeignKerbalBoardsUpper
                    ? (TerminalState?)null
                    : TerminalState.Landed));

            switch (variant)
            {
                case ReFlyThroughEvaVariant.Reboard:
                case ReFlyThroughEvaVariant.ReboardWithPlacedPart:
                {
                    spans[UpperAfterEvaId] = new[] { EvaUT, BoardUT };
                    spans[KerbalId] = new[] { EvaUT, BoardUT };
                    builders.Add(Vessel(UpperAfterBoardId, "TE Upper", UpperPid, UpperTerminal));
                    spans[UpperAfterBoardId] = new[] { BoardUT, EndUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        BoardBranchPointId, KerbalId, UpperAfterEvaId, UpperAfterBoardId, BoardUT));
                    spans[BoosterId] = new[] { SplitUT, EndUT };
                    if (variant == ReFlyThroughEvaVariant.ReboardWithPlacedPart)
                    {
                        builders.Add(new RecordingBuilder("TE Ground Part")
                            .WithRecordingId(PlacedPartId)
                            .WithVesselPersistentId(PlacedPartPid)
                            .WithTerminalState((int)TerminalState.Landed)
                            .AsPlacedGroundPart(100000u, "DeployedSeismicSensor", PlaceUT));
                        spans[PlacedPartId] = new[] { PlaceUT, EndUT };
                        bps.Add(ScenarioWriter.GroundPartPlacedBranch(
                            PlaceBranchPointId, KerbalId, PlacedPartId, PlaceUT));
                    }
                    break;
                }
                case ReFlyThroughEvaVariant.KerbalBoardsForeignVessel:
                {
                    SetTerminal(builders, UpperAfterEvaId, UpperTerminal);
                    spans[UpperAfterEvaId] = new[] { EvaUT, EndUT };
                    spans[KerbalId] = new[] { EvaUT, BoardUT };
                    spans[BoosterId] = new[] { SplitUT, BoardUT };
                    builders.Add(Vessel(BoosterAfterBoardId, "TE Booster", BoosterPid, TerminalState.Landed));
                    spans[BoosterAfterBoardId] = new[] { BoardUT, EndUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        ForeignBoardBranchPointId, KerbalId, BoosterId, BoosterAfterBoardId, BoardUT));
                    break;
                }
                case ReFlyThroughEvaVariant.KerbalDiesOnEva:
                {
                    SetTerminal(builders, UpperAfterEvaId, UpperTerminal);
                    SetTerminal(builders, KerbalId, TerminalState.Destroyed);
                    spans[UpperAfterEvaId] = new[] { EvaUT, EndUT };
                    spans[KerbalId] = new[] { EvaUT, BoardUT };
                    spans[BoosterId] = new[] { SplitUT, EndUT };
                    break;
                }
                case ReFlyThroughEvaVariant.KerbalStaysOnEva:
                {
                    SetTerminal(builders, UpperAfterEvaId, UpperTerminal);
                    SetTerminal(builders, KerbalId, TerminalState.Landed);
                    spans[UpperAfterEvaId] = new[] { EvaUT, EndUT };
                    spans[KerbalId] = new[] { EvaUT, EndUT };
                    spans[BoosterId] = new[] { SplitUT, EndUT };
                    break;
                }
                case ReFlyThroughEvaVariant.ForeignKerbalBoardsUpper:
                {
                    // Jeb re-boards (as Reboard), but first Bill came over from the booster
                    // and boarded U1: that Board has a foreign kerbal parent.
                    spans[BoosterId] = new[] { SplitUT, 230.0 };
                    builders.Add(Vessel(BoosterAfterEvaId, "TE Booster", BoosterPid, TerminalState.Landed));
                    spans[BoosterAfterEvaId] = new[] { 230.0, EndUT };
                    builders.Add(Kerbal(ForeignKerbalId, SecondEvaCrewName, ForeignKerbalPid));
                    spans[ForeignKerbalId] = new[] { 230.0, 250.0 };
                    bps.Add(ScenarioWriter.EvaBranch(
                        BoosterEvaBranchPointId, BoosterId, BoosterAfterEvaId, ForeignKerbalId, 230.0));
                    builders.Add(Vessel(UpperAfterSecondEvaId, "TE Upper", UpperPid));
                    spans[UpperAfterEvaId] = new[] { EvaUT, 250.0 };
                    spans[UpperAfterSecondEvaId] = new[] { 250.0, BoardUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        ForeignBoardBranchPointId, ForeignKerbalId, UpperAfterEvaId,
                        UpperAfterSecondEvaId, 250.0));
                    spans[KerbalId] = new[] { EvaUT, BoardUT };
                    builders.Add(Vessel(UpperAfterBoardId, "TE Upper", UpperPid, UpperTerminal));
                    spans[UpperAfterBoardId] = new[] { BoardUT, EndUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        BoardBranchPointId, KerbalId, UpperAfterSecondEvaId, UpperAfterBoardId, BoardUT));
                    break;
                }
                case ReFlyThroughEvaVariant.TwoEvasOneLeavesForForeign:
                {
                    // Jeb re-boards U (-> U2); Bill then EVAs from U2 and boards the booster.
                    spans[UpperAfterEvaId] = new[] { EvaUT, BoardUT };
                    spans[KerbalId] = new[] { EvaUT, BoardUT };
                    builders.Add(Vessel(UpperAfterBoardId, "TE Upper", UpperPid));
                    spans[UpperAfterBoardId] = new[] { BoardUT, SecondEvaUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        BoardBranchPointId, KerbalId, UpperAfterEvaId, UpperAfterBoardId, BoardUT));
                    builders.Add(Vessel(UpperAfterSecondEvaId, "TE Upper", UpperPid, UpperTerminal));
                    spans[UpperAfterSecondEvaId] = new[] { SecondEvaUT, EndUT };
                    builders.Add(Kerbal(SecondKerbalId, SecondEvaCrewName, SecondKerbalPid));
                    spans[SecondKerbalId] = new[] { SecondEvaUT, 300.0 };
                    bps.Add(ScenarioWriter.EvaBranch(
                        SecondEvaBranchPointId, UpperAfterBoardId, UpperAfterSecondEvaId,
                        SecondKerbalId, SecondEvaUT));
                    spans[BoosterId] = new[] { SplitUT, 300.0 };
                    builders.Add(Vessel(BoosterAfterBoardId, "TE Booster", BoosterPid, TerminalState.Landed));
                    spans[BoosterAfterBoardId] = new[] { 300.0, EndUT };
                    bps.Add(ScenarioWriter.BoardBranch(
                        ForeignBoardBranchPointId, SecondKerbalId, BoosterId, BoosterAfterBoardId, 300.0));
                    break;
                }
            }

            RecordingTree tree = ScenarioWriter.MaterializeTree(builders, null, bps);
            tree.Id = "tree_thru_eva";
            foreach (var rec in tree.Recordings.Values)
            {
                rec.TreeId = tree.Id;
                double[] span;
                if (spans.TryGetValue(rec.RecordingId, out span))
                {
                    rec.Points.Add(new TrajectoryPoint { ut = span[0], bodyName = "Kerbin" });
                    rec.Points.Add(new TrajectoryPoint { ut = span[1], bodyName = "Kerbin" });
                    rec.ExplicitStartUT = span[0];
                    rec.ExplicitEndUT = span[1];
                }
            }

            if (upperIsTreeBranchingParent)
            {
                // Tree-branching parent: the root keeps recording through the split, so
                // its single child-link slot names the EVA, not the separation.
                tree.Recordings[RootId].ChildBranchPointId = EvaBranchPointId;
            }
            return tree;
        }

        // ------------------------------------------------------------------
        // A Re-Fly SESSION of the upper-stage slot (the in-place fork and what the
        // player did with it after the rewind), for the merge-side cells.
        // ------------------------------------------------------------------

        public const string SessionId = "sess_thru_eva";
        public const string ForkId = "te-fork";
        public const string ForkAfterEvaId = "te-fork-u1";
        public const string ForkAfterBoardId = "te-fork-u2";
        public const string ForkAfterSwitchId = "te-fork-sw";
        public const string ForkAfterUndockId = "te-fork-ud";
        public const string ForkUndockedPartnerId = "te-fork-ud-partner";
        public const string ForkStagedChildId = "te-fork-stage-child";
        public const string SessionKerbalId = "te-fork-jeb";
        public const string ForeignVesselId = "te-foreign";
        public const string ForeignVesselAfterBoardId = "te-foreign-b";
        public const string SessionEvaBranchPointId = "bp_fork_eva";
        public const string SessionBoardBranchPointId = "bp_fork_board";
        public const string SessionForeignBoardBranchPointId = "bp_fork_foreign_board";
        public const string SessionSwitchBranchPointId = "bp_fork_switch";
        public const string SessionDockBranchPointId = "bp_fork_dock";
        public const string SessionUndockBranchPointId = "bp_fork_undock";
        public const string SessionStageBranchPointId = "bp_fork_stage";

        public const uint ForeignVesselPid = 4100007u;
        public const uint UndockedPartnerPid = 4100008u;
        public const uint StagedChildPid = 4100009u;

        public const double SessionEvaUT = 150.0;
        public const double SessionBoardUT = 170.0;
        public const double SessionEndUT = 250.0;

        /// <summary>
        /// Adds the recordings a Re-Fly session of the upper-stage slot leaves in the
        /// committed tree at merge time (after <c>CommitPendingTree</c> unioned the
        /// active tree in): the NotCommitted in-place fork, plus whatever the player
        /// did after the rewind. Production shape of the fork
        /// (<c>RewindInvoker.BuildProvisionalRecording</c>): session-tagged, playback
        /// suppressed, the origin's parent branch point, no chain identity. Recordings
        /// the recorder created DURING the session (vessel continuations, the EVA
        /// kerbal, a switch segment) carry the default <see cref="MergeState.Immutable"/>
        /// and no session id, exactly as <c>CreateSplitBranch</c> builds them.
        /// Returns the fork. The caller registers every tree recording in the store.
        /// </summary>
        public static Recording AddReFlySession(
            RecordingTree tree, ReFlySessionShape shape, TerminalState vesselEnd)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            var fork = SessionVessel(ForkId, tree.Id);
            fork.MergeState = MergeState.NotCommitted;
            fork.CreatingSessionId = SessionId;
            fork.PlaybackEnabled = false;
            fork.ParentBranchPointId = SplitBranchPointId;
            fork.SupersedeTargetId = UpperAfterBoardId;

            switch (shape)
            {
                case ReFlySessionShape.NoBranch:
                    AddSpan(fork, SplitUT, SessionEndUT);
                    fork.TerminalStateValue = vesselEnd;
                    break;

                case ReFlySessionShape.EvaReboard:
                case ReFlySessionShape.EvaKerbalStaysOut:
                case ReFlySessionShape.EvaKerbalBoardsForeignVessel:
                {
                    AddSpan(fork, SplitUT, SessionEvaUT);
                    fork.ChildBranchPointId = SessionEvaBranchPointId;
                    var afterEva = SessionVessel(ForkAfterEvaId, tree.Id);
                    afterEva.ParentBranchPointId = SessionEvaBranchPointId;
                    var kerbal = new Recording
                    {
                        RecordingId = SessionKerbalId,
                        VesselName = EvaCrewName,
                        TreeId = tree.Id,
                        VesselPersistentId = KerbalPid,
                        EvaCrewName = EvaCrewName,
                        ParentBranchPointId = SessionEvaBranchPointId,
                    };
                    tree.BranchPoints.Add(ScenarioWriter.EvaBranch(
                        SessionEvaBranchPointId, ForkId, ForkAfterEvaId, SessionKerbalId,
                        SessionEvaUT));
                    Add(tree, afterEva);
                    Add(tree, kerbal);

                    if (shape == ReFlySessionShape.EvaReboard)
                    {
                        AddSpan(afterEva, SessionEvaUT, SessionBoardUT);
                        AddSpan(kerbal, SessionEvaUT, SessionBoardUT);
                        afterEva.ChildBranchPointId = SessionBoardBranchPointId;
                        kerbal.ChildBranchPointId = SessionBoardBranchPointId;
                        kerbal.TerminalStateValue = TerminalState.Boarded;
                        var afterBoard = SessionVessel(ForkAfterBoardId, tree.Id);
                        afterBoard.ParentBranchPointId = SessionBoardBranchPointId;
                        AddSpan(afterBoard, SessionBoardUT, SessionEndUT);
                        afterBoard.TerminalStateValue = vesselEnd;
                        tree.BranchPoints.Add(ScenarioWriter.BoardBranch(
                            SessionBoardBranchPointId, SessionKerbalId, ForkAfterEvaId,
                            ForkAfterBoardId, SessionBoardUT));
                        Add(tree, afterBoard);
                    }
                    else if (shape == ReFlySessionShape.EvaKerbalStaysOut)
                    {
                        AddSpan(afterEva, SessionEvaUT, SessionEndUT);
                        afterEva.TerminalStateValue = vesselEnd;
                        AddSpan(kerbal, SessionEvaUT, SessionEndUT);
                        kerbal.TerminalStateValue = TerminalState.Landed;
                    }
                    else
                    {
                        // Jeb leaves the re-flown vessel for a different vessel.
                        AddSpan(afterEva, SessionEvaUT, SessionEndUT);
                        afterEva.TerminalStateValue = vesselEnd;
                        AddSpan(kerbal, SessionEvaUT, SessionBoardUT);
                        kerbal.ChildBranchPointId = SessionForeignBoardBranchPointId;
                        kerbal.TerminalStateValue = TerminalState.Boarded;
                        var foreign = new Recording
                        {
                            RecordingId = ForeignVesselId,
                            VesselName = "TE Foreign Lander",
                            TreeId = tree.Id,
                            VesselPersistentId = ForeignVesselPid,
                            ChildBranchPointId = SessionForeignBoardBranchPointId,
                        };
                        AddSpan(foreign, SplitUT, SessionBoardUT);
                        var foreignAfter = new Recording
                        {
                            RecordingId = ForeignVesselAfterBoardId,
                            VesselName = "TE Foreign Lander",
                            TreeId = tree.Id,
                            VesselPersistentId = ForeignVesselPid,
                            ParentBranchPointId = SessionForeignBoardBranchPointId,
                            TerminalStateValue = TerminalState.Landed,
                        };
                        AddSpan(foreignAfter, SessionBoardUT, SessionEndUT);
                        tree.BranchPoints.Add(ScenarioWriter.BoardBranch(
                            SessionForeignBoardBranchPointId, SessionKerbalId, ForeignVesselId,
                            ForeignVesselAfterBoardId, SessionBoardUT));
                        Add(tree, foreign);
                        Add(tree, foreignAfter);
                    }
                    break;
                }

                case ReFlySessionShape.SwitchContinuation:
                {
                    AddSpan(fork, SplitUT, SessionEvaUT);
                    fork.ChildBranchPointId = SessionSwitchBranchPointId;
                    var segment = SessionVessel(ForkAfterSwitchId, tree.Id);
                    segment.ParentBranchPointId = SessionSwitchBranchPointId;
                    AddSpan(segment, SessionEvaUT, SessionEndUT);
                    segment.TerminalStateValue = vesselEnd;
                    tree.BranchPoints.Add(new BranchPoint
                    {
                        Id = SessionSwitchBranchPointId,
                        UT = SessionEvaUT,
                        Type = BranchPointType.VesselSwitchContinuation,
                        ParentRecordingIds = new List<string> { ForkId },
                        ChildRecordingIds = new List<string> { ForkAfterSwitchId },
                    });
                    Add(tree, segment);
                    break;
                }

                case ReFlySessionShape.Dock:
                {
                    // Dock: the recorder closes the fork Docked (docs/dev/
                    // dock-undock-recording-structure.md section 3.1).
                    AddSpan(fork, SplitUT, SessionEvaUT);
                    fork.ChildBranchPointId = SessionDockBranchPointId;
                    fork.TerminalStateValue = TerminalState.Docked;
                    tree.BranchPoints.Add(new BranchPoint
                    {
                        Id = SessionDockBranchPointId,
                        UT = SessionEvaUT,
                        Type = BranchPointType.Dock,
                        MergeCause = "DOCK",
                        ParentRecordingIds = new List<string> { ForkId },
                        ChildRecordingIds = new List<string>(),
                    });
                    break;
                }

                case ReFlySessionShape.Undock:
                {
                    // Undock: the parent ends at the split with its terminal untouched
                    // (section 4 of the same doc); the survivor keeps the pid.
                    AddSpan(fork, SplitUT, SessionEvaUT);
                    fork.ChildBranchPointId = SessionUndockBranchPointId;
                    var survivor = SessionVessel(ForkAfterUndockId, tree.Id);
                    survivor.ParentBranchPointId = SessionUndockBranchPointId;
                    AddSpan(survivor, SessionEvaUT, SessionEndUT);
                    survivor.TerminalStateValue = vesselEnd;
                    var partner = new Recording
                    {
                        RecordingId = ForkUndockedPartnerId,
                        VesselName = "TE Undocked Partner",
                        TreeId = tree.Id,
                        VesselPersistentId = UndockedPartnerPid,
                        ParentBranchPointId = SessionUndockBranchPointId,
                        TerminalStateValue = TerminalState.Orbiting,
                    };
                    AddSpan(partner, SessionEvaUT, SessionEndUT);
                    tree.BranchPoints.Add(ScenarioWriter.SeparationBranch(
                        SessionUndockBranchPointId, ForkId,
                        new[] { ForkAfterUndockId, ForkUndockedPartnerId },
                        SessionEvaUT, BranchPointType.Undock));
                    Add(tree, survivor);
                    Add(tree, partner);
                    break;
                }

                case ReFlySessionShape.Stage:
                {
                    // Staging / decoupling: a tree-branching split - the fork keeps
                    // flying under its own id and reaches its own terminal; the
                    // separated stage hangs off the split as a child.
                    AddSpan(fork, SplitUT, SessionEndUT);
                    fork.TerminalStateValue = vesselEnd;
                    var staged = new Recording
                    {
                        RecordingId = ForkStagedChildId,
                        VesselName = "TE Spent Stage",
                        TreeId = tree.Id,
                        VesselPersistentId = StagedChildPid,
                        ParentBranchPointId = SessionStageBranchPointId,
                        TerminalStateValue = TerminalState.Destroyed,
                    };
                    AddSpan(staged, SessionEvaUT, SessionEndUT);
                    tree.BranchPoints.Add(ScenarioWriter.SeparationBranch(
                        SessionStageBranchPointId, ForkId, new[] { ForkStagedChildId },
                        SessionEvaUT));
                    Add(tree, staged);
                    break;
                }
            }

            Add(tree, fork);
            return fork;
        }

        /// <summary>The marker <c>RewindInvoker.AtomicMarkerWrite</c> writes for an
        /// in-place Re-Fly of the upper-stage slot of the
        /// <see cref="ReFlyThroughEvaVariant.Reboard"/> tree, whose walked tip is
        /// <see cref="UpperAfterBoardId"/>.</summary>
        public static ReFlySessionMarker BuildSessionMarker()
        {
            return new ReFlySessionMarker
            {
                SessionId = SessionId,
                TreeId = "tree_thru_eva",
                ActiveReFlyRecordingId = ForkId,
                OriginChildRecordingId = UpperId,
                SupersedeTargetId = UpperAfterBoardId,
                RewindPointId = RewindPointId,
                InvokedUT = SplitUT,
                RewindPointUT = SplitUT,
                InPlaceContinuation = true,
                PreSessionBranchPointIds = new List<string>
                {
                    SplitBranchPointId, EvaBranchPointId, BoardBranchPointId,
                },
            };
        }

        private static Recording SessionVessel(string id, string treeId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "TE Upper",
                TreeId = treeId,
                VesselPersistentId = UpperPid,
            };
        }

        private static void AddSpan(Recording rec, double startUT, double endUT)
        {
            rec.Points.Add(new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" });
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
        }

        private static void Add(RecordingTree tree, Recording rec)
        {
            tree.AddOrReplaceRecording(rec);
        }

        private static RecordingBuilder Vessel(
            string id, string name, uint pid, TerminalState? terminal = null)
        {
            var b = new RecordingBuilder(name)
                .WithRecordingId(id)
                .WithVesselPersistentId(pid);
            if (terminal.HasValue) b.WithTerminalState((int)terminal.Value);
            return b;
        }

        private static RecordingBuilder Kerbal(string id, string crewName, uint pid)
        {
            return new RecordingBuilder(crewName)
                .WithRecordingId(id)
                .WithVesselPersistentId(pid)
                .WithEvaCrewName(crewName);
        }

        private static void SetTerminal(
            List<RecordingBuilder> builders, string id, TerminalState terminal)
        {
            for (int i = 0; i < builders.Count; i++)
            {
                if (string.Equals(builders[i].GetRecordingId(), id, StringComparison.Ordinal))
                {
                    builders[i].WithTerminalState((int)terminal);
                    return;
                }
            }
            throw new ArgumentException("no builder " + id);
        }
    }
}
