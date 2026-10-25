using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A flight in which a kerbal the committed timeline holds steps out on EVA is never
    /// idle-on-pad or a pad failure, even when nothing moved 30 m: only its recordings can end
    /// the earlier mission's hold, and stock has already moved the kerbal (todo
    /// CX4-IDLE-DISCARDED-FLIGHT-LEAVES-THE-HOLD-OPEN). The live walk is
    /// <c>CrewAcrossVesselsHandoffTests.IdleVisitWhoseEvaTakesAHeldKerbalOff_IsNotIdle</c>.
    /// </summary>
    [Collection("Sequential")]
    public class HeldKerbalEvaNotIdleTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private readonly List<string> logLines = new List<string>();

        public HeldKerbalEvaNotIdleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekFlight.HeldKerbalProbeForTesting = null;
        }

        public void Dispose()
        {
            ParsekFlight.HeldKerbalProbeForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Rec(string id, string name, string startSituation, string evaCrew,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = name,
                StartSituation = startSituation,
                EvaCrewName = evaCrew,
                MaxDistanceFromLaunch = 3.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" });
            return rec;
        }

        // Landed capsule + the EVA of evaCrew off it, both idle; durations below / above the
        // pad-failure threshold.
        private static RecordingTree CapsuleVisit(string evaCrew, bool shortFlight)
        {
            double end = shortFlight ? 104.0 : 160.0;
            var tree = new RecordingTree
            {
                Id = "tree-visit",
                TreeName = "Capsule visit",
                Recordings = new Dictionary<string, Recording>(),
            };
            tree.Recordings["capsule"] = Rec("capsule", "Capsule", "Landed", null, 100.0, end);
            tree.Recordings["eva"] = Rec("eva", evaCrew, "Landed", evaCrew, 102.0, end);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-eva",
                UT = 102.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { "capsule" },
                ChildRecordingIds = new List<string> { "eva" },
            });
            return tree;
        }

        [Fact]
        public void IsTreeIdleOnPad_EvaOfFreeKerbal_StaysIdle()
        {
            ParsekFlight.HeldKerbalProbeForTesting = name => false;

            Assert.True(ParsekFlight.IsTreeIdleOnPad(CapsuleVisit(Jeb, shortFlight: false)));
            Assert.DoesNotContain(logLines, l => l.Contains("left his vessel on EVA"));
        }

        // catches: the idle-on-pad scene-exit discard throwing away the only flight that ends
        // another mission's open hold on the kerbal it took off his vessel.
        [Fact]
        public void IsTreeIdleOnPad_EvaOfHeldKerbal_NotIdle()
        {
            var asked = new List<string>();
            ParsekFlight.HeldKerbalProbeForTesting = name => { asked.Add(name); return name == Jeb; };

            Assert.False(ParsekFlight.IsTreeIdleOnPad(CapsuleVisit(Jeb, shortFlight: false)));
            Assert.Equal(new[] { Jeb }, asked);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - held kerbal 'Jebediah Kerman' left his vessel on EVA (recording eva)")
                && l.Contains("tree='Capsule visit'"));
        }

        [Fact]
        public void IsTreePadFailure_ShortVisitWithEvaOfFreeKerbal_IsPadFailure()
        {
            ParsekFlight.HeldKerbalProbeForTesting = name => false;

            Assert.True(ParsekFlight.IsTreePadFailure(CapsuleVisit(Jeb, shortFlight: true)));
        }

        // catches: the same discard through the pad-failure rule (every recording under 10 s).
        [Fact]
        public void IsTreePadFailure_ShortVisitWithEvaOfHeldKerbal_NotPadFailure()
        {
            ParsekFlight.HeldKerbalProbeForTesting = name => name == Jeb;

            Assert.False(ParsekFlight.IsTreePadFailure(CapsuleVisit(Jeb, shortFlight: true)));
            Assert.Contains(logLines, l =>
                l.Contains("IsTreePadFailure: not idle - held kerbal 'Jebediah Kerman' left his vessel on EVA"));
        }

        [Fact]
        public void FindEvaOfHeldKerbal_IgnoresNonEvaRecordingsAndNulls()
        {
            RecordingTree tree = CapsuleVisit(Jeb, shortFlight: false);
            tree.Recordings["eva"].EvaCrewName = null;

            Assert.Null(ParsekFlight.FindEvaOfHeldKerbal(tree, name => true));
            Assert.Null(ParsekFlight.FindEvaOfHeldKerbal(null, name => true));
            Assert.Null(ParsekFlight.FindEvaOfHeldKerbal(CapsuleVisit(Jeb, false), null));
        }
    }
}
