using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #615 — rescue-completion guard at <see cref="KerbalsModule.ApplyToRoster"/>
    /// step 1.
    ///
    /// <para>
    /// Re-Fly in-place continuation strips the original capsule recording but
    /// leaves its <see cref="GameActionType.KerbalAssignment"/> actions in
    /// ELS, so the next ledger walk rebuilds the original kerbals'
    /// reservations. The post-spawn rescue path
    /// (<see cref="VesselSpawner.RescueReservedMissingCrewInSnapshot"/> +
    /// <see cref="CrewReservationManager.UnreserveCrewInSnapshot"/>) places
    /// the originals back into the spawned vessel and removes the historical
    /// stand-ins from the roster, but the chain-entry name persists in
    /// <see cref="KerbalsModule.KerbalSlot.Chain"/>. Without this guard the
    /// next recalc walk's <see cref="KerbalsModule.ApplyToRoster"/> step 1
    /// recreates the same stand-in immediately after the rescue removed it,
    /// then the subsequent ghost spawn's <c>Crew dedup</c> WARN fires for the
    /// original who is now on two vessels in different ApplyToRoster snapshots.
    /// </para>
    ///
    /// <para>
    /// P1 review: the guard's predicate combines TWO signals — the
    /// rescue-specific marker set by
    /// <see cref="CrewReservationManager.MarkRescuePlaced"/> from the
    /// <see cref="VesselSpawner.RescueReservedMissingCrewInSnapshot"/> path
    /// (#608/#609) AND a live-vessel check. Either one alone is wrong:
    ///   - "on a live vessel" alone fires for fresh reservations where the
    ///     kerbal is on the active player vessel and was never rescued, so
    ///     <see cref="SwapReservedCrewInFlight"/> would have nothing to swap.
    ///   - "rescue-placed marker" alone fires after the rescued vessel was
    ///     destroyed — the kerbal is no longer on any vessel, the stand-in
    ///     is genuinely needed, the recreate must run.
    /// Combined, the guard fires only when the rescue path actually placed
    /// this kerbal AND they are still on a loaded non-ghost vessel.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RescueCompletionGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private KerbalsModule priorKerbalsModule;

        public RescueCompletionGuardTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorKerbalsModule = LedgerOrchestrator.Kerbals;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
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
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorKerbalsModule);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // ---------- Fixture helpers ----------------------------------------

        /// <summary>
        /// Build a module that has just walked one capsule recording carrying
        /// three reserved kerbals (Jeb / Bill / Bob), with a pre-loaded chain
        /// where each slot already holds a depth-0 stand-in. This mirrors the
        /// state at recalc time: reservation dict freshly rebuilt, slot.Chain
        /// preserved across walks via LoadSlots.
        /// </summary>
        private KerbalsModule BuildModuleWithChainedReservations(
            string standInJeb, string standInBill, string standInBob)
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");

            AppendSlot(slotsNode, "Jebediah Kerman", "Pilot", standInJeb);
            AppendSlot(slotsNode, "Bill Kerman", "Engineer", standInBill);
            AppendSlot(slotsNode, "Bob Kerman", "Scientist", standInBob);

            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            // Capsule recording with all three crew Aboard — generates the
            // reservations on each walk.
            var capsule = new Recording
            {
                RecordingId = "rec-capsule",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                ExplicitStartUT = 0,
                ExplicitEndUT = 200,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Aboard },
                    { "Bill Kerman", KerbalEndState.Aboard },
                    { "Bob Kerman", KerbalEndState.Aboard },
                },
            };
            var snap = new ConfigNode("VESSEL");
            var part = snap.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            part.AddValue("crew", "Bill Kerman");
            part.AddValue("crew", "Bob Kerman");
            capsule.GhostVisualSnapshot = snap;
            capsule.VesselSnapshot = snap;
            RecordingStore.AddRecordingWithTreeForTesting(capsule);

            var actions = new List<GameAction>
            {
                MakeAssignmentAction("Jebediah Kerman", "Pilot"),
                MakeAssignmentAction("Bill Kerman", "Engineer"),
                MakeAssignmentAction("Bob Kerman", "Scientist"),
            };

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
        }

        private static void AppendSlot(ConfigNode slotsNode, string owner, string trait,
            string standInName)
        {
            var slot = slotsNode.AddNode("SLOT");
            slot.AddValue("owner", owner);
            slot.AddValue("trait", trait);
            var entry = slot.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", standInName);
        }

        private static GameAction MakeAssignmentAction(string kerbalName, string trait,
            string recordingId = "rec-capsule")
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbalName,
                KerbalRole = trait,
                StartUT = 0f,
                EndUT = 200f,
                KerbalEndStateField = KerbalEndState.Aboard,
                UT = 0.0,
            };
        }

        // ---------- Test cases ---------------------------------------------

        /// <summary>
        /// Happy path matching the bug repro (KSP.log lines ~16106-16109): the
        /// rescue path placed the original three crew back into the spawned
        /// vessel and the spawn's UnreserveCrewInSnapshot removed their
        /// stand-ins from the roster. Both signals fire (rescue-placed marker
        /// AND on-live-vessel) — the next recalc walk's ApplyToRoster must
        /// NOT recreate those stand-ins, must log the rescue-completion guard
        /// skip line per kerbal, and must surface the per-walk summary
        /// counter.
        /// </summary>
        [Fact]
        public void ApplyToRoster_RescuePlacedAndOnLiveVessel_GuardSkipsRecreate()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            // Rescue path ran for all three: marker set + on a live vessel,
            // historical stand-ins NOT in the roster.
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");
            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.False(roster.Contains("Erilan Kerman"));
            Assert.False(roster.Contains("Debgas Kerman"));
            Assert.False(roster.Contains("Rodbro Kerman"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Jebediah Kerman")
                && l.Contains("rescuePlaced=true")
                && l.Contains("onLiveVessel=true")
                && l.Contains("Erilan Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Bill Kerman")
                && l.Contains("Debgas Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Bob Kerman")
                && l.Contains("Rodbro Kerman"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired")
                && l.Contains("skipped 3 stand-in"));

            // P1 review (third pass): the per-walk summary line surfaces
            // fired vs declined counts and pins the persistent-marker
            // contract in the log payload itself.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard summary")
                && l.Contains("fired=3")
                && l.Contains("marker persistent"));

            // P1 review (third pass): markers are PERSISTENT across walks.
            // The guard fires every recalc walk for the lifetime of the
            // rescue because the reservation slot is rebuilt each pass while
            // slot.Chain survives. Bulk lifecycle paths wipe the set on
            // session / rewind / wipe-all boundaries.
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                "Marker for 'Jebediah Kerman' must survive guard fire (persistent — not consumed)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bill Kerman"),
                "Marker for 'Bill Kerman' must survive guard fire (persistent — not consumed)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bob Kerman"),
                "Marker for 'Bob Kerman' must survive guard fire (persistent — not consumed)");

            // The Verbose per-skip log surfaces the persistent-marker
            // contract so KSP.log makes the design choice auditable.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("marker persistent — not consumed on fire"));

            // The per-slot recreated/created counters must report zero.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("ApplyToRoster complete")
                && l.Contains("0 created")
                && l.Contains("0 recreated"));

            // No "Recreated stand-in" Info line should fire on the happy path.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in"));
        }

        /// <summary>
        /// **P1 review regression test.** Active-vessel-not-rescue case: the
        /// reserved kerbal is seated on the active player vessel and was
        /// NEVER rescued. With the original (broken) predicate that only
        /// checked <c>IsKerbalOnLiveVessel</c>, the guard would fire and
        /// <c>TryCreateGeneratedStandIn</c> would be skipped, leaving
        /// <see cref="SwapReservedCrewInFlight"/> with nothing to swap. The
        /// fixed predicate requires the rescue-placed marker too — the
        /// reservation here is fresh, no marker, so the guard must NOT fire,
        /// and <see cref="CrewReservationManager.SetReplacement"/> must end
        /// up with a mapping for SwapReservedCrewInFlight to consume.
        /// </summary>
        [Fact]
        public void ApplyToRoster_OnLiveVesselButNotRescuePlaced_GuardDeclines_StandInGenerated()
        {
            // Pre-loaded slot for Jeb with NO chain entry (fresh slot — depth 0
            // will be a null pending-generation placeholder after PostWalk).
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var jebSlot = slotsNode.AddNode("SLOT");
            jebSlot.AddValue("owner", "Jebediah Kerman");
            jebSlot.AddValue("trait", "Pilot");
            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var rec = new Recording
            {
                RecordingId = "rec-fresh",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                ExplicitStartUT = 0,
                ExplicitEndUT = 100,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Aboard },
                },
            };
            var snap = new ConfigNode("VESSEL");
            snap.AddNode("PART").AddValue("crew", "Jebediah Kerman");
            rec.GhostVisualSnapshot = snap;
            rec.VesselSnapshot = snap;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            module.Reset();
            module.PrePass(new List<GameAction>());
            module.ProcessAction(MakeAssignmentAction("Jebediah Kerman", "Pilot",
                recordingId: "rec-fresh"));
            module.PostWalk();

            Assert.True(module.Slots.ContainsKey("Jebediah Kerman"));
            Assert.Single(module.Slots["Jebediah Kerman"].Chain);
            Assert.Null(module.Slots["Jebediah Kerman"].Chain[0]);

            // Active player vessel: Jeb is seated. NO rescue marker — this is
            // a fresh reservation, the kerbal happens to be on the player's
            // own ship and rescue path never ran for him.
            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            logLines.Clear();
            module.ApplyToRoster(roster);

            // Stand-in must be generated (the bug the reviewer caught: the
            // pre-fix guard would skip this).
            Assert.NotNull(module.Slots["Jebediah Kerman"].Chain[0]);
            string standInName = module.Slots["Jebediah Kerman"].Chain[0];
            Assert.True(roster.Contains(standInName));

            // SetReplacement must have populated the mapping for
            // SwapReservedCrewInFlight.
            Assert.True(CrewReservationManager.CrewReplacements
                .ContainsKey("Jebediah Kerman"),
                "CrewReservationManager mapping must exist for SwapReservedCrewInFlight");
            Assert.Equal(standInName,
                CrewReservationManager.CrewReplacements["Jebediah Kerman"]);

            // The "Rescue-completion guard:" skip line must NOT fire — but
            // the "guard declined" diagnostic must, so the decision is
            // visible in KSP.log.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:")
                && l.Contains("skipping stand-in"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard declined")
                && l.Contains("Jebediah Kerman")
                && l.Contains("legitimate fresh reservation"));
            // The summary counter line must NOT fire (no skipped stand-ins).
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired"));

            // The generate path's normal log fires.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in generated:")
                && l.Contains(standInName));
        }

        /// <summary>
        /// Mixed case: rescue path ran for one of three (Jeb), the other two
        /// (Bill / Bob) were lost — their slot.Chain[0] stand-ins were
        /// removed by UnreserveCrewInSnapshot but no rescue happened for them
        /// and no original is on a live vessel for them. The guard must fire
        /// only for Jeb and let the legitimate recreate path run for Bill /
        /// Bob with the explicit Verbose preamble that pins the fall-through
        /// path.
        /// </summary>
        [Fact]
        public void ApplyToRoster_OnlyOneRescuePlaced_OnlyThatStandInSkipped()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            // Bill and Bob were not rescued and not on a live vessel.

            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.False(roster.Contains("Erilan Kerman"));   // guarded out
            Assert.True(roster.Contains("Debgas Kerman"));    // legitimately recreated
            Assert.True(roster.Contains("Rodbro Kerman"));    // legitimately recreated

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:")
                && l.Contains("Jebediah Kerman")
                && l.Contains("Erilan Kerman"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Debgas Kerman")
                && l.Contains("rescuePlaced=False")
                && l.Contains("onLiveVessel=False"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Rodbro Kerman")
                && l.Contains("rescuePlaced=False"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in 'Debgas Kerman'"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in 'Rodbro Kerman'"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired")
                && l.Contains("skipped 1 stand-in"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("ApplyToRoster complete")
                && l.Contains("2 recreated"));
        }

        /// <summary>
        /// Negative path: rescue did not happen at all (no marker, no live
        /// vessel). The guard must NOT fire and the legacy recreate path
        /// must fire for every chain entry. Pinning this path proves the
        /// guard does not break the historical "stand-in vanished from the
        /// roster between walks" recovery path.
        /// </summary>
        [Fact]
        public void ApplyToRoster_NoRescueMarkerAndNotOnLiveVessel_NoGuardFires()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            var roster = new GuardFakeRoster(); // nobody on a live vessel,
                                                // no rescue marker either

            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.True(roster.Contains("Erilan Kerman"));
            Assert.True(roster.Contains("Debgas Kerman"));
            Assert.True(roster.Contains("Rodbro Kerman"));

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:")
                && l.Contains("skipping stand-in"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard declined"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("ApplyToRoster complete")
                && l.Contains("3 recreated"));
        }

        /// <summary>
        /// Rescue marker exists but the vessel was destroyed after rescue —
        /// kerbal is no longer on a live vessel. The combined predicate must
        /// fall through to the legitimate recreate path: the stand-in is
        /// genuinely needed because the original is no longer in scene. This
        /// pins the "marker without live-vessel must not fire" half of the
        /// combined predicate.
        /// </summary>
        [Fact]
        public void ApplyToRoster_RescueMarkerButNotOnLiveVessel_GuardDeclines_StandInRecreated()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            // No MarkOnLiveVessel call — the rescued vessel was destroyed.

            var roster = new GuardFakeRoster();

            logLines.Clear();
            module.ApplyToRoster(roster);

            // Stand-in must be recreated — the rescue is no longer effective.
            Assert.True(roster.Contains("Erilan Kerman"));

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:")
                && l.Contains("skipping stand-in")
                && l.Contains("Jebediah Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Erilan Kerman")
                && l.Contains("rescuePlaced=True")
                && l.Contains("onLiveVessel=False"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in 'Erilan Kerman'"));
        }

        /// <summary>
        /// Defense-in-depth: rescue placed Jeb (marker + live vessel) AND the
        /// stand-in is somehow already in the roster (e.g. from a prior walk
        /// that didn't fully clean up). The guard fires (no recreate), and
        /// no spurious "Failed to recreate stand-in" warn appears.
        /// </summary>
        [Fact]
        public void ApplyToRoster_RescueComplete_StandInAlreadyAvailable_NoChange()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            // Pre-existing stand-in entries (already in roster from a prior
            // walk that wasn't fully cleaned up).
            var roster = new GuardFakeRoster();
            roster.Add("Erilan Kerman", ProtoCrewMember.RosterStatus.Available);
            roster.Add("Debgas Kerman", ProtoCrewMember.RosterStatus.Available);
            roster.Add("Rodbro Kerman", ProtoCrewMember.RosterStatus.Available);

            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            logLines.Clear();
            module.ApplyToRoster(roster);

            // No spurious recreate, no Warning.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Failed to recreate stand-in"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired")
                && l.Contains("skipped 3 stand-in"));
        }

        /// <summary>
        /// Pending generation (slot.Chain[i] == null) AND the rescue path
        /// successfully placed the original. The guard must skip the
        /// <c>TryCreateGeneratedStandIn</c> path so a brand-new replacement
        /// is not minted while the rescue is still effective.
        /// </summary>
        [Fact]
        public void ApplyToRoster_PendingGeneration_RescueComplete_GuardSkipsCreate()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");

            // Pre-loaded slot for Jeb with NO chain entry — depth 0 will be
            // synthesized by EnsureChainDepth as a null pending-generation
            // placeholder.
            var jebSlot = slotsNode.AddNode("SLOT");
            jebSlot.AddValue("owner", "Jebediah Kerman");
            jebSlot.AddValue("trait", "Pilot");
            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var rec = new Recording
            {
                RecordingId = "rec-fresh",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                ExplicitStartUT = 0,
                ExplicitEndUT = 100,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jebediah Kerman", KerbalEndState.Aboard },
                },
            };
            var snap = new ConfigNode("VESSEL");
            snap.AddNode("PART").AddValue("crew", "Jebediah Kerman");
            rec.GhostVisualSnapshot = snap;
            rec.VesselSnapshot = snap;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = new List<GameAction>
            {
                MakeAssignmentAction("Jebediah Kerman", "Pilot", recordingId: "rec-fresh"),
            };
            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            Assert.True(module.Slots.ContainsKey("Jebediah Kerman"));
            Assert.Single(module.Slots["Jebediah Kerman"].Chain);
            Assert.Null(module.Slots["Jebediah Kerman"].Chain[0]);

            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");

            logLines.Clear();
            module.ApplyToRoster(roster);

            // The placeholder must remain null (no name minted) because the
            // guard intercepted the generate path.
            Assert.Null(module.Slots["Jebediah Kerman"].Chain[0]);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:")
                && l.Contains("Jebediah Kerman")
                && l.Contains("rescuePlaced=true")
                && l.Contains("onLiveVessel=true")
                && l.Contains("<pending>"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in generated:"));
        }

        /// <summary>
        /// **P1 review (second + third pass) — production lifecycle regression.**
        ///
        /// <para>
        /// The ORIGINAL P1 fix had per-name <see cref="CrewReservationManager.CleanUpReplacement"/>
        /// clear the rescue-placed marker. In production the spawn pipeline
        /// (<see cref="VesselSpawner.RespawnVessel"/> /
        /// <see cref="VesselSpawner.SpawnAtPosition"/>) calls
        /// <see cref="VesselSpawner.RescueReservedMissingCrewInSnapshot"/>
        /// (which calls <see cref="CrewReservationManager.MarkRescuePlaced"/>)
        /// and IMMEDIATELY follows with
        /// <see cref="CrewReservationManager.UnreserveCrewInSnapshot"/> on the
        /// SAME snapshot — and the unreserve goes through CleanUpReplacement
        /// for every reserved kerbal in the snapshot. The marker was cleared
        /// before the next <see cref="KerbalsModule.ApplyToRoster"/> walk
        /// could read it; the guard read <c>IsRescuePlaced=false</c>; the
        /// stand-in was regenerated; the bug PR #595 was meant to fix
        /// returned in production.
        /// </para>
        ///
        /// <para>
        /// Second-pass fix: <c>CleanUpReplacement</c> no longer clears the
        /// marker; the guard one-shot consumed the marker on fire instead.
        /// </para>
        ///
        /// <para>
        /// Third-pass fix (this test's invariant): the guard does NOT consume
        /// the marker either. The reservation slot is rebuilt on every recalc
        /// walk while <c>slot.Chain</c> survives, so the guard must fire on
        /// every subsequent <c>ApplyToRoster</c> pass for the lifetime of the
        /// rescue. <see cref="LedgerOrchestrator.RecalculateAndPatch"/> fires
        /// from 14+ call sites (every commit, KSC spending event, vessel
        /// recovery, warp exit at <c>ParsekFlight.cs:6366</c>, scene
        /// transition, save load); a one-shot consume design failed on the
        /// very next trigger. The marker is now PERSISTENT across walks and
        /// only bulk-cleared on session / rewind / wipe-all boundaries.
        /// </para>
        ///
        /// <para>
        /// The full <c>UnreserveCrewInSnapshot</c> path is roster-gated and
        /// short-circuits without a live <see cref="HighLogic.CurrentGame"/>,
        /// so the in-game test
        /// <c>RuntimeTests.RescueCompletionGuard_RescueThenUnreserveThenApplyToRoster_MarkerSurvives</c>
        /// reinforces this with the actual production call site under a real
        /// roster.
        /// </para>
        /// </summary>
        [Fact]
        public void RescueThenUnreserveThenApplyToRoster_GuardFiresAndStandInNotRegenerated()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            // Step 1: simulate the spawn pipeline's rescue path. Replacement
            // mappings exist (rebuilt by the recalc walk's PostWalk); the
            // rescue helper sets the marker for each kerbal it flipped from
            // Missing to Available.
            CrewReservationManager.SeedReplacementForTesting("Jebediah Kerman", "Erilan Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bill Kerman", "Debgas Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bob Kerman", "Rodbro Kerman");
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");

            // Step 2: simulate the spawn pipeline's UnreserveCrewInSnapshot
            // — runs CleanUpReplacement for each reserved kerbal in the
            // snapshot. Pre-fix this cleared the marker; post-fix it must NOT.
            CrewReservationManager.CleanUpReplacementForTesting("Jebediah Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bill Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bob Kerman");

            // Pin the contract: the marker MUST survive the per-name unreserve.
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                "Marker for 'Jebediah Kerman' must survive CleanUpReplacement (P1 review second pass)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bill Kerman"),
                "Marker for 'Bill Kerman' must survive CleanUpReplacement (P1 review second pass)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bob Kerman"),
                "Marker for 'Bob Kerman' must survive CleanUpReplacement (P1 review second pass)");
            // The replacement dict was wiped per-name (matches production).
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey("Jebediah Kerman"));
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey("Bill Kerman"));
            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey("Bob Kerman"));

            // Step 3: the next ApplyToRoster walk runs. Originals are still
            // on the (rescued) live vessel; both signals fire; the guard
            // skips the historical stand-ins. P1 review (third pass): the
            // marker is PERSISTENT — the guard does not consume it on fire.
            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            logLines.Clear();
            module.ApplyToRoster(roster);

            // No stand-in was regenerated.
            Assert.False(roster.Contains("Erilan Kerman"));
            Assert.False(roster.Contains("Debgas Kerman"));
            Assert.False(roster.Contains("Rodbro Kerman"));

            // Per-kerbal guard skip lines fired.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Jebediah Kerman")
                && l.Contains("Erilan Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired")
                && l.Contains("skipped 3 stand-in"));
            // P1 review (third pass): the summary line surfaces the
            // persistent-marker contract.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard summary")
                && l.Contains("fired=3")
                && l.Contains("marker persistent"));

            // P1 review (third pass): the marker MUST still be set after the
            // guard fired. The slot is rebuilt on every recalc walk while
            // slot.Chain survives — every subsequent walk needs to fire the
            // guard the same way for the lifetime of the rescue.
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                "Marker must SURVIVE the guard fire (P1 review third pass — persistent across walks)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bill Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Bob Kerman"));
            // No "Consumed rescue-placed marker" log line — the guard does
            // not consume on fire.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[CrewReservation]")
                && l.Contains("Consumed rescue-placed marker"));
        }

        /// <summary>
        /// Lifecycle pin: per-name <see cref="CrewReservationManager.CleanUpReplacement"/>
        /// (exercised by <see cref="CrewReservationManager.UnreserveCrewInSnapshot"/>
        /// in production) does NOT clear the rescue-placed marker. This is
        /// the new contract installed by the P1 review second pass — pre-fix
        /// the marker was cleared here and the guard never observed it on
        /// the next walk.
        /// </summary>
        [Fact]
        public void CleanUpReplacement_DoesNotClearRescuePlacedMarker()
        {
            CrewReservationManager.SeedReplacementForTesting("Jebediah Kerman", "Erilan Kerman");
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");

            CrewReservationManager.CleanUpReplacementForTesting("Jebediah Kerman");

            Assert.False(CrewReservationManager.CrewReplacements.ContainsKey("Jebediah Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                "Per-name CleanUpReplacement must NOT clear the rescue-placed marker");
        }

        /// <summary>
        /// Lifecycle pin: <see cref="CrewReservationManager.ClearRescuePlaced"/>
        /// remains usable for the bulk lifecycle paths and the test fixture.
        /// P1 review (third pass): the per-name guard-fire site no longer
        /// consumes the marker (the previous one-shot consume design failed
        /// because <see cref="LedgerOrchestrator.RecalculateAndPatch"/> fires
        /// from many call sites across a session). Bulk-clear is the only
        /// in-process clear path.
        /// </summary>
        [Fact]
        public void ClearRescuePlaced_BulkLifecycleSemantics()
        {
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            logLines.Clear();
            CrewReservationManager.ClearRescuePlaced("Jebediah Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Idempotent on second call (no log on no-op).
            CrewReservationManager.ClearRescuePlaced("Jebediah Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            Assert.Contains(logLines, l =>
                l.Contains("[CrewReservation]")
                && l.Contains("Cleared rescue-placed marker")
                && l.Contains("Jebediah Kerman")
                && l.Contains("bulk lifecycle"));
        }

        /// <summary>
        /// Death + revive re-mark cycle: a kerbal can be marked, then a bulk
        /// lifecycle path (e.g. <see cref="CrewReservationManager.ClearReplacements"/>
        /// at session boundary) wipes the marker, then a subsequent rescue
        /// marks the kerbal again and the guard fires fresh. This pins the
        /// idempotent re-mark contract — important for repeated Re-Fly cycles
        /// on the same kerbal across save/load or scene transitions.
        /// </summary>
        [Fact]
        public void MarkRescuePlaced_AfterBulkClear_IdempotentRemark()
        {
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Bulk lifecycle path wipes the marker (mirrors ClearReplacements
            // / LoadCrewReplacements / RestoreReplacements / Reset on session
            // / rewind / wipe-all boundaries).
            CrewReservationManager.ClearRescuePlaced("Jebediah Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Second rescue cycle: re-mark must work.
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Idempotent: marking an already-marked kerbal is a no-op.
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Bulk clear cleans up.
            CrewReservationManager.ClearRescuePlaced("Jebediah Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));
        }

        /// <summary>
        /// Lifecycle pin: <see cref="CrewReservationManager.ResetReplacementsForTesting"/>
        /// clears both the replacement dict and the rescue-placed marker
        /// set, so test fixtures see a clean signal between cases.
        /// </summary>
        [Fact]
        public void RescuePlacedMarker_ClearedByReset()
        {
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Bill Kerman"));

            CrewReservationManager.ResetReplacementsForTesting();

            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));
            Assert.False(CrewReservationManager.IsRescuePlaced("Bill Kerman"));
        }

        /// <summary>
        /// Lifecycle pin: <see cref="CrewReservationManager.RestoreReplacements"/>
        /// (Rewind-to-Staging Phase 6 reconciliation) clears the marker set
        /// when re-seeding the replacement dict from the captured bundle.
        /// The post-load spawn pipeline re-populates the markers as it runs.
        /// </summary>
        [Fact]
        public void RescuePlacedMarker_ClearedByRestoreReplacements()
        {
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            var captured = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Erilan Kerman" },
            };
            CrewReservationManager.RestoreReplacements(captured);

            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));
            Assert.False(CrewReservationManager.IsRescuePlaced("Bill Kerman"));
            Assert.True(CrewReservationManager.CrewReplacements.ContainsKey("Jebediah Kerman"));
        }

        /// <summary>
        /// Lifecycle pin: <see cref="CrewReservationManager.LoadCrewReplacements"/>
        /// (cold-load) clears the marker set. Markers are session-scoped:
        /// the rescue path runs in-flight, the marker drives the next
        /// recalc walk's guard decision; a cold load starts a new session.
        /// </summary>
        [Fact]
        public void RescuePlacedMarker_ClearedByLoadCrewReplacements()
        {
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Empty save load: no CREW_REPLACEMENTS node.
            var node = new ConfigNode("PARSEK");
            CrewReservationManager.LoadCrewReplacements(node);

            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));
        }

        // ---------- Multi-walk regression coverage (P1 review third pass) --

        /// <summary>
        /// **P1 review (third pass) — multi-walk regression.**
        ///
        /// <para>
        /// The previous P1 fix consumed the rescue-placed marker the first
        /// time the <see cref="KerbalsModule.ApplyToRoster"/> guard fired.
        /// In production <see cref="LedgerOrchestrator.RecalculateAndPatch"/>
        /// is invoked from 14+ call sites across a session — every commit,
        /// every KSC spending event, every vessel recovery, warp exit at
        /// <c>ParsekFlight.cs:6366</c>, scene transition, save load, etc.
        /// Each one rebuilds the reservation slot from the recording's
        /// <c>KerbalAssignment</c> action while <c>slot.Chain</c> survives,
        /// re-presenting the kerbal as needing a stand-in. The first
        /// trigger after the merge tail consumed the marker, the next
        /// trigger saw <c>IsRescuePlaced=false</c>, took the
        /// "live-but-no-marker" branch, and regenerated the stand-in —
        /// exactly the bug PR #595 was meant to fix.
        /// </para>
        ///
        /// <para>
        /// The third-pass fix makes the marker PERSISTENT across walks.
        /// This test exercises three back-to-back ApplyToRoster passes
        /// (mimicking three recalc triggers within the same session) on the
        /// same rescued-on-live-vessel state and asserts: every pass fires
        /// the guard, no pass regenerates the stand-in, and the marker
        /// remains set after every pass.
        /// </para>
        ///
        /// <para>
        /// The previous tests only ran <c>ApplyToRoster</c> once, missing
        /// this regression — that false-confidence test pattern is the
        /// reason the third-pass review was needed.
        /// </para>
        /// </summary>
        [Fact]
        public void MultipleApplyToRosterPasses_RescuedKerbalOnLiveVessel_GuardFiresEveryPass()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            // Stage the post-rescue + post-unreserve state via the
            // production-mirroring seams.
            CrewReservationManager.SeedReplacementForTesting("Jebediah Kerman", "Erilan Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bill Kerman", "Debgas Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bob Kerman", "Rodbro Kerman");
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Jebediah Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bill Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bob Kerman");

            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            // Walk 1: typical merge-tail recalc. Guard fires.
            logLines.Clear();
            module.ApplyToRoster(roster);
            AssertGuardFiredForAllThreeAndMarkerSurvives(roster, "walk 1");

            // Walk 2: the next trigger (warp exit, save, scene transition,
            // any of 14+ call sites). The slot is rebuilt; slot.Chain
            // survived; the original is still on the rescued live vessel.
            // The guard MUST fire again because the marker is persistent.
            logLines.Clear();
            module.ApplyToRoster(roster);
            AssertGuardFiredForAllThreeAndMarkerSurvives(roster, "walk 2");

            // Walk 3: another arbitrary trigger later in the same session.
            // Same expectation — the marker is still set, the guard fires,
            // the historical stand-ins are still not in the roster.
            logLines.Clear();
            module.ApplyToRoster(roster);
            AssertGuardFiredForAllThreeAndMarkerSurvives(roster, "walk 3");
        }

        private void AssertGuardFiredForAllThreeAndMarkerSurvives(
            GuardFakeRoster roster, string walkLabel)
        {
            // Stand-ins still not in roster.
            Assert.False(roster.Contains("Erilan Kerman"),
                $"{walkLabel}: 'Erilan Kerman' must NOT be regenerated");
            Assert.False(roster.Contains("Debgas Kerman"),
                $"{walkLabel}: 'Debgas Kerman' must NOT be regenerated");
            Assert.False(roster.Contains("Rodbro Kerman"),
                $"{walkLabel}: 'Rodbro Kerman' must NOT be regenerated");

            // Guard fired and surfaced the persistent-marker contract.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired")
                && l.Contains("skipped 3 stand-in"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard summary")
                && l.Contains("fired=3")
                && l.Contains("marker persistent"));

            // Markers persisted across the walk.
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                $"{walkLabel}: marker for 'Jebediah Kerman' must survive (persistent)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bill Kerman"),
                $"{walkLabel}: marker for 'Bill Kerman' must survive (persistent)");
            Assert.True(CrewReservationManager.IsRescuePlaced("Bob Kerman"),
                $"{walkLabel}: marker for 'Bob Kerman' must survive (persistent)");

            // No "Recreated stand-in" Info line.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Recreated stand-in"));
        }

        /// <summary>
        /// **P1 review (third pass) — bulk-clear lifecycle pin across walks.**
        ///
        /// <para>
        /// Two ApplyToRoster passes on the rescued-on-live-vessel state
        /// (both fire the guard; both leave the marker set). Then a bulk
        /// lifecycle path (<see cref="CrewReservationManager.ClearReplacements"/>
        /// — mirrors a session boundary / scene transition wipe-all) wipes
        /// the marker set. The third pass must fall through to the legitimate
        /// recreate path because the marker is gone, exactly the same way a
        /// fresh post-load session would.
        /// </para>
        /// </summary>
        [Fact]
        public void MultipleApplyToRosterPasses_BulkClearWipesMarker_NextPassRegeneratesStandIn()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            CrewReservationManager.SeedReplacementForTesting("Jebediah Kerman", "Erilan Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bill Kerman", "Debgas Kerman");
            CrewReservationManager.SeedReplacementForTesting("Bob Kerman", "Rodbro Kerman");
            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Jebediah Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bill Kerman");
            CrewReservationManager.CleanUpReplacementForTesting("Bob Kerman");

            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            // Walks 1 and 2 — guard fires both times, markers persist.
            logLines.Clear();
            module.ApplyToRoster(roster);
            Assert.False(roster.Contains("Erilan Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            logLines.Clear();
            module.ApplyToRoster(roster);
            Assert.False(roster.Contains("Erilan Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Bulk wipe — mirrors session boundary path. Use the per-name
            // clear directly here because ClearReplacements requires a live
            // KSP roster which xUnit cannot stand up; the per-name clear
            // exercises the same marker-set mutation.
            CrewReservationManager.ClearRescuePlaced("Jebediah Kerman");
            CrewReservationManager.ClearRescuePlaced("Bill Kerman");
            CrewReservationManager.ClearRescuePlaced("Bob Kerman");
            Assert.False(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Walk 3: marker is gone. Originals are still on the live
            // vessel, but the predicate now reads (rescuePlaced=false,
            // onLiveVessel=true) — the legitimate-fresh-reservation branch.
            // Stand-ins must regenerate, mirroring the post-load expectation.
            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.True(roster.Contains("Erilan Kerman"),
                "After bulk clear: stand-in 'Erilan Kerman' must regenerate (marker gone)");
            Assert.True(roster.Contains("Debgas Kerman"));
            Assert.True(roster.Contains("Rodbro Kerman"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard declined")
                && l.Contains("legitimate fresh reservation"));
        }

        /// <summary>
        /// **P1 review (third pass) — stale-marker harmlessness.**
        ///
        /// <para>
        /// Within a session the marker accumulates if the rescued vessel is
        /// destroyed (no per-name clear path is reachable cleanly without
        /// significant refactoring). The combined predicate self-protects:
        /// the second clause requires <see cref="IKerbalRosterFacade.IsKerbalOnLiveVessel"/>
        /// = true, so a stale-true marker for a kerbal who is no longer on
        /// any vessel falls through to the legitimate recreate path.
        /// </para>
        ///
        /// <para>
        /// First pass: rescue placed Jeb on a live vessel; guard fires;
        /// marker persists. Second pass: rescued vessel destroyed (Jeb no
        /// longer on a live vessel); guard takes the "marker without live
        /// vessel" branch and the stand-in is recreated — even though the
        /// marker is still set. This is the safety property that makes
        /// bulk-only clearing a correct strategy.
        /// </para>
        /// </summary>
        [Fact]
        public void MultipleApplyToRosterPasses_StaleMarker_VesselDestroyed_NextPassRecreatesStandIn()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            CrewReservationManager.MarkRescuePlaced("Jebediah Kerman");
            CrewReservationManager.MarkRescuePlaced("Bill Kerman");
            CrewReservationManager.MarkRescuePlaced("Bob Kerman");

            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            roster.MarkOnLiveVessel("Bill Kerman");
            roster.MarkOnLiveVessel("Bob Kerman");

            // Walk 1: guard fires for all three.
            logLines.Clear();
            module.ApplyToRoster(roster);
            Assert.False(roster.Contains("Erilan Kerman"));
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"));

            // Vessel destroyed: Jeb no longer on a live vessel. The marker
            // is still set because the bulk-only clearing strategy makes no
            // attempt to invalidate it on per-vessel destruction (would
            // need a destruction-event hook; not implemented).
            roster = new GuardFakeRoster();
            // No MarkOnLiveVessel calls — kerbals are gone.
            Assert.True(CrewReservationManager.IsRescuePlaced("Jebediah Kerman"),
                "Sanity: marker is intentionally still set (bulk-only clear strategy)");

            // Walk 2: predicate is (rescuePlaced=true, onLiveVessel=false).
            // The combined-predicate guard takes the "marker without live
            // vessel" branch (declined), and the legitimate-recreate path
            // mints the stand-in.
            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.True(roster.Contains("Erilan Kerman"),
                "After vessel destroyed: stand-in must recreate (the live-vessel " +
                "second clause makes a stale marker harmless — legitimate-recreate path fires)");
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Erilan Kerman")
                && l.Contains("rescuePlaced=True")
                && l.Contains("onLiveVessel=False"));
        }

        // ---------- Test-only roster facade --------------------------------

        /// <summary>
        /// In-process facade mirroring the production
        /// <see cref="KerbalsModule.IKerbalRosterFacade"/> contract for
        /// testing. Distinct from <see cref="KerbalLoadDiagnosticsTests"/>'s
        /// nested FakeRoster so this test class can exercise the rescue
        /// guard surface without coupling to the diagnostics fixture.
        /// </summary>
        private sealed class GuardFakeRoster : KerbalsModule.IKerbalRosterFacade
        {
            private readonly Dictionary<string, ProtoCrewMember.RosterStatus> members
                = new Dictionary<string, ProtoCrewMember.RosterStatus>(System.StringComparer.Ordinal);
            private readonly HashSet<string> liveVesselCrew
                = new HashSet<string>(System.StringComparer.Ordinal);
            private int generatedCounter;

            public void Add(string name, ProtoCrewMember.RosterStatus status)
            {
                members[name] = status;
            }

            public bool Contains(string name)
            {
                return members.ContainsKey(name);
            }

            public void MarkOnLiveVessel(string name)
            {
                if (!string.IsNullOrEmpty(name)) liveVesselCrew.Add(name);
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                return members.TryGetValue(name, out status);
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedCounter++;
                generatedName = "Generated-" + generatedCounter;
                members[generatedName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                members[desiredName] = ProtoCrewMember.RosterStatus.Available;
                return true;
            }

            public bool TryRemove(string name)
            {
                return members.Remove(name);
            }

            public bool IsKerbalOnLiveVessel(string kerbalName)
            {
                return !string.IsNullOrEmpty(kerbalName) && liveVesselCrew.Contains(kerbalName);
            }
        }
    }
}
