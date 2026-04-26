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
    /// Test fixtures use a fake <see cref="KerbalsModule.IKerbalRosterFacade"/>
    /// from <see cref="KerbalLoadDiagnosticsTests"/>'s test infrastructure,
    /// extended with <c>MarkOnLiveVessel</c> to model the rescue having
    /// placed the original kerbal back on a loaded vessel.
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
        /// stand-ins from the roster. The next recalc walk's ApplyToRoster
        /// must NOT recreate those stand-ins, must log the rescue-completion
        /// guard skip line per kerbal, and must surface the per-walk summary
        /// counter.
        /// </summary>
        [Fact]
        public void ApplyToRoster_OriginalsOnLiveVessel_GuardSkipsRecreate()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            // Rescue path: originals are now on a live vessel and the
            // historical stand-ins are NOT in the roster.
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
                && l.Contains("already placed on active vessel via rescue path")
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
        /// Negative path: rescue did not happen for one of the crew (e.g.
        /// the spawn snapshot crewed only Jeb back into the pod; Bill / Bob
        /// were lost or stayed on a non-loaded vessel). The guard must fire
        /// only for Jeb and let the legitimate recreate path run for Bill /
        /// Bob, with the explicit Verbose preamble that pins the
        /// fall-through path.
        /// </summary>
        [Fact]
        public void ApplyToRoster_OneOriginalOnLiveVessel_OnlyThatStandInSkipped()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");
            // Bill and Bob were not rescued — their slot.Chain[0] stand-ins
            // were removed by UnreserveCrewInSnapshot but no original is on
            // a live vessel for them. ApplyToRoster must fall through to
            // TryRecreateStandIn for those two.

            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.False(roster.Contains("Erilan Kerman"));   // guarded out
            Assert.True(roster.Contains("Debgas Kerman"));    // legitimately recreated
            Assert.True(roster.Contains("Rodbro Kerman"));    // legitimately recreated

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Jebediah Kerman")
                && l.Contains("Erilan Kerman"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Debgas Kerman")
                && l.Contains("not on live vessel"));
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in recreate:")
                && l.Contains("Rodbro Kerman")
                && l.Contains("not on live vessel"));

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
        /// Negative path: rescue did not happen at all (no original on a live
        /// vessel). The guard must NOT fire and the legacy recreate path
        /// must fire for every chain entry. Pinning this path proves the
        /// guard does not break the historical "stand-in vanished from the
        /// roster between walks" recovery path.
        /// </summary>
        [Fact]
        public void ApplyToRoster_NoOriginalOnLiveVessel_NoGuardFires()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            var roster = new GuardFakeRoster(); // nobody on a live vessel

            logLines.Clear();
            module.ApplyToRoster(roster);

            Assert.True(roster.Contains("Erilan Kerman"));
            Assert.True(roster.Contains("Debgas Kerman"));
            Assert.True(roster.Contains("Rodbro Kerman"));

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard:"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard fired"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("ApplyToRoster complete")
                && l.Contains("3 recreated"));
        }

        /// <summary>
        /// Edge case: original AND historical stand-in are both somehow on
        /// loaded vessels (e.g. the player undocked the stand-in onto a
        /// separate craft before rescue). The guard must still fire for the
        /// slot's depth-0 entry because slot.OwnerName is on a live vessel,
        /// and ApplyToRoster must not double-create the stand-in. This is
        /// the defense-in-depth path that complements the existing Spawner
        /// "Crew dedup" warn at line 18769 of the bug log.
        /// </summary>
        [Fact]
        public void ApplyToRoster_OriginalOnLiveVessel_StandInAlreadyAvailable_NoChange()
        {
            var module = BuildModuleWithChainedReservations(
                "Erilan Kerman", "Debgas Kerman", "Rodbro Kerman");

            var roster = new GuardFakeRoster();
            // Pre-existing stand-in entries (already in roster from a prior
            // walk that wasn't fully cleaned up).
            roster.Add("Erilan Kerman", ProtoCrewMember.RosterStatus.Available);
            roster.Add("Debgas Kerman", ProtoCrewMember.RosterStatus.Available);
            roster.Add("Rodbro Kerman", ProtoCrewMember.RosterStatus.Available);
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
        /// Edge case: pending generation (slot.Chain[i] == null), original
        /// on a live vessel. The guard must skip the
        /// <c>TryCreateGeneratedStandIn</c> path so a brand-new replacement
        /// is not minted while the player still has the original.
        /// </summary>
        [Fact]
        public void ApplyToRoster_PendingGeneration_OriginalOnLiveVessel_GuardSkipsCreate()
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

            // Confirm the placeholder was added at depth 0.
            Assert.True(module.Slots.ContainsKey("Jebediah Kerman"));
            Assert.Single(module.Slots["Jebediah Kerman"].Chain);
            Assert.Null(module.Slots["Jebediah Kerman"].Chain[0]);

            var roster = new GuardFakeRoster();
            roster.MarkOnLiveVessel("Jebediah Kerman");

            logLines.Clear();
            module.ApplyToRoster(roster);

            // The placeholder must remain null (no name minted) because the
            // guard intercepted the generate path.
            Assert.Null(module.Slots["Jebediah Kerman"].Chain[0]);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Rescue-completion guard")
                && l.Contains("Jebediah Kerman")
                && l.Contains("<pending>"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Stand-in generated:"));
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
