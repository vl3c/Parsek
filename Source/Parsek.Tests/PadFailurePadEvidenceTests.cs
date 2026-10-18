using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pad-failure discards need evidence of a pad, not only the distance/duration thresholds:
    /// a recording with a single trajectory sample has duration 0 and MaxDistanceFromLaunch 0,
    /// so the thresholds alone read an orbiting recording destroyed within one sample interval
    /// as a pad failure and discard it (IDLE-ON-PAD-READS-ONE-SAMPLE-ORBITING-RECORDING-AS-IDLE,
    /// follow-up). Evidence is checked at the level each path has: the tree for
    /// <see cref="ParsekFlight.IsTreePadFailure"/> (EVA and debris children carry no launch
    /// situation), the recording itself for the single-recording destroyed-during-split branch
    /// (<see cref="ParsekFlight.DecideDestroyedSplitRecordingDiscard"/>).
    /// </summary>
    [Collection("Sequential")]
    public class PadFailurePadEvidenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PadFailurePadEvidenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
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
                rec.Points.Add(new TrajectoryPoint { ut = 100.0 + 4.0 * i, bodyName = "Kerbin", altitude = 80000.0 });
            return rec;
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree-pad-failure",
                TreeName = "Probe",
                Recordings = new Dictionary<string, Recording>()
            };
            foreach (Recording rec in recs)
                tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        // ------------------------------------------------------------
        //  IsTreePadFailure: the red-before-fix case and the real pad shapes
        // ------------------------------------------------------------

        [Fact]
        public void IsTreePadFailure_OneSampleOrbitingSingleRecording_NotPadFailure()
        {
            // The bug: duration 0 (one sample) and maxDist 0 pass the thresholds, so without
            // the tree evidence gate an orbiting recording destroyed within one sample
            // interval reads as a pad failure and the whole tree is discarded.
            RecordingTree tree = TreeOf(Rec("rec", "Orbiting", terminal: TerminalState.Destroyed));

            Assert.False(ParsekFlight.IsTreePadFailure(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreePadFailure: tree='Probe' id=tree-pad-failure pad evidence=none"));
        }

        [Fact]
        public void IsTreePadFailure_PadTreeWithDestroyedDebrisChildWithoutSituation_StaysPadFailure()
        {
            // The evidence stays tree-level: a pad explosion's debris children carry no launch
            // situation, but the Prelaunch root still shows the pad, so the tree is discarded.
            RecordingTree tree = TreeOf(
                Rec("root", "Prelaunch", launchSite: "LaunchPad", maxDistance: 2.0, points: 2),
                Rec("debris", null, terminal: TerminalState.Destroyed, maxDistance: 3.0, points: 2));

            Assert.True(ParsekFlight.IsTreePadFailure(tree));
            Assert.Contains(logLines, l => l.Contains("IsTreePadFailure: tree='Probe'")
                && l.Contains("pad evidence=launch-site"));
        }

        [Fact]
        public void IsTreePadFailure_FizzledLaunchFlyingWithLaunchSite_StaysPadFailure()
        {
            // A normal launch recording starts "Flying" with the site set (the auto-record
            // trigger fires when the vessel leaves PRELAUNCH). A fizzled launch that never got
            // 30 m off the pad is the pad-failure case the discard exists for.
            RecordingTree tree = TreeOf(Rec("rec", "Flying", launchSite: "LaunchPad"));

            Assert.True(ParsekFlight.IsTreePadFailure(tree));
        }

        // ------------------------------------------------------------
        //  Pure decision: DecideDestroyedSplitRecordingDiscard
        // ------------------------------------------------------------

        [Fact]
        public void DecideDestroyedSplitRecordingDiscard_OrbitingOneSampleDestroyed_ReturnsNull()
        {
            // Within both thresholds (duration 0, maxDist 0) but no pad evidence: commit.
            Recording rec = Rec("rec", "Orbiting", terminal: TerminalState.Destroyed);

            Assert.Null(ParsekFlight.DecideDestroyedSplitRecordingDiscard(rec));
        }

        [Fact]
        public void DecideDestroyedSplitRecordingDiscard_PrelaunchShortClose_ReturnsPadFailure()
        {
            Recording rec = Rec("rec", "Prelaunch", maxDistance: 5.0, points: 2);

            Assert.Equal("pad failure", ParsekFlight.DecideDestroyedSplitRecordingDiscard(rec));
        }

        [Fact]
        public void DecideDestroyedSplitRecordingDiscard_LandedLongClose_ReturnsIdleOnPad()
        {
            // Lasts over the 10 s pad-failure duration but never left the pad: idle on pad.
            Recording rec = Rec("rec", "Landed", maxDistance: 10.0, points: 8);

            Assert.Equal("idle on pad", ParsekFlight.DecideDestroyedSplitRecordingDiscard(rec));
        }

        [Fact]
        public void DecideDestroyedSplitRecordingDiscard_Null_ReturnsNull()
        {
            Assert.Null(ParsekFlight.DecideDestroyedSplitRecordingDiscard(null));
        }

        [Fact]
        public void DecideDestroyedSplitRecordingDiscard_FarFromLaunch_ReturnsNull()
        {
            // Pad evidence present, but the flight really moved (points over a kilometre
            // away), so neither the distance rule nor the pad-drop override fires.
            var rec = Rec("rec", "Prelaunch", maxDistance: 5000.0, points: 0);
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 80000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 108.0, bodyName = "Kerbin", latitude = 0.1, altitude = 80000.0 });

            Assert.Null(ParsekFlight.DecideDestroyedSplitRecordingDiscard(rec));
        }

        // ------------------------------------------------------------
        //  Source gate: the live destroyed-during-split branch reads the
        //  recording through the pure decision and keeps the no-evidence log
        // ------------------------------------------------------------

        [Fact]
        public void FallbackCommitSplitRecorder_DecisionReadsTheRecordingAndKeepsTheNoEvidenceLog()
        {
            string body = ReadFallbackCommitSplitRecorderBody();

            // Pin the argument that matters: the decision reads THIS branch's recording.
            Assert.Contains("string discardReason = DecideDestroyedSplitRecordingDiscard(rec);", body);
            Assert.Contains("no pad evidence - committing", body);
        }

        // The commit line names the ACTUAL decision for every non-discarded destroyed
        // recording: the decision returned null either because the recording shows no pad
        // evidence or because the thresholds did not match, and the log says which.
        [Fact]
        public void FallbackCommitSplitRecorder_LogsTheCommitReasonForEveryNonDiscardedDestroyedRecording()
        {
            string body = ReadFallbackCommitSplitRecorderBody();

            // One Info line for every destroyed recording that is NOT discarded: the block
            // is gated on the decision alone, never on re-testing the evidence/thresholds.
            Assert.Contains("if (discardReason == null)\n", body);
            Assert.Contains("bool hasPadEvidence = HasPadEvidence(rec);", body);
            // Both outcomes are named on that line: the exact reason tokens and the
            // reason= key they are logged under, plus the no-evidence wording the spec
            // pins for its case.
            Assert.Contains("reason={commitReason}", body);
            Assert.Contains("\"no-pad-evidence\"", body);
            Assert.Contains("\"outside-pad-thresholds\"", body);
            Assert.Contains("no pad evidence - committing", body);
            Assert.Contains("outside pad thresholds - committing", body);

            // Pin WHICH branch carries WHICH literal: the tokens above all being present
            // would stay green if the two ternaries swapped their branches, logging
            // "outside-pad-thresholds" for a recording with no pad evidence and vice
            // versa. In source the true branch (?) is written first in each ternary, so
            // index order inside the span pins the association: condition < true literal
            // < false literal, for the human wording and the reason token alike.
            int whyCond = body.IndexOf(
                "string commitWhy = hasPadEvidence", StringComparison.Ordinal);
            int whyTrue = body.IndexOf(
                "\"outside pad thresholds - committing\"", StringComparison.Ordinal);
            int whyFalse = body.IndexOf(
                "\"no pad evidence - committing\"", StringComparison.Ordinal);
            int reasonCond = body.IndexOf(
                "string commitReason = hasPadEvidence", StringComparison.Ordinal);
            int reasonTrue = body.IndexOf(
                "\"outside-pad-thresholds\"", StringComparison.Ordinal);
            int reasonFalse = body.IndexOf(
                "\"no-pad-evidence\"", StringComparison.Ordinal);
            Assert.True(
                whyCond >= 0 && whyTrue >= 0 && whyFalse >= 0
                && reasonCond >= 0 && reasonTrue >= 0 && reasonFalse >= 0,
                "one of the commit-reason ternaries is gone or renamed - update this gate.");
            Assert.True(whyCond < whyTrue && whyTrue < whyFalse,
                "commitWhy no longer reads `hasPadEvidence ? outside : no evidence` - a " +
                "swapped branch would log the wrong wording for the recording.");
            Assert.True(reasonCond < reasonTrue && reasonTrue < reasonFalse,
                "commitReason no longer reads `hasPadEvidence ? outside : no evidence` - a " +
                "swapped branch would log the wrong reason= token for the recording.");
        }

        /// <summary>
        /// Reads the live FallbackCommitSplitRecorder span out of ParsekFlight.cs
        /// (same walk as the cell above): declaration through the next method. Comments
        /// are stripped first so a needle that also appears in a comment (the guard the
        /// comment documents repeats its words) cannot satisfy these gates; string
        /// literals survive whole, which the literal assertions rely on.
        /// </summary>
        private static string ReadFallbackCommitSplitRecorderBody()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string source = SourceScanText.StripCSharpComments(File.ReadAllText(
                    Path.Combine(projectRoot, "Source", "Parsek", "ParsekFlight.cs")))
                .Replace("\r\n", "\n");

            int start = source.IndexOf("void FallbackCommitSplitRecorder(FlightRecorder splitRec)\n",
                StringComparison.Ordinal);
            Assert.True(start >= 0, "FallbackCommitSplitRecorder declaration not found");
            int end = source.IndexOf("void ResumeSplitRecorder(", start, StringComparison.Ordinal);
            Assert.True(end > start, "FallbackCommitSplitRecorder end marker not found");
            return source.Substring(start, end - start);
        }
    }
}
