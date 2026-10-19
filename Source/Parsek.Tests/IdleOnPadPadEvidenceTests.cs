using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Idle-on-pad needs evidence of a pad somewhere in the flight, not only a short distance
    /// from launch. A recording with a single trajectory sample reads maxDist 0 by construction,
    /// so before this rule an orbiting one-sample flight exited to the Space Center was
    /// auto-discarded as "idle on pad" (IDLE-ON-PAD-READS-ONE-SAMPLE-ORBITING-RECORDING-AS-IDLE).
    /// The evidence is a TREE-level requirement: StartSituation and LaunchSiteName are only set
    /// on the active recording, so an EVA kerbal or a background-only child carries neither.
    /// </summary>
    [Collection("Sequential")]
    public class IdleOnPadPadEvidenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public IdleOnPadPadEvidenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static double MetersToLatDegrees(double meters)
        {
            return meters / 600000.0 * 180.0 / Math.PI;
        }

        private static Recording Rec(
            string id, string startSituation, string launchSite = null, TerminalState? terminal = null,
            double maxDistance = 0.0, int points = 1)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Probe",
                StartSituation = startSituation,
                LaunchSiteName = launchSite,
                TerminalStateValue = terminal,
                MaxDistanceFromLaunch = maxDistance
            };
            for (int i = 0; i < points; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 100.0 + 10.0 * i, bodyName = "Kerbin", altitude = 80000.0 });
            return rec;
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree-pad-evidence",
                TreeName = "Probe",
                Recordings = new Dictionary<string, Recording>()
            };
            foreach (Recording rec in recs)
                tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private static TerminalState? ParseTerminal(string name)
        {
            return string.IsNullOrEmpty(name)
                ? (TerminalState?)null
                : (TerminalState)Enum.Parse(typeof(TerminalState), name);
        }

        // ------------------------------------------------------------
        //  The per-recording distance rule is unchanged (origin/main)
        // ------------------------------------------------------------

        [Fact]
        public void IsIdleOnPad_Recording_IsTheDistanceRuleOnly()
        {
            // No evidence on the recording itself: the recording-level rule is distance only.
            // The evidence requirement is tree-level (IsTreeIdleOnPad / IsActiveTreeIdleOnPad).
            Assert.True(ParsekFlight.IsIdleOnPad(Rec("rec", null, maxDistance: 0.0)));
            Assert.False(ParsekFlight.IsIdleOnPad(Rec("rec", "Orbiting", maxDistance: 50.0)));
        }

        [Fact]
        public void IsIdleOnPad_NullRecording_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsIdleOnPad((Recording)null));
        }

        // ------------------------------------------------------------
        //  Tree-level: the red-before-fix case and the real idle shapes
        // ------------------------------------------------------------

        [Fact]
        public void IsTreeIdleOnPad_OneSampleOrbitingSingleRecording_NotIdle()
        {
            RecordingTree tree = TreeOf(Rec("rec", "Orbiting", terminal: TerminalState.Orbiting));

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: tree='Probe' id=tree-pad-evidence pad evidence=none"));
        }

        [Fact]
        public void IsTreeIdleOnPad_FizzledLaunchOneSampleFlyingWithLaunchSite_StaysIdle()
        {
            // A normal launch recording starts "Flying" with the site set (the auto-record
            // trigger fires when the vessel leaves PRELAUNCH). A fizzled launch that never got
            // 30 m off the pad is the idle-on-pad case the discard exists for.
            RecordingTree tree = TreeOf(Rec("rec", "Flying", launchSite: "LaunchPad"));

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("IsTreeIdleOnPad: tree='Probe'")
                && l.Contains("pad evidence=launch-site"));
        }

        [Fact]
        public void IsTreeIdleOnPad_EvaBoardTreeRootCarriesEvidence_StaysIdle()
        {
            // The real shape of a pad flight that sent a kerbal out and back in: the root is the
            // launch recording; the EVA kerbal and the continuation carry no launch situation.
            RecordingTree tree = TreeOf(
                Rec("root", "Prelaunch", launchSite: "LaunchPad", maxDistance: 2.0, points: 3),
                Rec("eva", "EVA", terminal: TerminalState.Boarded, maxDistance: 5.0, points: 2),
                Rec("cont", null, terminal: TerminalState.Boarded, maxDistance: 0.0, points: 1));

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("IsTreeIdleOnPad: tree='Probe'")
                && l.Contains("pad evidence=launch-site"));
        }

        [Fact]
        public void IsTreeIdleOnPad_PadExplosionDestroyedDebrisWithoutSituation_StaysIdle()
        {
            RecordingTree tree = TreeOf(
                Rec("root", "Prelaunch", launchSite: "LaunchPad", maxDistance: 1.0, points: 2),
                Rec("debris1", null, terminal: TerminalState.Destroyed, maxDistance: 3.0, points: 2),
                Rec("debris2", null, terminal: TerminalState.Destroyed, maxDistance: 0.0, points: 1));

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void IsTreeIdleOnPad_EvidenceOnlyOnAChild_StaysIdle()
        {
            // At least one recording in the tree is enough: a landed EVA kerbal shows the pad.
            RecordingTree tree = TreeOf(
                Rec("root", null, maxDistance: 2.0, points: 2),
                Rec("eva", "EVA", terminal: TerminalState.Landed, maxDistance: 4.0, points: 2));

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void IsTreeIdleOnPad_NoRecordingShowsEvidence_NotIdle()
        {
            // Every recording is within 30 m, but none starts on the ground or ends landed.
            RecordingTree tree = TreeOf(
                Rec("root", "Flying", maxDistance: 2.0, points: 3),
                Rec("eva", "EVA", terminal: TerminalState.Orbiting, maxDistance: 5.0, points: 2),
                Rec("cont", null, terminal: TerminalState.Boarded, maxDistance: 0.0, points: 1));

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("pad evidence=none"));
        }

        [Fact]
        public void IsTreeIdleOnPad_DistanceGuardFiresBeforeTheEvidenceCheck()
        {
            // A recording beyond 30 m is refused by the distance walk; the evidence check never runs.
            var far = Rec("far", null, maxDistance: 100.0, points: 1);
            far.Points.Add(new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin", latitude = 1.0, altitude = 80000.0 });
            RecordingTree tree = TreeOf(
                Rec("root", "Prelaunch", launchSite: "LaunchPad", maxDistance: 2.0, points: 2),
                far);

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("pad evidence="));
        }

        [Fact]
        public void IsTreeIdleOnPad_ToppledDropOverrideWithFlyingStartOnly_NotIdle()
        {
            // The pad-drop override is part of the distance rule; without tree evidence it is not idle.
            var rec = Rec("rec", "Flying", maxDistance: 35.0, points: 0);
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 10.0 });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 105.0, bodyName = "Kerbin", latitude = MetersToLatDegrees(5.0), altitude = 0.0
            });

            Assert.False(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsTreeIdleOnPad_OneSamplePrelaunchSingleRecording_StaysIdle()
        {
            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(Rec("rec", "Prelaunch"))));
        }

        [Fact]
        public void IsTreeIdleOnPad_OneSampleLandedTerminalOnly_StaysIdle()
        {
            // A legacy recording with no start situation still reads its surface terminal.
            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(Rec("rec", null, terminal: TerminalState.Landed))));
        }

        [Fact]
        public void IsTreeIdleOnPad_MultiSampleLandedWithinThirtyMetres_StaysIdle()
        {
            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(
                Rec("rec", "Landed", terminal: TerminalState.Landed, maxDistance: 5.0, points: 3))));
        }

        // ------------------------------------------------------------
        //  Pure decision: TreePadEvidence (first recording with evidence wins)
        // ------------------------------------------------------------

        [Fact]
        public void TreePadEvidence_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(ParsekFlight.TreePadEvidence(null));
            Assert.Null(ParsekFlight.TreePadEvidence(new List<Recording>()));
        }

        [Fact]
        public void TreePadEvidence_NoRecordingWithEvidence_ReturnsNull()
        {
            Assert.Null(ParsekFlight.TreePadEvidence(new[]
            {
                Rec("a", "Orbiting"),
                Rec("b", "EVA", terminal: TerminalState.Orbiting),
                Rec("c", null, terminal: TerminalState.Destroyed)
            }));
        }

        [Fact]
        public void TreePadEvidence_FirstRecordingWithEvidenceWins()
        {
            Assert.Equal("launch-site", ParsekFlight.TreePadEvidence(new[]
            {
                Rec("a", "Orbiting"),
                Rec("b", "Flying", launchSite: "LaunchPad"),
                Rec("c", "Prelaunch")
            }));
        }

        [Fact]
        public void TreePadEvidence_NullEntriesAreSkipped()
        {
            Assert.Equal("start-prelaunch", ParsekFlight.TreePadEvidence(new Recording[]
            {
                null,
                Rec("a", "Prelaunch")
            }));
        }

        [Fact]
        public void TreePadEvidence_TerminalOnlyEvidence_ReturnsTheTerminalReason()
        {
            Assert.Equal("terminal-splashed", ParsekFlight.TreePadEvidence(new[]
            {
                Rec("a", "EVA"),
                Rec("b", null, terminal: TerminalState.Splashed)
            }));
        }

        [Fact]
        public void TreeHasPadEvidenceForIdle_LogsTheTreeAndTheReason()
        {
            RecordingTree tree = TreeOf(Rec("a", "Prelaunch"));

            Assert.True(ParsekFlight.TreeHasPadEvidenceForIdle(tree, "IsTreeIdleOnPad"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: tree='Probe' id=tree-pad-evidence pad evidence=start-prelaunch"));
        }

        // ------------------------------------------------------------
        //  Pure decision truth table: PadEvidenceReason
        // ------------------------------------------------------------

        [Theory]
        [InlineData("Prelaunch", null, null, "start-prelaunch")]
        [InlineData("PRELAUNCH", null, null, "start-prelaunch")]
        [InlineData("Landed", null, null, "start-landed")]
        [InlineData("Splashed", null, null, "start-splashed")]
        [InlineData("Orbiting", null, "Landed", "terminal-landed")]
        [InlineData("Orbiting", null, null, null)]
        [InlineData("Flying", "LaunchPad", null, "launch-site")]
        [InlineData("Sub-orbital", "LaunchPad", "Landed", "launch-site")]
        [InlineData("Prelaunch", "LaunchPad", null, "launch-site")]
        [InlineData("Flying", null, "Landed", "terminal-landed")]
        [InlineData("Flying", null, null, null)]
        [InlineData("Escaping", null, "Splashed", "terminal-splashed")]
        [InlineData("Escaping", null, "Orbiting", null)]
        [InlineData("EVA", null, "Landed", "terminal-landed")]
        [InlineData("EVA", null, null, null)]
        [InlineData("Docked", null, null, null)]
        [InlineData(null, "LaunchPad", null, "launch-site")]
        [InlineData(null, "", null, null)]
        [InlineData(null, null, "Landed", "terminal-landed")]
        [InlineData(null, null, "Splashed", "terminal-splashed")]
        [InlineData(null, null, "Orbiting", null)]
        [InlineData(null, null, "Destroyed", null)]
        [InlineData(null, null, "Recovered", null)]
        [InlineData(null, null, null, null)]
        public void PadEvidenceReason_TruthTable(
            string startSituation, string launchSite, string terminal, string expectedReason)
        {
            string reason = ParsekFlight.PadEvidenceReason(
                startSituation, launchSite, ParseTerminal(terminal));

            Assert.Equal(expectedReason, reason);
            Assert.Equal(expectedReason != null,
                ParsekFlight.HasPadEvidence(startSituation, launchSite, ParseTerminal(terminal)));
        }

        [Fact]
        public void HasPadEvidence_Recording_FollowsTheRecordingFields()
        {
            Assert.True(ParsekFlight.HasPadEvidence(Rec("a", "Prelaunch")));
            Assert.False(ParsekFlight.HasPadEvidence(Rec("a", "Orbiting")));
            Assert.False(ParsekFlight.HasPadEvidence((Recording)null));
        }

        // ------------------------------------------------------------
        //  Source gate: the live path reads the evidence after the flush
        // ------------------------------------------------------------

        [Fact]
        public void IsActiveTreeIdleOnPad_EvidenceCheckRunsAfterTheRecorderFlush()
        {
            // The flush copies the live root's StartSituation / LaunchSiteName onto the tree
            // recording, so the tree-level evidence check must come after it (and after the
            // distance walk, so the existing guards fire first).
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string source = File.ReadAllText(
                Path.Combine(projectRoot, "Source", "Parsek", "ParsekFlight.cs"))
                .Replace("\r\n", "\n");

            int start = source.IndexOf("internal bool IsActiveTreeIdleOnPad()\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "IsActiveTreeIdleOnPad declaration not found");
            int flush = source.IndexOf("FlushRecorderIntoActiveTreeForSerialization();", start, StringComparison.Ordinal);
            int walk = source.IndexOf("foreach (var rec in activeTree.Recordings.Values)", start, StringComparison.Ordinal);
            int evidence = source.IndexOf("TreeHasPadEvidenceForIdle(activeTree, \"IsActiveTreeIdleOnPad\")", start, StringComparison.Ordinal);
            Assert.True(flush > start, "flush not found in IsActiveTreeIdleOnPad");
            Assert.True(walk > flush, "distance walk must run after the recorder flush");
            Assert.True(evidence > walk, "tree-level evidence check must run after the distance walk");
        }
    }
}
