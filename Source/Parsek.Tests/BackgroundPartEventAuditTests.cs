using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Audit tests verifying that BackgroundRecorder has complete part event coverage
    /// matching FlightRecorder. Tests verify:
    ///
    /// POLLED events (per-physics-frame in PollPartEvents) -- all 17 types:
    ///   CheckParachuteState, CheckJettisonState, CheckEngineState, CheckRcsState,
    ///   CheckDeployableState, CheckLadderState, CheckAnimationGroupState,
    ///   CheckAeroSurfaceState, CheckControlSurfaceState, CheckRobotArmScannerState,
    ///   CheckAnimateHeatState, CheckAnimateGenericState, CheckLightState,
    ///   CheckGearState, CheckCargoBayState, CheckFairingState, CheckRoboticState
    ///
    /// GAME-EVENT DRIVEN events:
    ///   onPartDie -> OnBackgroundPartDie -> Destroyed / ParachuteDestroyed
    ///   onPartJointBreak -> OnBackgroundPartJointBreak -> Decoupled
    ///
    /// NOT APPLICABLE (handled elsewhere):
    ///   Docked / Undocked -- branch management in ParsekFlight
    ///   InventoryPartPlaced / InventoryPartRemoved -- EVA-only, active vessel
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundPartEventAuditTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BackgroundPartEventAuditTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with the given background vessel PIDs.
        /// </summary>
        private RecordingTree MakeTree(params (uint pid, string recId)[] backgroundVessels)
        {
            var tree = new RecordingTree
            {
                Id = "tree_audit_test",
                TreeName = "Audit Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = "rec_active"
            };

            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                VesselName = "Active Vessel",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };

            for (int i = 0; i < backgroundVessels.Length; i++)
            {
                var (pid, recId) = backgroundVessels[i];
                tree.Recordings[recId] = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"BG Vessel {pid}",
                    VesselPersistentId = pid,
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 200.0
                };
                tree.BackgroundMap[pid] = recId;
            }

            return tree;
        }

        #region Coverage Audit: Polled Event Types

        // This gate reads the parity claim out of the IL of the two poll methods.

        [Fact]
        public void PollPartEvents_CoversAllPolledEventTypes_MatchingFlightRecorder()
        {
            // The call SET of BackgroundRecorder.PollPartEvents must equal the call set of
            // FlightRecorder.PollPartStates, restricted to the Check*State family.
            //
            // Why the call set and not a name-existence reflection check: deleting a
            // CheckXState CALL is the actual background part-event recording regression, and
            // it leaves the method (and therefore its name) on the type, so a name check
            // cannot see it. Asymmetry fails in BOTH directions - a Check* polled only by the
            // flight recorder is a background coverage gap, and one polled only in the
            // background is the mirror of that.
            Func<string, bool> isPolledCheck = n =>
                n.StartsWith("Check", StringComparison.Ordinal) &&
                n.EndsWith("State", StringComparison.Ordinal);

            var flightPolled = ILCallSet.CalledMethodNames(
                ILCallSet.Method(typeof(FlightRecorder), "PollPartStates"), isPolledCheck);
            var backgroundPolled = ILCallSet.CalledMethodNames(
                ILCallSet.Method(typeof(BackgroundRecorder), "PollPartEvents"), isPolledCheck);

            // Floor: an empty set on either side would make the equality below vacuous, and a
            // SYMMETRIC removal (the same Check*State dropped from both pollers) keeps the
            // equality green while shrinking real coverage. 19 is the count at the time of
            // writing and is a deliberate maintenance tripwire: a genuine retirement of a
            // polled state must LOWER this floor in the same commit, on purpose, rather than
            // pass unnoticed.
            Assert.True(flightPolled.Count >= 19,
                $"FlightRecorder.PollPartStates polls only {flightPolled.Count} Check*State method(s): " +
                string.Join(", ", flightPolled.OrderBy(n => n)));
            Assert.True(backgroundPolled.Count >= 19,
                $"BackgroundRecorder.PollPartEvents polls only {backgroundPolled.Count} Check*State method(s): " +
                string.Join(", ", backgroundPolled.OrderBy(n => n)));

            var missingInBackground = flightPolled.Except(backgroundPolled).OrderBy(n => n).ToList();
            var missingInFlight = backgroundPolled.Except(flightPolled).OrderBy(n => n).ToList();

            Assert.True(missingInBackground.Count == 0,
                "BackgroundRecorder.PollPartEvents does not call: " +
                string.Join(", ", missingInBackground));
            Assert.True(missingInFlight.Count == 0,
                "FlightRecorder.PollPartStates does not call: " +
                string.Join(", ", missingInFlight));
        }

        #endregion

        #region Coverage Audit: GameEvent-Driven Event Types

        [Fact]
        public void BackgroundRecorder_HasOnBackgroundPartDie_Method()
        {
            // Verifies that BackgroundRecorder has the onPartDie handler
            var bgType = typeof(BackgroundRecorder);
            var method = bgType.GetMethod("OnBackgroundPartDie",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);
            Assert.NotNull(method);
        }

        [Fact]
        public void BackgroundRecorder_HasOnBackgroundPartJointBreak_Method()
        {
            // Verifies that BackgroundRecorder has the onPartJointBreak handler
            var bgType = typeof(BackgroundRecorder);
            var method = bgType.GetMethod("OnBackgroundPartJointBreak",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);
            Assert.NotNull(method);
        }

        #endregion

        #region OnBackgroundPartDie Routing

        [Fact]
        public void OnBackgroundPartDie_NullTree_DoesNotThrow()
        {
            // A BackgroundRecorder with a null tree should silently return
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // Force tree to null via reflection (simulating teardown race)
            var treeField = typeof(BackgroundRecorder).GetField("tree",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            treeField.SetValue(bgRecorder, null);

            // Should not throw
            bgRecorder.OnBackgroundPartDie(null);
        }

        [Fact]
        public void OnBackgroundPartDie_NullPart_LogsWarning()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnBackgroundPartDie(null);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("part or vessel is null"));
        }

        #endregion

        #region OnBackgroundPartJointBreak Routing

        [Fact]
        public void OnBackgroundPartJointBreak_NullTree_DoesNotThrow()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            var treeField = typeof(BackgroundRecorder).GetField("tree",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            treeField.SetValue(bgRecorder, null);

            // Should not throw
            bgRecorder.OnBackgroundPartJointBreak(null, 0f);
        }

        [Fact]
        public void OnBackgroundPartJointBreak_NullJoint_LogsWarning()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.OnBackgroundPartJointBreak(null, 0f);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") && l.Contains("joint, child, or vessel is null"));
        }

        #endregion

        #region BackgroundVesselState DecoupledPartIds

        [Fact]
        public void InjectLoadedState_ExposesDecoupledPartIds()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            bgRecorder.InjectLoadedStateForTesting(100, "rec_bg1");

            var decoupledIds = bgRecorder.GetDecoupledPartIdsForTesting(100);
            Assert.NotNull(decoupledIds);
            Assert.Empty(decoupledIds);
        }

        [Fact]
        public void GetDecoupledPartIds_NoLoadedState_ReturnsNull()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            // 100 is in background map but has no loaded state (constructor only creates on-rails)
            var decoupledIds = bgRecorder.GetDecoupledPartIdsForTesting(100);
            Assert.Null(decoupledIds);
        }

        [Fact]
        public void DecoupledPartIds_SetIsTheDedupSurfaceExposedPerVessel()
        {
            // What this pins: the per-vessel decoupledPartIds set the loaded state
            // exposes is the surface OnBackgroundPartJointBreak dedups against, and a
            // repeat add is a no-op on it. What it CANNOT pin: the guard itself
            // (BackgroundRecorder.OnBackgroundPartJointBreak) - reaching it needs a
            // live PartJoint with a Child Part, a Vessel and an attachJoint, none of
            // which exist headlessly. The in-game BackgroundRecording category is the
            // detector for the guard.
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateForTesting(100, "rec_bg1");

            var decoupledIds = bgRecorder.GetDecoupledPartIdsForTesting(100);
            Assert.DoesNotContain(42u, decoupledIds);

            decoupledIds.Add(42u);
            Assert.Contains(42u, decoupledIds);

            // Second add is a no-op (HashSet semantics)
            bool added = decoupledIds.Add(42u);
            Assert.False(added);
        }

        #endregion

        #region Static Logic: ClassifyPartDeath delegates to FlightRecorder

        [Fact]
        public void ClassifyPartDeath_DestroyedPart_ReturnsDestroyed()
        {
            // BackgroundRecorder.OnBackgroundPartDie delegates to FlightRecorder.ClassifyPartDeath.
            // Verify the static method works correctly for the background vessel scenario.
            var states = new Dictionary<uint, int>();
            var result = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: false, states);
            Assert.Equal(PartEventType.Destroyed, result);
        }

        [Fact]
        public void ClassifyPartDeath_DeployedParachute_ReturnsParachuteDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } };
            var result = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);
            Assert.Equal(PartEventType.ParachuteDestroyed, result);
            Assert.False(states.ContainsKey(42)); // cleaned up
        }

        #endregion

        #region Static Logic: IsStructuralJointBreak delegates to FlightRecorder

        [Fact]
        public void IsStructuralJointBreak_StructuralBreak_ReturnsTrue()
        {
            // BackgroundRecorder.OnBackgroundPartJointBreak delegates to
            // FlightRecorder.IsStructuralJointBreak. Verify it returns true
            // when the broken joint IS the attach joint.
            Assert.True(FlightRecorder.IsStructuralJointBreak(
                brokenJointIsAttachJoint: true, hasAttachJoint: true));
        }

        [Fact]
        public void IsStructuralJointBreak_NonStructuralBreak_ReturnsFalse()
        {
            Assert.False(FlightRecorder.IsStructuralJointBreak(
                brokenJointIsAttachJoint: false, hasAttachJoint: true));
        }

        #endregion

        #region Subscribe/Unsubscribe Lifecycle

        [Fact]
        public void SubscribePartEvents_SetsSubscribedFlag()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);

            Assert.False(bgRecorder.IsPartEventsSubscribed);

            bgRecorder.SubscribePartEvents();
            try
            {
                Assert.True(bgRecorder.IsPartEventsSubscribed);
            }
            finally
            {
                bgRecorder.UnsubscribePartEvents();
            }
            Assert.False(bgRecorder.IsPartEventsSubscribed);
        }

        #endregion

        #region Log Assertions

        [Fact]
        public void OnBackgroundPartDie_NullPart_LogsWithBgRecorderTag()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateForTesting(100, "rec_bg1");

            bgRecorder.OnBackgroundPartDie(null);

            // Verify the log line uses [BgRecorder] subsystem tag
            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") &&
                l.Contains("OnBackgroundPartDie") &&
                l.Contains("part or vessel is null"));
        }

        [Fact]
        public void OnBackgroundPartJointBreak_NullJoint_LogsWithBgRecorderTag()
        {
            var tree = MakeTree((100, "rec_bg1"));
            var bgRecorder = new BackgroundRecorder(tree);
            bgRecorder.InjectLoadedStateForTesting(100, "rec_bg1");

            bgRecorder.OnBackgroundPartJointBreak(null, 50f);

            Assert.Contains(logLines, l =>
                l.Contains("[BgRecorder]") &&
                l.Contains("OnBackgroundPartJointBreak") &&
                l.Contains("joint, child, or vessel is null"));
        }

        #endregion
    }
}
