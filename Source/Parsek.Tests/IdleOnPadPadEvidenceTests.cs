using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Idle-on-pad needs evidence of a pad, not only a short distance from launch. A recording
    /// with a single trajectory sample reads maxDist 0 by construction, so before this rule an
    /// orbiting one-sample recording exited to the Space Center was auto-discarded as "idle on
    /// pad" (IDLE-ON-PAD-READS-ONE-SAMPLE-ORBITING-RECORDING-AS-IDLE).
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

        private static Recording OneSample(
            string startSituation, string launchSite = null, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = "rec",
                VesselName = "Probe",
                StartSituation = startSituation,
                LaunchSiteName = launchSite,
                TerminalStateValue = terminal,
                MaxDistanceFromLaunch = 0.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 80000.0 });
            return rec;
        }

        private static RecordingTree TreeOf(Recording rec)
        {
            return new RecordingTree
            {
                Id = "tree-pad-evidence",
                TreeName = "Probe",
                Recordings = new Dictionary<string, Recording> { { rec.RecordingId, rec } }
            };
        }

        private static TerminalState? ParseTerminal(string name)
        {
            return string.IsNullOrEmpty(name)
                ? (TerminalState?)null
                : (TerminalState)Enum.Parse(typeof(TerminalState), name);
        }

        // ------------------------------------------------------------
        //  The red-before-fix case: one sample, orbiting, no pad
        // ------------------------------------------------------------

        [Fact]
        public void IsIdleOnPad_OneSampleOrbitingStartNoSite_ReturnsFalse()
        {
            Recording rec = OneSample("Orbiting");

            Assert.False(ParsekFlight.IsIdleOnPad(rec));
        }

        [Fact]
        public void IsTreeIdleOnPad_OneSampleOrbitingStartNoSite_ReturnsFalse()
        {
            RecordingTree tree = TreeOf(OneSample("Orbiting", terminal: TerminalState.Orbiting));

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsIdleOnPad: rec='rec' within 30m but no pad evidence")
                && l.Contains("startSituation=Orbiting"));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: 'Probe' maxDist=0.0m"));
        }

        [Theory]
        [InlineData("Flying")]
        [InlineData("Sub-orbital")]
        [InlineData("SUB_ORBITAL")]
        [InlineData("Escaping")]
        [InlineData("Orbiting")]
        public void IsIdleOnPad_OneSampleAirborneStartNoSiteNoTerminal_ReturnsFalse(string startSituation)
        {
            // No pad evidence: the vessel was off the ground and nothing says it came back down.
            Assert.False(ParsekFlight.IsIdleOnPad(OneSample(startSituation)));
        }

        [Fact]
        public void IsIdleOnPad_FizzledLaunchOneSampleFlyingStartWithLaunchSite_StaysIdle()
        {
            // A normal launch recording starts "Flying" with the site set (the auto-record
            // trigger fires when the vessel leaves PRELAUNCH). A fizzled launch that never got
            // 30 m off the pad is the idle-on-pad case the discard exists for: the site wins.
            Assert.True(ParsekFlight.IsIdleOnPad(OneSample("Flying", launchSite: "LaunchPad")));
        }

        [Fact]
        public void IsTreeIdleOnPad_FizzledLaunchMultiSampleFlyingStartWithLaunchSite_StaysIdle()
        {
            var rec = new Recording
            {
                RecordingId = "rec",
                VesselName = "Probe",
                StartSituation = "Flying",
                LaunchSiteName = "LaunchPad",
                MaxDistanceFromLaunch = 4.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120.0, bodyName = "Kerbin" });

            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsIdleOnPad_OneSampleOrbitingStartLandedTerminal_FallsThroughToTerminalEvidence()
        {
            // The airborne start is no veto: with no site, the Landed terminal is the evidence.
            Assert.True(ParsekFlight.IsIdleOnPad(OneSample("Orbiting", terminal: TerminalState.Landed)));
        }

        [Fact]
        public void IsIdleOnPad_ToppledPadDropOverrideWithFlyingStart_ReturnsFalse()
        {
            // The pad-drop override is part of the distance rule, so it needs pad evidence too.
            var rec = new Recording { RecordingId = "rec", StartSituation = "Flying", MaxDistanceFromLaunch = 35.0 };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 10.0 });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 105.0, bodyName = "Kerbin", latitude = MetersToLatDegrees(5.0), altitude = 0.0
            });

            Assert.False(ParsekFlight.IsIdleOnPad(rec));
        }

        // ------------------------------------------------------------
        //  Pad evidence present: idle stays idle
        // ------------------------------------------------------------

        [Fact]
        public void IsIdleOnPad_OneSamplePrelaunchStart_ReturnsTrue()
        {
            Recording rec = OneSample("Prelaunch");

            Assert.True(ParsekFlight.IsIdleOnPad(rec));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsIdleOnPad_OneSampleAtLaunchSite_ReturnsTrue()
        {
            Recording rec = OneSample("Prelaunch", launchSite: "LaunchPad");

            Assert.True(ParsekFlight.IsIdleOnPad(rec));
            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsIdleOnPad_OneSampleLandedStart_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsIdleOnPad(OneSample("Landed")));
        }

        [Fact]
        public void IsIdleOnPad_OneSampleSplashedStart_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsIdleOnPad(OneSample("Splashed")));
        }

        [Fact]
        public void IsIdleOnPad_OneSampleLandedTerminalNoStartSituation_ReturnsTrue()
        {
            // A legacy recording with no start situation still reads its surface terminal.
            Assert.True(ParsekFlight.IsIdleOnPad(OneSample(null, terminal: TerminalState.Landed)));
        }

        [Fact]
        public void IsIdleOnPad_MultiSampleLandedWithinThirtyMetres_ReturnsTrue()
        {
            // Unchanged: a landed vessel that never left the pad, with real trajectory samples.
            var rec = new Recording
            {
                RecordingId = "rec",
                VesselName = "Probe",
                StartSituation = "Landed",
                TerminalStateValue = TerminalState.Landed,
                MaxDistanceFromLaunch = 5.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120.0, bodyName = "Kerbin" });

            Assert.True(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsTreeIdleOnPad_MultiSampleFlyingWithinThirtyMetresNoEvidence_ReturnsFalse()
        {
            // Within 30 m, but a flying start with no site and no surface terminal: not a pad sit.
            var rec = new Recording
            {
                RecordingId = "rec",
                VesselName = "Probe",
                StartSituation = "Flying",
                MaxDistanceFromLaunch = 10.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120.0, bodyName = "Kerbin" });

            Assert.False(ParsekFlight.IsTreeIdleOnPad(TreeOf(rec)));
        }

        [Fact]
        public void IsIdleOnPad_NullRecording_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsIdleOnPad((Recording)null));
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
            Assert.True(ParsekFlight.HasPadEvidence(OneSample("Prelaunch")));
            Assert.False(ParsekFlight.HasPadEvidence(OneSample("Orbiting")));
            Assert.False(ParsekFlight.HasPadEvidence((Recording)null));
        }
    }
}
