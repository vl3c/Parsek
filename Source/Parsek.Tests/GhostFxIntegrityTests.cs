using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="GhostFxIntegrity"/>, the pure core of the <c>GhostFxIntegrity</c>
    /// in-game category (lane FXR-1, audit B8).
    /// </summary>
    public class GhostFxIntegrityTests
    {
        private const uint Poodle = 1592830016u;
        private const uint Mainsail = 235925713u;

        private static PartEvent Ev(double ut, PartEventType type, uint pid, float value = 0f, int midx = 0)
        {
            return new PartEvent
            {
                ut = ut,
                eventType = type,
                partPersistentId = pid,
                value = value,
                moduleIndex = midx,
            };
        }

        // ---- ExpectedEnginePower ---------------------------------------------------

        [Fact]
        public void ExpectedPower_NullEvents_IsZero()
        {
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(null, 5, Poodle, 0, null));
        }

        [Fact]
        public void ExpectedPower_NoEngineEventAtAll_AutoStartsAtFullPower()
        {
            var events = new List<PartEvent> { Ev(10, PartEventType.ParachuteDeployed, 7u) };
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 0, Poodle, 0, null));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 1, Poodle, 0, null));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(new List<PartEvent>(), 0, Poodle, 0, null));
        }

        [Fact]
        public void ExpectedPower_AnyEngineEventOnAnotherPart_StartsDark()
        {
            // The fork shape: a recording with engine events leaves an engine without its
            // own events idle (no auto-start).
            var events = new List<PartEvent> { Ev(10, PartEventType.EngineIgnited, Mainsail, 1f) };
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 1, Poodle, 0, null));
        }

        [Fact]
        public void ExpectedPower_LastEngineEventOfTheKeyWins_UpToTheCursor()
        {
            var events = new List<PartEvent>
            {
                Ev(118.8, PartEventType.EngineShutdown, Poodle),
                Ev(119.58, PartEventType.EngineIgnited, Poodle, 1f),
                Ev(125.0, PartEventType.EngineThrottle, Poodle, 0.42f),
                Ev(136.02, PartEventType.EngineShutdown, Poodle),
            };
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 0, Poodle, 0, null));
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 1, Poodle, 0, null));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 2, Poodle, 0, null));
            Assert.Equal(0.42f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Poodle, 0, null));
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 4, Poodle, 0, null));
            // A cursor past the end clamps; a negative one reads nothing.
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 99, Poodle, 0, null));
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, -3, Poodle, 0, null));
        }

        [Fact]
        public void ExpectedPower_OtherModuleOfTheSamePart_DoesNotLeak()
        {
            var events = new List<PartEvent>
            {
                Ev(1, PartEventType.EngineIgnited, Poodle, 1f, midx: 1),
            };
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 1, Poodle, 0, null));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 1, Poodle, 1, null));
        }

        [Fact]
        public void ExpectedPower_DecoupledAncestor_ZeroesTheSubtree()
        {
            const uint decoupler = 50u;
            var tree = new Dictionary<uint, List<uint>>
            {
                { decoupler, new List<uint> { 60u } },
                { 60u, new List<uint> { Mainsail } },
            };
            var events = new List<PartEvent>
            {
                Ev(1, PartEventType.EngineIgnited, Mainsail, 1f),
                Ev(2, PartEventType.EngineIgnited, Poodle, 1f),
                Ev(3, PartEventType.Decoupled, decoupler),
            };
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 2, Mainsail, 0, tree));
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Mainsail, 0, tree));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Poodle, 0, tree));
        }

        [Fact]
        public void ExpectedPower_DecoupledWithNullTree_ZeroesOnlyTheRootPid()
        {
            var events = new List<PartEvent>
            {
                Ev(1, PartEventType.EngineIgnited, Mainsail, 1f),
                Ev(2, PartEventType.EngineIgnited, Poodle, 1f),
                Ev(3, PartEventType.Decoupled, Mainsail),
            };
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Mainsail, 0, null));
            Assert.Equal(1f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Poodle, 0, null));
        }

        [Fact]
        public void ExpectedPower_Destroyed_ZeroesOnlyThatPart_AndALaterIgnitionRelights()
        {
            var events = new List<PartEvent>
            {
                Ev(1, PartEventType.EngineIgnited, Mainsail, 1f),
                Ev(2, PartEventType.EngineIgnited, Poodle, 0.5f),
                Ev(3, PartEventType.Destroyed, Mainsail),
                Ev(4, PartEventType.EngineIgnited, Mainsail, 0.7f),
            };
            Assert.Equal(0f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Mainsail, 0, null));
            Assert.Equal(0.5f, GhostFxIntegrity.ExpectedEnginePower(events, 3, Poodle, 0, null));
            // Mirrors the applier: events apply in order, whatever came before.
            Assert.Equal(0.7f, GhostFxIntegrity.ExpectedEnginePower(events, 4, Mainsail, 0, null));
        }

        [Fact]
        public void HasAnyEngineEvent_CountsAllThreeEngineTypesOnly()
        {
            Assert.False(GhostFxIntegrity.HasAnyEngineEvent(null));
            Assert.False(GhostFxIntegrity.HasAnyEngineEvent(new List<PartEvent>
                { Ev(1, PartEventType.Decoupled, 1u), Ev(2, PartEventType.Destroyed, 2u) }));
            Assert.True(GhostFxIntegrity.HasAnyEngineEvent(new List<PartEvent>
                { Ev(1, PartEventType.EngineShutdown, 1u) }));
            Assert.True(GhostFxIntegrity.HasAnyEngineEvent(new List<PartEvent>
                { Ev(1, PartEventType.EngineThrottle, 1u, 0.3f) }));
            Assert.True(GhostFxIntegrity.HasAnyEngineEvent(new List<PartEvent>
                { Ev(1, PartEventType.EngineIgnited, 1u, 1f) }));
        }

        // ---- JudgeEngine -----------------------------------------------------------

        [Fact]
        public void Judge_PowerDisagreement_IsPowerMismatch_EvenWhenHidden()
        {
            Assert.Equal(GhostFxIntegrity.EngineVerdict.PowerMismatch,
                GhostFxIntegrity.JudgeEngine(1f, 0f, false, 2, 0));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.PowerMismatch,
                GhostFxIntegrity.JudgeEngine(0f, 0.5f, true, 2, 2));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(0.5f, 0.50001f, true, 2, 2));
        }

        [Fact]
        public void Judge_Hidden_DoesNotJudgeThePlume()
        {
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(1f, 1f, false, 3, 0));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(0f, 0f, false, 3, 3));
        }

        [Fact]
        public void Judge_Visible_PlumeMustMatchPower()
        {
            Assert.Equal(GhostFxIntegrity.EngineVerdict.DarkWhileOn,
                GhostFxIntegrity.JudgeEngine(1f, 1f, true, 3, 0));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.LitWhileOff,
                GhostFxIntegrity.JudgeEngine(0f, 0f, true, 3, 1));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(1f, 1f, true, 3, 1));
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(0f, 0f, true, 3, 0));
            // An engine with no plume pieces at all cannot be dark.
            Assert.Equal(GhostFxIntegrity.EngineVerdict.Ok,
                GhostFxIntegrity.JudgeEngine(1f, 1f, true, 0, 0));
        }

        // ---- CountOwnership --------------------------------------------------------

        private static GhostFxIntegrity.FxSystemRef Ref(string ghost, uint pid, int id,
            bool destroyed = false, bool underOwn = true)
        {
            return new GhostFxIntegrity.FxSystemRef
            {
                GhostKey = ghost,
                PartPid = pid,
                InstanceId = id,
                Destroyed = destroyed,
                UnderOwnGhost = underOwn,
            };
        }

        [Fact]
        public void Ownership_CleanSet_CountsSystems()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 10), Ref("#1", Poodle, 11), Ref("#2", Mainsail, 12),
            });
            Assert.True(c.Clean);
            Assert.Equal(3, c.Systems);
            Assert.Empty(c.Problems);
        }

        [Fact]
        public void Ownership_NullOrEmpty_IsClean()
        {
            Assert.True(GhostFxIntegrity.CountOwnership(null).Clean);
            Assert.True(GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>()).Clean);
        }

        [Fact]
        public void Ownership_DestroyedReference_IsAnOrphan()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 0, destroyed: true), Ref("#1", Poodle, 0, destroyed: true),
            });
            Assert.False(c.Clean);
            Assert.Equal(2, c.Destroyed);
            // Destroyed entries share id 0 but never count as duplicates of each other.
            Assert.Equal(0, c.Duplicates);
            Assert.Contains(c.Problems, p => p.Contains("destroyed system on ghost=#1 pid=1592830016"));
        }

        [Fact]
        public void Ownership_SystemOffItsGhostRoot_IsForeign()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 10, underOwn: false),
            });
            Assert.False(c.Clean);
            Assert.Equal(1, c.ForeignRoot);
            Assert.Contains(c.Problems, p => p.Contains("off its ghost root"));
        }

        [Fact]
        public void Ownership_OneSystemTwoGhosts_IsADuplicate()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 10), Ref("#2", Poodle, 10),
            });
            Assert.False(c.Clean);
            Assert.Equal(1, c.Duplicates);
            Assert.Contains(c.Problems, p => p.Contains("shared by ghost=#1") && p.Contains("ghost=#2"));
        }

        [Fact]
        public void Ownership_OneSystemTwoPartsOfOneGhost_IsADuplicate()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 10), Ref("#1", Mainsail, 10),
            });
            Assert.Equal(1, c.Duplicates);
        }

        [Fact]
        public void Ownership_TwoModulesOfOnePartSharingASystem_IsCountedNotFailed()
        {
            var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
            {
                Ref("#1", Poodle, 10), Ref("#1", Poodle, 10),
            });
            Assert.True(c.Clean);
            Assert.Equal(1, c.SharedWithinPart);
        }

        [Fact]
        public void Ownership_ProblemListIsBounded()
        {
            var refs = new List<GhostFxIntegrity.FxSystemRef>();
            for (int i = 0; i < 25; i++) refs.Add(Ref("#1", (uint)i, 0, destroyed: true));
            var c = GhostFxIntegrity.CountOwnership(refs);
            Assert.Equal(25, c.Destroyed);
            Assert.Equal(10, c.Problems.Count);
        }

        // ---- recording-level checks -----------------------------------------------

        [Fact]
        public void SharedRecordingIds_ReportsEachRepeatOnce()
        {
            var shared = GhostFxIntegrity.SharedRecordingIds(new List<string>
                { "a", "b", "a", null, "", "a", "c", "b" });
            Assert.Equal(new List<string> { "a", "b" }, shared);
            Assert.Empty(GhostFxIntegrity.SharedRecordingIds(null));
        }

        [Fact]
        public void LiveInactive_NamesEachInactiveLiveId_WithItsReason()
        {
            var inactive = new Dictionary<string, TimelineInactiveReason>
            {
                { "tip", TimelineInactiveReason.SupersededByRelation },
                { "retired", TimelineInactiveReason.RewindRetired },
                { "active", TimelineInactiveReason.None },
            };
            var hits = GhostFxIntegrity.LiveInactive(
                new List<string> { "fork", "tip", "active", null, "retired" }, inactive);
            Assert.Equal(new List<string> { "tip(SupersededByRelation)", "retired(RewindRetired)" }, hits);
            Assert.Empty(GhostFxIntegrity.LiveInactive(null, inactive));
            Assert.Empty(GhostFxIntegrity.LiveInactive(new List<string> { "tip" }, null));
        }

        [Fact]
        public void InBothSamples_KeepsOnlyNamesSeenTwice()
        {
            var both = GhostFxIntegrity.InBothSamples(
                new List<string> { "Parsek_Timeline_3@1", "Parsek_Timeline_4@2", null },
                new List<string> { "Parsek_Timeline_4@2", "Parsek_Timeline_9@5" });
            Assert.Equal(new List<string> { "Parsek_Timeline_4@2" }, both);
            Assert.Empty(GhostFxIntegrity.InBothSamples(null, new List<string> { "x" }));
        }

        // ---- formatters (the lane pins these lines) --------------------------------

        [Fact]
        public void Summaries_AreCultureInvariant_AndCarryThePinnedKeys()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "engine-fx vs recording: scene=FLIGHT frames=30 ghosts=2 engines=8 judged=240 ok=240 " +
                    "bad=0 lit=30 dark=180 fxHidden=30 unsupported=0",
                    GhostFxIntegrity.FormatEngineSummary("FLIGHT", 30, 2, 8, 240, 240, 0, 30, 180, 30, 0));

                var c = GhostFxIntegrity.CountOwnership(new List<GhostFxIntegrity.FxSystemRef>
                    { Ref("#1", Poodle, 10), Ref("#1", Poodle, 10) });
                Assert.Equal(
                    "fx ownership: scene=FLIGHT ghosts=1 systems=2 destroyed=0 foreignRoot=0 " +
                    "duplicates=0 sharedWithinPart=1 sharedRecordings=0 unownedGhostRoots=0",
                    GhostFxIntegrity.FormatOwnershipSummary("FLIGHT", 1, c, 0, 0));
                Assert.Contains("systems=0", GhostFxIntegrity.FormatOwnershipSummary(null, 0, null, 0, 0));

                Assert.Equal(
                    "inactive recordings: scene=FLIGHT ghosts=3 supersedeRows=1 inactiveIds=2 " +
                    "replayingInactive=0 indexMismatch=0",
                    GhostFxIntegrity.FormatSupersedeSummary("FLIGHT", 3, 1, 2, 0, 0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
